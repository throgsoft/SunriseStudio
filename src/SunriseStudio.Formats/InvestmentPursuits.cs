namespace SunriseStudio.Formats;

/** One objective row of root slot 58. Raw keeps the whole row, including bytes Studio does not decode. */
public sealed record InvestmentObjective(int Index, int Offset, uint Hash, InvestmentExpression Source, bool Optional, bool AllowNegative,
    bool AllowChangeWhenCompleted, bool CountsDown, int CompletionValue, InvestmentExpression TargetAdjustment,
    InvestmentTableField CompletionField, ReadOnlyMemory<byte> Raw, string Error = "");
/** A slot-84 or value-slot reference with its display label. 0xFFFF reads as "None". */
public sealed record InvestmentReference(ushort Index, string Label);
/** A reward the item shows on its pursuit screen. Sunrise does not grant from this list. */
public sealed record InvestmentRewardPreview(int Slot, ushort ItemIndex, int Quantity, ushort ConditionRow, InvestmentExpression? Condition);
/** Quest is an item in or owning a quest chain. Single is an item with objectives outside any chain. */
public enum InvestmentPursuitShape : byte { None, Quest, Single }
/** One quest step. Value identifies the step and is not a progress count. */
public sealed record InvestmentQuestStep(int Member, int Value, ushort ItemIndex);
public sealed record InvestmentQuestSet(ushort OwnerItem, int Block, ushort ValueSlot, byte Mode, IReadOnlyList<InvestmentQuestStep> Steps, string Error = "")
{
    /** Only mode 1 is decoded, as in quest_initialization_reader.cpp kSupportedQuestSetMode. */
    public bool ModeDecoded => Mode == 1;
}
/** An item's objective block and the blocks read beside it. Diagnostics note what Sunrise would refuse. */
public sealed record InvestmentPursuit(int Block, IReadOnlyList<ushort> Objectives, InvestmentReference Primary, InvestmentReference Secondary,
    InvestmentReference ContextValue, InvestmentReference CompletionValue, bool Suppress, bool RequireAll, bool RequireNonOptional,
    bool Automatic, ushort QuestOwner, IReadOnlyList<InvestmentRewardPreview> Previews, int? LifetimeSeconds,
    InvestmentExpression? Lifetime, IReadOnlyList<ushort> PresenceFlags, IReadOnlyList<ushort> InstanceValues,
    InvestmentQuestSet? Chain, IReadOnlyList<string> Diagnostics);

/** Reads objectives, pursuit objective blocks, quest sets, reward previews and lifetimes. */
public static class InvestmentPursuits
{
    public const uint ObjectiveTableClass = 0x8080775B, ObjectiveClass = 0x8080775F, ObjectiveBlockClass = 0x808077EB,
        ObjectiveListClass = 0x808087B1, QuestSetClass = 0x808077C8, QuestMemberClass = 0x808077CA, ValueBlockClass = 0x808077AB,
        PresenceFlagClass = 0x80807D4B, ValueSourceClass = 0x808077AE, PreviewClass = 0x80807875;
    /** Sunrise's pursuit reader accepts at most 7 objectives (items.h kObjectiveCapacity). Some installed items list 8. */
    public const int ServerObjectiveLimit = 7;
    public const byte PursuitBucket = SunriseStudio.Packages.InvestmentItemKinds.PursuitBucket;
    internal const int TableArray = 8, ObjectiveStride = 0xA0, ObjectiveCompletion = 0x30, ObjectiveField = 0x30, QuestSetField = 0x60,
        PreviewField = 0x98, QuestOwner = 0x1C, ObjectiveReferenceStride = 2;
    private const int ObjectiveSource = 8, ObjectiveAdjustment = 0x38, ValueBlockField = 0x90;
    internal const int LifetimeField = 192;
    /** Objective block references: completion operations into root slot 84, then context and completion value slots. */
    internal const int PrimaryReference = 0x10, SecondaryReference = 0x12, ContextValue = 0x14, CompletionValue = 0x1A;
    /**
     * Objective row control bytes: optional, allow negative, allow change when completed, counts down. Sunrise reads only
     * the optional and counts-down bytes, and a value above 1 in either refuses the whole objective table
     * (package_objective_build.cpp read_completion_controls).
     */
    private const int OptionalControl = 0x28, AllowNegativeControl = 0x2A, AllowChangeControl = 0x2B, CountsDownControl = 0x2C;
    private const int CompletionControls = 0x16, CompletionControlCount = 4;
    internal const int QuestSetValueSlot = 0x10, QuestSetMode = 0x1C, MemberStride = 8, MemberItem = 4, MemberReserved = 6;
    /** Minimum quest owner and quest-set block sizes (quest_initialization_reader.cpp kMinimumQuest*Size). */
    internal const int MinimumQuestOwner = 0xF0, MinimumQuestSet = 0x20;
    /** Previews hold a count, then six slots of {u32 item, i32 quantity, u32 shared condition row}. */
    internal const int PreviewSlots = 6, PreviewHeader = 4, PreviewStride = 12, PreviewCondition = 8;
    private const int InstanceSources = 0x20, ValueSourceStride = 8;
    private const ushort Absent = 0xFFFF;

