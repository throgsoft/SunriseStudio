using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/**
 * An item's references to other investment tables, read while the catalog loads the item's definition. A reference that
 * cannot be read holds 0xFFFF. SocketLanes holds each socket lane's slot-79 list and slot-84 site, and Previews holds each
 * declared preview slot's item and shared condition row.
 */
internal readonly record struct InvestmentItemLinks(ushort WrapperPool, byte WrapperFlags, ushort QuestOwner, ushort[] Objectives)
{
    private const ushort Absent = ushort.MaxValue;
    public static readonly InvestmentItemLinks None = new(Absent, 0, Absent, []);
    /** The acquisition flag slot at +0xDA, kept only when the item has a readable wrapper block. */
    public ushort AcquiredFlagSlot { get; init; } = Absent;
    /** The same +0xDA flag slot read on every item, as package_reward_build.cpp RewardBuild::read_item reads it. */
    public ushort AcquisitionFlag { get; init; } = Absent;
    public ushort AcquireSite { get; init; } = Absent;
    /** The objective block's two completion operations (root slot 84 rows), then its context and completion value slots. */
    public ushort Primary { get; init; } = Absent;
    public ushort Secondary { get; init; } = Absent;
    public ushort ContextValue { get; init; } = Absent;
    public ushort CompletionValue { get; init; } = Absent;
    /** The chain value slot of the quest set this item owns. */
    public ushort QuestSetValueSlot { get; init; } = Absent;
    public InvestmentPursuitShape Shape { get; init; }
    /** True for a pursuit whose lifetime expression is one row with a positive operand. Sunrise reports it as a bounty. */
    public bool Expires { get; init; }
    public (ushort List, ushort Site)[] SocketLanes { get; init; } = [];
    public (int Slot, ushort Item, ushort Condition)[] Previews { get; init; } = [];

    /** Never throws or reports. A malformed block reads as absent here, and opening the item diagnoses it. */
    public static InvestmentItemLinks Read(TagReader item)
    {
        int Block(int field, uint expectedClass)
        {
            try { return item.Block(field, expectedClass); }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { return -1; }
        }
        var links = None;
        int wrapper = Block(InvestmentRewards.WrapperField, InvestmentRewards.WrapperClass);
        if (wrapper >= 0 && item.Has(wrapper, InvestmentRewards.WrapperFlags + sizeof(uint)))
            links = links with { WrapperPool = item.U16(wrapper), WrapperFlags = item.U8(wrapper + InvestmentRewards.WrapperFlags),
                AcquiredFlagSlot = Field(item, InvestmentRewards.AcquiredFlagField) };
        links = links with { AcquireSite = Field(item, InvestmentRewards.AcquireSiteField),
            AcquisitionFlag = Field(item, InvestmentRewards.AcquiredFlagField) };
        int objectives = Block(InvestmentPursuits.ObjectiveField, InvestmentPursuits.ObjectiveBlockClass);
        if (objectives >= 0 && item.Has(objectives, InvestmentPursuits.QuestOwner + sizeof(ushort)))
        {
            links = links with { QuestOwner = item.U16(objectives + InvestmentPursuits.QuestOwner),
                Primary = item.U16(objectives + InvestmentPursuits.PrimaryReference),
                Secondary = item.U16(objectives + InvestmentPursuits.SecondaryReference),
                ContextValue = item.U16(objectives + InvestmentPursuits.ContextValue),
                CompletionValue = item.U16(objectives + InvestmentPursuits.CompletionValue), Expires = Expiring(item) };
            if (item.TryArray(objectives, InvestmentPursuits.ObjectiveListClass, InvestmentPursuits.ObjectiveReferenceStride, out var list))
                links = links with { Objectives = Enumerable.Range(0, list.Count)
                    .Select(row => item.U16(list.Data + row * InvestmentPursuits.ObjectiveReferenceStride)).ToArray() };
        }
        int set = Block(InvestmentPursuits.QuestSetField, InvestmentPursuits.QuestSetClass);
        // The set is read only when the item and set meet quest_initialization_reader.cpp's minimum sizes.
        if (set >= 0 && item.Length >= InvestmentPursuits.MinimumQuestOwner && item.Has(set, InvestmentPursuits.MinimumQuestSet))
            links = links with { QuestSetValueSlot = item.U16(set + InvestmentPursuits.QuestSetValueSlot) };
        int sockets = Block(InvestmentCatalog.SocketField, InvestmentCatalog.SocketBlockClass);
        if (sockets >= 0 && item.TryArray(sockets, InvestmentCatalog.SocketRowClass, InvestmentCatalog.SocketStride, out var lanes))
            links = links with { SocketLanes = Enumerable.Range(0, lanes.Count).Select(lane => lanes.Data + lane * InvestmentCatalog.SocketStride)
                .Select(at => (item.U16(at + InvestmentRewards.SocketLaneList), item.U16(at + InvestmentRewards.SocketLaneSite))).ToArray() };
        int preview = Block(InvestmentPursuits.PreviewField, InvestmentPursuits.PreviewClass);
        if (preview >= 0 && item.Has(preview, InvestmentPursuits.PreviewHeader + InvestmentPursuits.PreviewSlots * InvestmentPursuits.PreviewStride))
            links = links with { Previews = PreviewRows(item, preview) };
        return links with { Shape = InvestmentPursuits.Shape(objectives >= 0, links.QuestOwner, set >= 0) };
    }

    private static ushort Field(TagReader item, int field) => item.Has(field, sizeof(ushort)) ? item.U16(field) : Absent;

    /**
     * Sunrise reads a pursuit's lifetime as the operand of its one expression row, whatever the opcode, and reports a held
     * pursuit with a positive lifetime as a bounty (item_definition_reader.cpp read_lifetime, studio_investment_payloads.cpp
     * held_pursuit).
     */
    private static bool Expiring(TagReader item) =>
        InvestmentUnlocks.ReadExpression(item, InvestmentPursuits.LifetimeField).Instructions is [var only] && only.Operand is > 0 and <= int.MaxValue;

    /** The declared preview slots. An item or condition index past the u16 range reads as 0xFFFF. */
    private static (int Slot, ushort Item, ushort Condition)[] PreviewRows(TagReader item, int block)
    {
        var rows = new (int, ushort, ushort)[Math.Min(item.U32(block), (uint)InvestmentPursuits.PreviewSlots)];
        for (int slot = 0; slot < rows.Length; slot++)
        {
            int at = block + InvestmentPursuits.PreviewHeader + slot * InvestmentPursuits.PreviewStride;
            rows[slot] = (slot, (ushort)Math.Min(item.U32(at), Absent), (ushort)Math.Min(item.U32(at + InvestmentPursuits.PreviewCondition), Absent));
        }
        return rows;
    }
}

