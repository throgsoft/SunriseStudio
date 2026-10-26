using System.Buffers.Binary;

namespace SunriseStudio.Formats;

public sealed record InvestmentSocketOverride(int Offset, ushort SocketType, ushort PlugItem, ushort PlugSet, ushort RollSet, uint Selection)
{
    public bool FixedPlug => Selection == 0xFFFFFFFF;
}
/** The first modifier whose condition holds replaces the entry weight. A negative modifier value keeps the base weight. */
public sealed record InvestmentWeightModifier(int Offset, InvestmentExpression Condition, ushort ValueIndex, float Value);
/** One pool entry. Offset is absolute in the pool table, and Raw covers the whole row. */
public sealed record InvestmentRewardEntry(int Pool, int Index, int Offset, ushort ItemIndex, uint Quantity, ushort NestedPool,
    ushort Slot84Reference, uint Category, float Weight, InvestmentExpression Condition,
    IReadOnlyList<InvestmentWeightModifier> Modifiers, IReadOnlyList<InvestmentSocketOverride> Sockets,
    InvestmentTableField WeightField, InvestmentTableField QuantityField, ReadOnlyMemory<byte> Raw, string Error = "")
{
    /** Entry bytes Studio does not decode, shown raw: the u16 at +2, the eight bytes at +12 and the u32 at +28. */
    public ushort Unknown2 => BinaryPrimitives.ReadUInt16LittleEndian(Raw.Span[2..]);
    public ulong Unknown12 => BinaryPrimitives.ReadUInt64LittleEndian(Raw.Span[12..]);
    public uint Unknown28 => BinaryPrimitives.ReadUInt32LittleEndian(Raw.Span[28..]);
    public bool AnyCategory => Category == InvestmentRewards.AnyCategory;
}
public sealed record InvestmentRewardPool(int Index, int Offset, uint Hash, IReadOnlyList<InvestmentRewardEntry> Entries, string Error = "");
/** A wrapper selection draws Count entries from Category. Unknown8 is populated but not decoded. */
public sealed record InvestmentRewardSelection(uint Category, uint Count, uint Unknown8);
/** An item's wrapper block, which names its reward pool. The item opens when acquired only when flag bit 0 is set. */
public sealed record InvestmentRewardWrapper(int Block, ushort Pool, uint Flags, uint Unknown4,
    IReadOnlyList<InvestmentRewardSelection> Selections, ushort AcquiredFlagSlot, string Error = "")
{
    public bool OpensOnAcquisition => (Flags & 1) != 0;
}

/** Reward pools (root slot 88), their entries, and the wrapper blocks items carry (package_reward_build.cpp). */
public static class InvestmentRewards
{
    public const uint TableClass = 0x80807553, PoolClass = 0x8080748C, EntryClass = 0x8080748E, ModifierClass = 0x80807490,
        SocketClass = 0x80803062, WrapperClass = 0x808077CC, SelectionClass = 0x808077CF;
    /** An entry in this category matches every selection (reward_resolver.cpp kEmptyTag). */
    public const uint AnyCategory = 0x811C9DC5;
    /** The most selections Sunrise accepts on one wrapper (rewards/definition.h kSelectionCapacity). */
    public const int SelectionLimit = 4;
    /** Nested pools, automatic wrappers and conditions share Sunrise's traversal depth (rewards/definition.h kTraversalDepth). */
    public const int ServerTraversalDepth = InvestmentUnlocks.ServerNestingLimit;
    internal const int TableArray = 8, PoolStride = 24, PoolEntries = 8, EntryStride = 80, EntryQuantity = 4, EntryWeight = 24;
    internal const int WrapperField = 0x58, WrapperFlags = 24, AcquiredFlagField = 0xDA;
    /** Read as the acquire reward site index. No Sunrise reader confirms it, so its UI is marked possibly inaccurate. */
    internal const int AcquireSiteField = 0xD8;
    /** Socket lane +4 and +6 read as indices into root slots 79 and 84. No Sunrise reader confirms them. */
    internal const int SocketLaneList = 4, SocketLaneSite = 6;
    private const int EntryNestedPool = 8, EntrySlot84 = 10, EntryCategory = 20, EntryCondition = 32, EntryModifiers = 48,
        EntrySockets = 64, ModifierStride = 24, ModifierValueIndex = 16, ModifierValue = 20, SocketStride = 12,
        WrapperUnknown = 4, WrapperSelections = 8, SelectionStride = 12;
    private const ushort Absent = 0xFFFF;

