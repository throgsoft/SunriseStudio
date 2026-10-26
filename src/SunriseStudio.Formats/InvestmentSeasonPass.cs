namespace SunriseStudio.Formats;

/** One rank step. Unknown4 is populated but not decoded. */
public sealed record InvestmentProgressionStep(int Index, int Offset, int Cost, uint Unknown4);
/** One progression row. The value indices at +8 and +10 are unconfirmed, and bytes +12 to +23 are kept only in Raw. */
public sealed record InvestmentProgression(int Index, int Offset, uint Hash, byte Scope, ushort ValueIndexA, ushort ValueIndexB,
    IReadOnlyList<InvestmentProgressionStep> Steps, IReadOnlyList<InvestmentSeasonPassRow> Rewards, ReadOnlyMemory<byte> Raw, string Error = "")
{
    /** Experience needed to reach rank r: the sum of the first r step costs. The first step costs 0. */
    public long Threshold(int rank) => Steps.Take(Math.Max(0, rank)).Sum(step => (long)step.Cost);
    /** Scope 0 is the account and 1 the character. Other scopes are not replicated (package_progression_build.cpp). */
    public string ScopeLabel => Scope switch { 0 => "Account", 1 => "Character", _ => "Not replicated" };
}
/** One reward row. Its index is the native claim index, so rows are never reordered or dropped. */
public sealed record InvestmentSeasonPassRow(int Progression, int Index, int Offset, uint Rank, uint ItemIndex, uint Quantity,
    uint ClaimSlot, InvestmentExpressionList Conditions, IReadOnlyList<InvestmentSocketOverride> Sockets,
    uint Unknown12, uint Unknown16, InvestmentTableField QuantityField, InvestmentTableField ItemField,
    string Error = "");

/** Progression definitions (root slot 68) with their steps and reward rows (package_season_pass_build.cpp). */
public static class InvestmentSeasonPass
{
    public const uint TableClass = 0x80807CD9, ProgressionClass = 0x80807CDD, StepClass = 0x80807CED, RewardClass = 0x80802FF7;
    /** Sunrise stores a reward's rank in one byte (package_season_pass_build.cpp read_season_reward). */
    public const int MaximumRank = byte.MaxValue;
    internal const int TableArray = 8, ProgressionStride = 88, StepField = 24, StepStride = 8, RewardField = 72, RewardStride = 56,
        RewardRank = 0, RewardItem = 4, RewardQuantity = 8;
    private const int Scope = 4, ValueA = 8, ValueB = 10, RewardUnknown12 = 12, RewardUnknown16 = 16, RewardClaim = 20,
        RewardConditions = 24, RewardSockets = 40;
    /** A claim flag must map to the account flag bank, kind 1 in the flag slot table. Kind 2 is the profile bank. */
    private const ushort AccountFlagKind = 1, ProfileFlagKind = 2;
    private const uint OrOpcode = 3;
    private const int OwnershipLength = 3;

    internal static IReadOnlyList<InvestmentProgression> Read(InvestmentSlotRead slot, int itemCount, InvestmentUnlocks unlocks,
        List<string> diagnostics, CancellationToken token)
    {
        if (!slot.Loaded) { diagnostics.Add("Progressions: " + slot.Diagnostic); return []; }
        var table = new TagReader(slot.Bytes);
        var rows = table.Array(TableArray, ProgressionClass, ProgressionStride);
        var progressions = new InvestmentProgression[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int at = rows.Data + i * ProgressionStride;
            var errors = new List<string>();
            IReadOnlyList<InvestmentProgressionStep> steps = [];
            IReadOnlyList<InvestmentSeasonPassRow> rewards = [];
            try
            {
                var list = table.Array(at + StepField, StepClass, StepStride);
                steps = Enumerable.Range(0, list.Count).Select(step => list.Data + step * StepStride)
                    .Select((p, step) => new InvestmentProgressionStep(step, p, table.I32(p), table.U32(p + 4))).ToArray();
            }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { errors.Add("Steps: " + error.Message); }
            try
            {
                var list = table.Array(at + RewardField, RewardClass, RewardStride);
                rewards = Enumerable.Range(0, list.Count).Select(row => ReadRow(table, slot.Tag, i, row, list.Data + row * RewardStride,
                    itemCount, unlocks)).ToArray();
            }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { errors.Add("Rewards: " + error.Message); }
            progressions[i] = new(i, at, table.U32(at), table.U8(at + Scope), table.U16(at + ValueA), table.U16(at + ValueB), steps, rewards,
                slot.Bytes.Slice(at, ProgressionStride), string.Join(" ", errors));
        }
        return progressions;
    }