    /**
     * The pursuit page an item belongs on, and its bucket as InvestmentCatalog reads it (255 when unreadable). Never throws.
     * Quest means an objective block naming a quest owner, or an owned quest set. Single means an objective block and neither.
     */
    public static (InvestmentPursuitShape Shape, byte Bucket) Classify(ReadOnlyMemory<byte> item)
    {
        var reader = new TagReader(item);
        return (InvestmentItemLinks.Read(reader).Shape, InvestmentCatalog.ReadBucket(reader));
    }

    internal static InvestmentPursuitShape Shape(bool objectives, ushort questOwner, bool ownsQuestSet) =>
        (objectives && questOwner != Absent) || ownsQuestSet ? InvestmentPursuitShape.Quest
            : objectives ? InvestmentPursuitShape.Single : InvestmentPursuitShape.None;

    /** A row whose optional or counts-down byte is above 1, or whose expression is unreadable, carries an Error and stays listed. */
    internal static IReadOnlyList<InvestmentObjective> ReadObjectives(InvestmentSlotRead slot, List<string> diagnostics, CancellationToken token)
    {
        if (!slot.Loaded) { diagnostics.Add("Objectives: " + slot.Diagnostic); return []; }
        var table = new TagReader(slot.Bytes);
        var rows = table.Array(TableArray, ObjectiveClass, ObjectiveStride);
        var objectives = new InvestmentObjective[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            int at = rows.Data + i * ObjectiveStride;
            byte optional = table.U8(at + OptionalControl), countsDown = table.U8(at + CountsDownControl);
            var source = InvestmentUnlocks.ReadExpression(table, at + ObjectiveSource);
            var adjustment = InvestmentUnlocks.ReadExpression(table, at + ObjectiveAdjustment);
            var problems = new List<string>();
            if (optional > 1 || countsDown > 1) problems.Add("The optional or counts-down byte is above 1, so Sunrise refuses the whole objective table.");
            if (source.Error.Length != 0) problems.Add("Source: " + source.Error);
            if (adjustment.Error.Length != 0) problems.Add("Target adjustment: " + adjustment.Error);
            objectives[i] = new(i, at, table.U32(at), source, optional != 0, table.U8(at + AllowNegativeControl) != 0,
                table.U8(at + AllowChangeControl) != 0, countsDown != 0, table.I32(at + ObjectiveCompletion), adjustment,
                new(InvestmentTableFieldKind.ObjectiveCompletionValue, slot.Tag, i, -1, table.Int("Completion value", at + ObjectiveCompletion, true)),
                slot.Bytes.Slice(at, ObjectiveStride), string.Join(" ", problems));
        }
        return objectives;
    }