    /** Every pool row stays at its native index. A bad row keeps its place and carries its Error. */
    internal static IReadOnlyList<InvestmentRewardPool> Read(InvestmentSlotRead slot, int itemCount, List<string> diagnostics,
        CancellationToken token)
    {
        if (!slot.Loaded) { diagnostics.Add("Reward pools: " + slot.Diagnostic); return []; }
        var table = new TagReader(slot.Bytes);
        var rows = table.Array(TableArray, PoolClass, PoolStride);
        var pools = new InvestmentRewardPool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            if ((i & 63) == 0) token.ThrowIfCancellationRequested();
            int at = rows.Data + i * PoolStride; uint hash = table.U32(at);
            string error = hash == 0 ? "Pool has no definition hash." : "";
            IReadOnlyList<InvestmentRewardEntry> entries = [];
            try
            {
                var members = table.Array(at + PoolEntries, EntryClass, EntryStride);
                entries = Enumerable.Range(0, members.Count).Select(entry => ReadEntry(table, slot, i, entry,
                    members.Data + entry * EntryStride, itemCount, rows.Count)).ToArray();
            }
            catch (Exception failure) when (InvestmentCatalog.Recoverable(failure)) { error = (error + " " + failure.Message).Trim(); }
            pools[i] = new(i, at, hash, entries, error);
        }
        return pools;
    }

    /** Reads each sub-array independently, so one bad array does not hide the others. */
    private static InvestmentRewardEntry ReadEntry(TagReader table, InvestmentSlotRead slot, int pool, int index, int at,
        int itemCount, int poolCount)
    {
        var problems = new List<string>();
        ushort item = table.U16(at), nested = table.U16(at + EntryNestedPool);
        float weight = BitConverter.Int32BitsToSingle(table.I32(at + EntryWeight));
        if (!float.IsFinite(weight) || weight < 0) problems.Add("Weight is negative or not finite.");
        if (item != Absent && item >= itemCount) problems.Add($"Item {item} is outside the item index.");
        if (nested != Absent && nested >= poolCount) problems.Add($"Nested pool {nested} is outside the pool table.");
        var condition = InvestmentUnlocks.ReadExpression(table, at + EntryCondition);
        if (condition.Error.Length != 0) problems.Add("Condition: " + condition.Error);
        IReadOnlyList<InvestmentWeightModifier> modifiers = [];
        try
        {
            var rows = table.Array(at + EntryModifiers, ModifierClass, ModifierStride);
            modifiers = Enumerable.Range(0, rows.Count).Select(row => rows.Data + row * ModifierStride)
                .Select(p => new InvestmentWeightModifier(p, InvestmentUnlocks.ReadExpression(table, p), table.U16(p + ModifierValueIndex),
                    BitConverter.Int32BitsToSingle(table.I32(p + ModifierValue)))).ToArray();
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { problems.Add("Modifiers: " + error.Message); }
        var sockets = ReadSockets(table, at + EntrySockets, itemCount, problems);
        return new(pool, index, at, item, table.U32(at + EntryQuantity), nested, table.U16(at + EntrySlot84), table.U32(at + EntryCategory),
            weight, condition, modifiers, sockets,
            new(InvestmentTableFieldKind.PoolEntryWeight, slot.Tag, pool, index, table.Float("Weight", at + EntryWeight, true)),
            new(InvestmentTableFieldKind.PoolEntryQuantity, slot.Tag, pool, index, table.UInt("Quantity", at + EntryQuantity, true)),
            slot.Bytes.Slice(at, EntryStride), string.Join(" ", problems));
    }

    /** Socket overrides need a socket type, and a fixed plug must be an installed item (rewards/definition.h valid_socket). */
    internal static IReadOnlyList<InvestmentSocketOverride> ReadSockets(TagReader blob, int field, int itemCount, List<string> problems)
    {
        try
        {
            var rows = blob.Array(field, SocketClass, SocketStride);
            var sockets = new InvestmentSocketOverride[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                int p = rows.Data + i * SocketStride;
                var socket = sockets[i] = new(p, blob.U16(p), blob.U16(p + 2), blob.U16(p + 4), blob.U16(p + 6), blob.U32(p + 8));
                if (socket.SocketType == Absent) problems.Add($"Socket override {i} has no socket type.");
                if (socket.PlugItem != Absent && socket.PlugItem >= itemCount) problems.Add($"Socket override {i} plugs item {socket.PlugItem}, outside the item index.");
            }
            return sockets;
        }
        catch (Exception error) when (InvestmentCatalog.Recoverable(error)) { problems.Add("Socket overrides: " + error.Message); return []; }
    }

    /** @return The item's wrapper block, or null when the item names none. */
    internal static InvestmentRewardWrapper? ReadWrapper(TagReader item)
    {
        int block = item.Block(WrapperField, WrapperClass);
        if (block < 0) return null;
        if (!item.Has(block, WrapperFlags + 4)) throw new InvalidDataException("Reward wrapper is truncated.");
        var rows = item.Array(block + WrapperSelections, SelectionClass, SelectionStride);
        var selections = Enumerable.Range(0, rows.Count).Select(row => rows.Data + row * SelectionStride)
            .Select(p => new InvestmentRewardSelection(item.U32(p), item.U32(p + 4), item.U32(p + 8))).ToArray();
        ushort acquired = item.Has(AcquiredFlagField, 2) ? item.U16(AcquiredFlagField) : Absent;
        return new(block, item.U16(block), item.U32(block + WrapperFlags), item.U32(block + WrapperUnknown), selections, acquired,
            selections.Length > SelectionLimit ? $"More selections than Sunrise accepts ({SelectionLimit})." : "");
    }
}