/**
 * What an objective's progress reads once Sunrise binds its program: a program Sunrise cannot bind, one per-item value, one
 * flag, one value, one class flag, several instructions, or one other instruction such as a constant.
 */
public enum InvestmentProgressKind { Unreadable, ItemValue, Flag, Value, ClassFlag, Several, Other }
/** Instruction is the program's only instruction, or default when the kind is Unreadable or Several. */
public readonly record struct InvestmentProgressSource(InvestmentProgressKind Kind, InvestmentUnlockInstruction Instruction);

/** The root-slot tables of one catalog. A table that cannot be read leaves a diagnostic and an empty list. */
public sealed partial class InvestmentTables
{
    /** Slot 84's table class and row size, as package_pursuit_build.cpp reads it. Studio reads only its row count. */
    public const uint Slot84Class = 0x808078BB;
    public const int Slot84Stride = 0x120;
    private const int TableArray = 8;
    /** The value-slot kind that holds a per-item value (package_reward_build.cpp kItemValueKind). */
    private const byte ItemValueKind = 4;
    /** Diagnostics name at most this many reward pool or shared expression cycles, so many cycles do not bury other problems. */
    internal const int ListedCycles = 8;
    public const string Slot84Note = "Reward pool entries name its rows as supplemental rewards, and pursuit objective blocks name "
        + "them as completion operations. Neither Sunrise nor Studio decodes its rows.";
    private readonly Dictionary<int, List<int>> objectiveUsers;
    private readonly Dictionary<byte, InvestmentBucket> bucketsById = [];
    private readonly Dictionary<byte, int[]> itemsByBucket;
    private readonly Dictionary<int, List<(int Progression, int Row)>> passRowsByPool = [];
    private readonly InvestmentItemLinks[] links;
    private readonly string[] objectiveText;
    private readonly int slot84Rows;
    public InvestmentRoot? Root { get; }
    public InvestmentUnlocks Unlocks { get; }
    public IReadOnlyList<InvestmentRewardPool> Pools { get; }
    public InvestmentRewardGraph RewardGraph { get; }
    public IReadOnlyList<InvestmentProgression> Progressions { get; }
    /** Progressions with at least one reward row. Any of them may be the pass Sunrise serves, so Studio does not pick one. */
    public IReadOnlyList<InvestmentProgression> PassCandidates { get; }
    public IReadOnlyList<InvestmentObjective> Objectives { get; }
    public InvestmentSlotRead Slot84 { get; }
    /** Bucket descriptors in table order, or none when the table cannot be read. */
    public IReadOnlyList<InvestmentBucket> Buckets { get; }
    /** Why Sunrise would refuse the whole bucket table. Studio lists the rows either way. */
    public IReadOnlyList<string> BucketTableProblems { get; }
    public uint BucketTableTag => TableTag(InvestmentRootSlots.Buckets);
    /** The bucket table's entry class, which Sunrise does not check, or 0 when the table is not installed. */
    public uint BucketTableClass { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    /** Who references each reward site and saved unlock row, and who gives each item. */
    public InvestmentReferences References { get; }

    private InvestmentTables(InvestmentRoot? root, InvestmentUnlocks unlocks, IReadOnlyList<InvestmentRewardPool> pools,
        IReadOnlyList<InvestmentProgression> progressions, IReadOnlyList<InvestmentObjective> objectives, InvestmentSlotRead slot84,
        int slot84Rows, InvestmentItemLinks[] links, List<string> diagnostics, IReadOnlyList<InvestmentItem> items,
        IReadOnlyList<InvestmentBucket> buckets, IReadOnlyList<string> bucketProblems, uint bucketClass, string[] objectiveText,
        CancellationToken token)
    {
        Root = root; Unlocks = unlocks; Pools = pools; Progressions = progressions; Objectives = objectives; this.objectiveText = objectiveText;
        Slot84 = slot84; this.slot84Rows = slot84Rows;
        Buckets = buckets; BucketTableProblems = bucketProblems; BucketTableClass = bucketClass;
        foreach (var bucket in buckets) bucketsById.TryAdd(bucket.Id, bucket);
        itemsByBucket = items.GroupBy(item => item.Bucket).ToDictionary(group => group.Key, group => group
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Index).Select(item => item.Index).ToArray());
        PassCandidates = progressions.Where(row => row.Rewards.Count != 0).ToArray();
        RewardGraph = InvestmentRewardGraph.Build(pools, links.Select(row => row.WrapperPool).ToArray(),
            links.Select(row => (uint)row.WrapperFlags).ToArray());
        this.links = links;
        objectiveUsers = new();
        for (int item = 0; item < links.Length; item++)
            foreach (ushort objective in links[item].Objectives.Distinct()) Add(objectiveUsers, objective, item);
        foreach (var ladder in PassCandidates)
            foreach (var row in ladder.Rewards)
                if (WrapperPool((int)Math.Min(row.ItemIndex, int.MaxValue)) is >= 0 and var pool) Add(passRowsByPool, pool, (ladder.Index, row.Index));
        References = InvestmentReferences.Build(this, links, token);
        diagnostics.AddRange(unlocks.GraphDiagnostics);
        foreach (var cycle in RewardGraph.Cycles.Take(ListedCycles)) diagnostics.Add($"Reward pools contain themselves: {string.Join(", ", cycle)}.");
        Diagnostics = diagnostics;
    }