    /**
     * @param readItem Reads another item's definition by index, or null when it cannot be read.
     * @return The item's pursuit, or null when it has no objective block.
     */
    internal static InvestmentPursuit? ReadPursuit(TagReader item, InvestmentItem summary, InvestmentTables tables, int itemCount,
        Func<int, TagReader?> readItem)
    {
        int block = item.Block(ObjectiveField, ObjectiveBlockClass);
        if (block < 0) return null;
        if (!item.Has(block, QuestOwner + sizeof(ushort))) throw new InvalidDataException("Objective block is truncated.");
        var diagnostics = new List<string>();
        var list = item.Array(block, ObjectiveListClass, ObjectiveReferenceStride);
        var objectives = Enumerable.Range(0, list.Count).Select(row => item.U16(list.Data + row * ObjectiveReferenceStride)).ToArray();
        if (summary.Bucket == PursuitBucket && objectives.Length > ServerObjectiveLimit)
            diagnostics.Add($"Sunrise reads at most {ServerObjectiveLimit} objectives on a pursuit.");
        foreach (ushort index in objectives.Where(index => tables.Objectives.Count != 0 && index >= tables.Objectives.Count).Distinct())
            diagnostics.Add($"Objective {index} is outside the objective table.");
        var controls = new bool[CompletionControlCount];
        for (int i = 0; i < CompletionControlCount; i++)
        {
            byte value = item.U8(block + CompletionControls + i);
            if (value > 1) diagnostics.Add($"Completion byte +0x{CompletionControls + i:X} holds {value}; Sunrise accepts only 0 or 1.");
            controls[i] = value != 0;
        }
        InvestmentReference Slot84(int field)
        { ushort index = item.U16(block + field); return new(index, index == Absent ? "None" : tables.Slot84Label(index)); }
        InvestmentReference Value(int field)
        { ushort index = item.U16(block + field); return new(index, index == Absent ? "None" : tables.Unlocks.ValueLabel(index).Text); }
        IReadOnlyList<InvestmentRewardPreview> previews = [];
        try { previews = ReadPreviews(item, tables.Unlocks, itemCount, diagnostics); }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { diagnostics.Add("Reward preview: " + error.Message); }
        // Other buckets reuse the lifetime field for other arrays, so only a pursuit reports a bad one.
        var lifetime = InvestmentUnlocks.ReadExpression(item, LifetimeField);
        int? seconds = null;
        if (lifetime.Error.Length != 0)
        {
            if (summary.Bucket == PursuitBucket) diagnostics.Add("Lifetime: " + lifetime.Error);
            lifetime = lifetime with { Error = "" };
        }
        else if (lifetime.Instructions is [{ Opcode: InvestmentUnlocks.ConstantOpcode } constant])
        {
            seconds = (int)constant.Operand;
            if (seconds < 0) diagnostics.Add("Lifetime is negative.");
        }
        IReadOnlyList<ushort> presence = [], instances = [];
        try
        {
            int values = item.Block(ValueBlockField, ValueBlockClass);
            if (values >= 0)
            {
                var flags = item.Array(values, PresenceFlagClass, sizeof(ushort));
                presence = Enumerable.Range(0, flags.Count).Select(row => item.U16(flags.Data + row * sizeof(ushort))).ToArray();
                var sources = item.Array(values + InstanceSources, ValueSourceClass, ValueSourceStride);
                instances = Enumerable.Range(0, sources.Count).Select(row => item.U16(sources.Data + row * ValueSourceStride)).ToArray();
            }
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { diagnostics.Add("Item values: " + error.Message); }
        ushort owner = item.U16(block + QuestOwner);
        InvestmentQuestSet? chain = null;
        if (owner != Absent && owner >= itemCount) diagnostics.Add($"Quest owner {owner} is outside the item index.");
        else if (owner != Absent) chain = ReadQuestSet(owner, readItem(owner), itemCount);
        return new(block, objectives, Slot84(PrimaryReference), Slot84(SecondaryReference), Value(ContextValue), Value(CompletionValue),
            controls[0], controls[1], controls[2], controls[3], owner, previews, seconds, lifetime.IsEmpty ? null : lifetime,
            presence, instances, chain, diagnostics);
    }

    /** Lists the declared slots even when the block breaks a preview rule, and adds a diagnostic for each break. */
    private static IReadOnlyList<InvestmentRewardPreview> ReadPreviews(TagReader item, InvestmentUnlocks unlocks, int itemCount, List<string> diagnostics)
    {
        int block = item.Block(PreviewField, PreviewClass);
        if (block < 0) return [];
        if (!item.Has(block, PreviewHeader + PreviewSlots * PreviewStride)) { diagnostics.Add("Reward preview is truncated."); return []; }
        uint count = item.U32(block);
        if (count > PreviewSlots) diagnostics.Add($"Reward preview declares {count} slots; Sunrise reads at most {PreviewSlots}.");
        var previews = new List<InvestmentRewardPreview>();
        for (int i = 0; i < PreviewSlots; i++)
        {
            int at = block + PreviewHeader + i * PreviewStride;
            uint reward = item.U32(at), condition = item.U32(at + PreviewCondition); int quantity = item.I32(at + 4);
            if (i >= count)
            {
                if (reward != Absent || quantity != 0 || condition != Absent) diagnostics.Add($"Unused reward preview slot {i} is not empty.");
                continue;
            }
            if (reward >= Absent) diagnostics.Add($"Reward preview slot {i} names no item.");
            else if (reward >= itemCount) diagnostics.Add($"Reward preview slot {i} names item {reward}, outside the item index.");
            if (quantity < 0) diagnostics.Add($"Reward preview slot {i} has a negative quantity.");
            if (condition > Absent) diagnostics.Add($"Reward preview slot {i} names condition row {condition}, outside the shared rows.");
            previews.Add(new(i, (ushort)Math.Min(reward, Absent), quantity, (ushort)Math.Min(condition, Absent),
                condition < Absent ? unlocks.Shared((int)condition) : null));
        }
        return previews;
    }

    /** The owner's quest-set block, read with the quest_initialization_reader.cpp layout. Steps keep their authored order. */
    internal static InvestmentQuestSet ReadQuestSet(ushort owner, TagReader? data, int itemCount)
    {
        InvestmentQuestSet Unreadable(string error) => new(owner, -1, Absent, 0, [], error);
        if (data is null) return Unreadable("The quest owner could not be read.");
        if (data.Length < MinimumQuestOwner) return Unreadable("The quest owner is too short to hold a quest set.");
        try
        {
            int set = data.Block(QuestSetField, QuestSetClass);
            if (set < 0) return Unreadable("The quest owner has no quest set.");
            if (!data.Has(set, MinimumQuestSet)) return Unreadable("The quest set is truncated.");
            var members = data.Array(set, QuestMemberClass, MemberStride);
            var problems = new List<string>();
            var steps = new InvestmentQuestStep[members.Count];
            for (int i = 0; i < members.Count; i++)
            {
                int at = members.Data + i * MemberStride;
                steps[i] = new(i, data.I32(at), data.U16(at + MemberItem));
                if (data.U16(at + MemberReserved) != 0) problems.Add($"Quest step {i} has nonzero reserved bytes.");
                if (steps[i].ItemIndex >= itemCount) problems.Add($"Quest step {i} names item {steps[i].ItemIndex}, outside the item index.");
            }
            return new(owner, set, data.U16(set + QuestSetValueSlot), data.U8(set + QuestSetMode), steps, string.Join(" ", problems));
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { return Unreadable(error.Message); }
    }
}