    /**
     * Studio's guess at the paid track: conditions that start with an account flag, a profile flag and an or. The data
     * names no tracks, so this is a heuristic. Shared rows are inlined the way Sunrise binds them.
     */
    public static bool LooksLikeOwnership(InvestmentExpressionList conditions, InvestmentUnlocks unlocks)
    {
        var start = new List<InvestmentUnlockInstruction>(OwnershipLength);
        var visiting = new HashSet<uint>();
        void Take(IReadOnlyList<InvestmentUnlockInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (start.Count == OwnershipLength) return;
                if (instruction.Opcode != InvestmentUnlocks.SharedOpcode) { start.Add(instruction); continue; }
                if (!visiting.Add(instruction.Operand)) { start.Add(instruction); continue; }
                Take(unlocks.Shared((int)instruction.Operand).Instructions);
                visiting.Remove(instruction.Operand);
            }
        }
        foreach (var member in conditions.Members) Take(member.Instructions);
        return start.Count == OwnershipLength && Flag(start[0], AccountFlagKind) && Flag(start[1], ProfileFlagKind)
            && start[2].Opcode == OrOpcode;
        bool Flag(InvestmentUnlockInstruction instruction, ushort kind) => instruction.Opcode == InvestmentUnlocks.FlagOpcode
            && instruction.Operand < unlocks.FlagSlots.Count && unlocks.FlagSlots[(int)instruction.Operand].Kind == kind;
    }

    /** Validates a row as Sunrise does, without dropping it. A row Sunrise would refuse keeps its index and carries its Error. */
    private static InvestmentSeasonPassRow ReadRow(TagReader table, uint owner, int progression, int index, int at, int itemCount,
        InvestmentUnlocks unlocks)
    {
        var problems = new List<string>();
        uint rank = table.U32(at + RewardRank), item = table.U32(at + RewardItem), claim = table.U32(at + RewardClaim);
        if (rank > MaximumRank) problems.Add($"Rank {rank} is above {MaximumRank}.");
        if (item >= itemCount) problems.Add($"Item {item} is outside the item index.");
        if (claim != 0 && unlocks.FlagSlots.Count != 0 && (claim >= unlocks.FlagSlots.Count || unlocks.FlagSlots[(int)claim].Kind != AccountFlagKind))
            problems.Add($"Claim flag slot {claim} is not an account flag.");
        var conditions = InvestmentUnlocks.ReadList(table, at + RewardConditions);
        if (conditions.Error.Length != 0) problems.Add("Conditions: " + conditions.Error);
        problems.AddRange(conditions.Members.Where(member => member.Error.Length != 0).Select(member => "Condition: " + member.Error));
        var sockets = InvestmentRewards.ReadSockets(table, at + RewardSockets, itemCount, problems);
        return new(progression, index, at, rank, item, table.U32(at + RewardQuantity), claim, conditions, sockets,
            table.U32(at + RewardUnknown12), table.U32(at + RewardUnknown16),
            new(InvestmentTableFieldKind.PassRewardQuantity, owner, progression, index, table.UInt("Quantity", at + RewardQuantity, true)),
            new(InvestmentTableFieldKind.PassRewardItem, owner, progression, index, table.UInt("Item", at + RewardItem, true)),
            string.Join(" ", problems));
    }
}