    public uint TableTag(int slot) => Root is not null && slot >= 0 && slot < Root.SlotCount ? Root.SlotTag(slot) : 0;
    /** The first descriptor of a bucket id, or null when the installed table has none. */
    public InvestmentBucket? Bucket(byte id) => bucketsById.GetValueOrDefault(id);
    /** Installed items whose definition names the bucket, sorted by name. */
    public IReadOnlyList<int> ItemsInBucket(byte id) => itemsByBucket.GetValueOrDefault(id) ?? [];
    /** The objective's progress description, or "" when the install gives none. */
    public string ObjectiveText(int objective) => objective >= 0 && objective < objectiveText.Length ? objectiveText[objective] : "";
    /** Items whose objective block lists `objective`. */
    public IReadOnlyList<int> ObjectiveUsers(int objective) => objectiveUsers.GetValueOrDefault(objective) ?? [];
    /** The quest owner the item's objective block names, or -1. */
    public int QuestOwner(int item) => item >= 0 && item < links.Length && links[item].QuestOwner != ushort.MaxValue ? links[item].QuestOwner : -1;
    /** Which pursuit page the item belongs on (InvestmentPursuits.Classify), read once at load. */
    public InvestmentPursuitShape PursuitShape(int item) => item >= 0 && item < links.Length ? links[item].Shape : InvestmentPursuitShape.None;
    /** True when Sunrise would report the held item as a bounty: a pursuit with a positive installed lifetime. */
    public bool Expires(int item) => item >= 0 && item < links.Length && links[item].Expires;
    /** Pass candidate rows whose item's wrapper opens the pool. */
    public IReadOnlyList<(int Progression, int Row)> PassRowsOfPool(int pool) => passRowsByPool.GetValueOrDefault(pool) ?? [];
    /** The item's wrapper pool, or -1 when the item has no readable wrapper. */
    public int WrapperPool(int item) => item >= 0 && item < links.Length && links[item].WrapperPool != ushort.MaxValue ? links[item].WrapperPool : -1;
    /** The flag slot the item's wrapper sets once acquired, or 0xFFFF when it names none. */
    public ushort AcquiredFlagSlot(int item) => item >= 0 && item < links.Length ? links[item].AcquiredFlagSlot : ushort.MaxValue;
    /** The flag slot any item names at +0xDA (package_reward_build.cpp RewardBuild::read_item), or 0xFFFF. */
    public ushort AcquisitionFlag(int item) => item >= 0 && item < links.Length ? links[item].AcquisitionFlag : ushort.MaxValue;
    /** True when the item's wrapper opens as soon as it is acquired; false without a wrapper. */
    public bool OpensOnAcquisition(int item) => WrapperPool(item) >= 0 && (links[item].WrapperFlags & 1) != 0;