/** Pools linked through nested-pool entries and automatic wrappers, which share one traversal in Sunrise. */
public sealed class InvestmentRewardGraph
{
    private readonly List<int>[] wrappers;
    private readonly List<(int Pool, int Entry)>[] parents;
    private readonly IReadOnlyList<int>[] edges;
    /** Pools on each cycle, in the order the walk met them. */
    public IReadOnlyList<IReadOnlyList<int>> Cycles { get; }
    /** The longest chain below each pool, or -1 for a pool on or above a cycle. */
    public IReadOnlyList<int> Depths { get; }
    public int MaximumDepth { get; }

    private InvestmentRewardGraph(List<int>[] wrappers, List<(int, int)>[] parents, IReadOnlyList<int>[] edges,
        IReadOnlyList<IReadOnlyList<int>> cycles, int[] depths)
    {
        this.wrappers = wrappers; this.parents = parents; this.edges = edges; Cycles = cycles; Depths = depths;
        MaximumDepth = depths.DefaultIfEmpty(0).Max();
    }

    /** Items whose wrapper opens this pool. */
    public IReadOnlyList<int> WrappersOf(int pool) => pool >= 0 && pool < wrappers.Length ? wrappers[pool] : [];
    /** Entries that nest this pool. */
    public IReadOnlyList<(int Pool, int Entry)> ParentsOf(int pool) => pool >= 0 && pool < parents.Length ? parents[pool] : [];
    public int References(int pool) => WrappersOf(pool).Count + ParentsOf(pool).Count;

    /** The pool and every pool one draw from it can reach, each listed once, nearest first. */
    public IReadOnlyList<int> Closure(int pool)
    {
        if (pool < 0 || pool >= edges.Length) return [];
        var order = new List<int> { pool };
        var seen = new HashSet<int> { pool };
        for (int i = 0; i < order.Count; i++)
            foreach (int target in edges[order[i]]) if (seen.Add(target)) order.Add(target);
        return order;
    }

    /**
     * Links each pool to its nested pools and to the pool of any item it gives whose wrapper opens when acquired. Other
     * wrappers are leaves.
     * @param wrapperPoolByItem Each item's wrapper pool, 0xFFFF when it has none.
     * @param wrapperFlagsByItem Each item's wrapper flags.
     */
    public static InvestmentRewardGraph Build(IReadOnlyList<InvestmentRewardPool> pools, IReadOnlyList<ushort> wrapperPoolByItem,
        IReadOnlyList<uint> wrapperFlagsByItem)
    {
        int count = pools.Count;
        var wrappers = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var parents = Enumerable.Range(0, count).Select(_ => new List<(int, int)>()).ToArray();
        for (int item = 0; item < wrapperPoolByItem.Count; item++)
            if (wrapperPoolByItem[item] < count) wrappers[wrapperPoolByItem[item]].Add(item);
        var edges = new IReadOnlyList<int>[count];
        foreach (var pool in pools)
        {
            var targets = new List<int>();
            foreach (var entry in pool.Entries)
            {
                if (entry.NestedPool < count) { targets.Add(entry.NestedPool); parents[entry.NestedPool].Add((pool.Index, entry.Index)); }
                if (entry.ItemIndex < wrapperPoolByItem.Count && wrapperPoolByItem[entry.ItemIndex] < count
                    && (wrapperFlagsByItem[entry.ItemIndex] & 1) != 0)
                    targets.Add(wrapperPoolByItem[entry.ItemIndex]);
            }
            edges[pool.Index] = targets;
        }
        var (order, cycles) = InvestmentGraphWalk.Walk(edges);
        var cyclic = cycles.SelectMany(cycle => cycle).ToHashSet();
        var depths = new int[count];
        foreach (int pool in order)
            depths[pool] = cyclic.Contains(pool) || edges[pool].Any(target => depths[target] < 0) ? -1
                : edges[pool].Select(target => depths[target] + 1).DefaultIfEmpty(0).Max();
        return new(wrappers, parents, edges, cycles, depths);
    }

    /** The total weight a selection draws from: entries in its category or in any category. A null category sums every entry. */
    public static double BaseTotal(InvestmentRewardPool pool, uint? category) => pool.Entries
        .Where(entry => category is null || entry.AnyCategory || entry.Category == category)
        .Where(entry => float.IsFinite(entry.Weight) && entry.Weight >= 0).Sum(entry => (double)entry.Weight);
}