    /** A slot-84 row's label. It commits to neither reading of the table, since Slot84Note explains both. */
    public string Slot84Label(ushort row) => Slot84.State switch
    {
        InvestmentSlotState.Omitted => $"Site {row}; this installation does not ship root slot 84.",
        InvestmentSlotState.Loaded => row < slot84Rows ? $"Slot 84 row {row}" : $"Slot 84 row {row}, outside the table",
        _ => $"Slot 84 row {row}; {Slot84.Diagnostic}"
    };

    /**
     * The progress lane an objective fills: its one instruction reads a per-item value that the pursuit's instance values
     * list at that lane. The join is inferred, so the UI marks it as possibly inaccurate.
     * @return The lane, or -1 when the objective does not read one per-item value the pursuit lists.
     */
    public int ProgressLane(InvestmentObjective objective, InvestmentPursuit pursuit)
    {
        if (objective.Source.Instructions is not [var read] || !ItemValueRead(read)) return -1;
        for (int lane = 0; lane < pursuit.InstanceValues.Count; lane++) if (pursuit.InstanceValues[lane] == read.Operand) return lane;
        return -1;
    }

    /**
     * What an objective's progress reads once shared rows are inlined, as package_objective_build.cpp read_objective and
     * package_reward_build.cpp RewardConditions::append_expression bind it. A missing, unreadable or empty shared row,
     * nesting at InvestmentUnlocks.ServerNestingLimit, a cycle, an opcode Sunrise does not evaluate or a slot outside its
     * table leaves the program Unreadable. The pursuit's instance values are not consulted, since Sunrise's item-progress
     * rule does not read them either.
     */
    public InvestmentProgressSource ProgressSource(InvestmentObjective objective)
    {
        int count = 0;
        InvestmentUnlockInstruction only = default;
        var visiting = new HashSet<uint>();
        bool Append(InvestmentExpression expression, int depth)
        {
            if (depth >= InvestmentUnlocks.ServerNestingLimit || expression.Error.Length != 0) return false;
            foreach (var instruction in expression.Instructions)
            {
                if (instruction.Opcode == InvestmentUnlocks.SharedOpcode)
                {
                    int before = count;
                    // Sunrise has no cycle check of its own, so a cycle nests until the depth limit refuses it. Shared
                    // returns an error for an operand outside the table, including one too large for an int.
                    if (!visiting.Add(instruction.Operand) || !Append(Unlocks.Shared((int)instruction.Operand), depth + 1)
                        || count == before) return false;
                    visiting.Remove(instruction.Operand);
                    continue;
                }
                if (!Binds(instruction)) return false;
                if (count++ == 0) only = instruction;
            }
            return true;
        }
        if (!Append(objective.Source, 0) || count == 0) return new(InvestmentProgressKind.Unreadable, default);
        if (count > 1) return new(InvestmentProgressKind.Several, default);
        var kind = only.Opcode switch
        {
            InvestmentUnlocks.FlagOpcode => Unlocks.ClassOf(only) >= 0 ? InvestmentProgressKind.ClassFlag : InvestmentProgressKind.Flag,
            InvestmentUnlocks.ValueOpcode => ItemValueRead(only) ? InvestmentProgressKind.ItemValue : InvestmentProgressKind.Value,
            _ => InvestmentProgressKind.Other
        };
        return new(kind, only);
    }

    /**
     * True when a value read is per-item progress as Sunrise binds it: a slot of the per-item value kind that neither the account
     * nor the character value mapping table names, so the read stays external (package_objective_build.cpp read_objective,
     * package_reward_build.cpp RewardConditions::bind).
     */
    private bool ItemValueRead(InvestmentUnlockInstruction read) => read.Opcode == InvestmentUnlocks.ValueOpcode
        && read.Operand < Unlocks.ValueSlots.Count && Unlocks.ValueSlots[(int)read.Operand].Kind == ItemValueKind
        && !Unlocks.BoundValue(read.Operand);

    /** Sunrise binds a flag or value read only to a slot its table holds, and any other instruction only when it evaluates it. */
    private bool Binds(InvestmentUnlockInstruction instruction) => instruction.Opcode switch
    {
        InvestmentUnlocks.FlagOpcode => instruction.Operand < Unlocks.FlagSlots.Count,
        InvestmentUnlocks.ValueOpcode => instruction.Operand < Unlocks.ValueSlots.Count,
        _ => InvestmentUnlocks.Evaluated(instruction.Opcode)
    };

    /**
     * Reads the root, unlocks, pools, progressions, objectives, objective text, buckets and slot 84 in that order, then
     * builds the reference indexes over what loaded. A section that fails leaves a diagnostic and does not stop the next.
     * @param text Resolves a (bank, string hash) pair through the catalog's string banks.
     */
    internal static InvestmentTables Read(PackageCatalog catalog, InvestmentSources sources, IReadOnlyList<InvestmentItem> items,
        InvestmentItemLinks[] links, Func<uint, uint, string> text, CancellationToken token)
    {
        var diagnostics = new List<string>();
        token.ThrowIfCancellationRequested();
        var root = InvestmentRoot.Locate(catalog, sources.Root, out string located);
        var omitted = new InvestmentSlotRead(InvestmentRootSlots.Slot84, 0, InvestmentSlotState.Missing, default, "The investment root is unavailable.");
        if (root is null)
        {
            diagnostics.Add(located);
            return new(null, InvestmentUnlocks.Empty, [], [], [], omitted, 0, links, diagnostics, items, [], [], 0, [], token);
        }
        T Section<T>(string section, T fallback, Func<T> read)
        {
            token.ThrowIfCancellationRequested();
            try { return read(); }
            catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { diagnostics.Add($"{section}: {error.Message}"); return fallback; }
        }
        if (Section("Investment root", 0u, () => root.SlotTag(InvestmentRootSlots.Items)) != sources.Items)
            diagnostics.Add("The item index differs from the investment root's item table; reward item names may be wrong.");
        var unlocks = Section("Unlock slots", InvestmentUnlocks.Empty, () => InvestmentUnlocks.Read(root, catalog, items, diagnostics, token));
        var pools = Section<IReadOnlyList<InvestmentRewardPool>>("Reward pools", [], () => InvestmentRewards.Read(
            root.ReadSlot(catalog, InvestmentRootSlots.Pools, InvestmentRewards.TableClass), items.Count, diagnostics, token));
        var progressions = Section<IReadOnlyList<InvestmentProgression>>("Progressions", [], () => InvestmentSeasonPass.Read(
            root.ReadSlot(catalog, InvestmentRootSlots.Progressions, InvestmentSeasonPass.TableClass), items.Count, unlocks, diagnostics, token));
        var objectives = Section<IReadOnlyList<InvestmentObjective>>("Objectives", [], () => InvestmentPursuits.ReadObjectives(
            root.ReadSlot(catalog, InvestmentRootSlots.Objectives, InvestmentPursuits.ObjectiveTableClass), diagnostics, token));
        var objectiveText = Section<string[]>("Objective text", [], () => InvestmentObjectiveText.Read(catalog, sources.ObjectiveDisplays,
            objectives, text, diagnostics, token));
        var bucketProblems = new List<string>();
        var buckets = Section<IReadOnlyList<InvestmentBucket>>("Buckets", [], () => InvestmentBuckets.Read(
            root.ReadSlotAnyClass(catalog, InvestmentRootSlots.Buckets), bucketProblems, token));
        uint bucketClass = InvestmentRootSlots.Buckets < root.SlotCount
            && catalog.TryGetEntry(root.SlotTag(InvestmentRootSlots.Buckets), out var bucketEntry) ? bucketEntry.Reference : 0;
        var slot84 = Section("Slot 84", omitted, () => root.ReadSlot(catalog, InvestmentRootSlots.Slot84, Slot84Class));
        int slot84Rows = slot84.Loaded ? Section("Slot 84", 0, () => new TagReader(slot84.Bytes).Array(TableArray, stride: Slot84Stride).Count) : 0;
        return new(root, unlocks, pools, progressions, objectives, slot84, slot84Rows, links, diagnostics, items, buckets,
            bucketProblems, bucketClass, objectiveText, token);
    }

    private static void Add<T>(Dictionary<int, List<T>> index, int key, T value)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(value);
    }
}

public sealed partial class InvestmentCatalog
{
    private readonly object tablesLock = new();
    private InvestmentTables? tables;
    public bool TablesLoaded => Volatile.Read(ref tables) is not null;

    /** Loads the root-slot tables once per catalog, on the caller's worker. A cancelled load caches nothing. */
    public InvestmentTables Tables(CancellationToken token = default)
    {
        if (Volatile.Read(ref tables) is { } loaded) return loaded;
        var built = InvestmentTables.Read(catalog, sources, Items, links, Text, token);
        lock (tablesLock) return tables ??= built;
    }

    /**
     * Only an item with an objective block loads the tables. A quest chain reads its owner's definition,
     * which is this item's own bytes when the item owns its set.
     */
    private InvestmentPursuit? ReadPursuit(InvestmentItem item, TagReader data, CancellationToken token)
    {
        if (data.Block(InvestmentPursuits.ObjectiveField, InvestmentPursuits.ObjectiveBlockClass) < 0) return null;
        return InvestmentPursuits.ReadPursuit(data, item, Tables(token), Items.Count, index => index == item.Index ? data : ReadDefinition(index));
    }

    /** The quest set an item owns, for an owner without objectives of its own. A set that cannot be read carries its Error. */
    public InvestmentQuestSet ReadQuestSet(int owner, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (owner < 0 || owner >= Items.Count || owner >= ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(owner), "The quest owner is outside the item index.");
        return InvestmentPursuits.ReadQuestSet((ushort)owner, ReadDefinition(owner), Items.Count);
    }

    /** An item's definition bytes, or null when they cannot be read as an item. */
    private TagReader? ReadDefinition(int index)
    {
        try { RequireClass(catalog, Items[index].Tag, ItemClass); return new(catalog.Read(Items[index].Tag)); }
        catch (Exception error) when (Recoverable(error)) { return null; }
    }
}
