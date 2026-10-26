namespace SunriseStudio.Formats;

public enum RewardReferenceKind { CompletionPrimary, CompletionSecondary, PoolEntry, Acquire, SocketSite, SocketList }
/** Owner is an item index, or a pool index for PoolEntry. Element is the pool entry or socket lane, or -1. */
public readonly record struct RewardReference(RewardReferenceKind Kind, int Owner, int Element);
/**
 * A saved unlock row as the slot tables bind it: flag or value, mapping kind and bank row. Slot is -1, except for a read no
 * mapping table binds, which is keyed by its own slot.
 */
public readonly record struct UnlockBinding(bool Value, ushort MapKind, ushort BankRow, int Slot);
public enum UnlockUseKind { ObjectiveSource, ObjectiveTarget, PoolCondition, PoolWeightRule, PassCondition, PassClaim,
    WrapperAcquired, CompletionValue, ContextValue, ChainValue, PreviewCondition }
/**
 * One read of a saved unlock row. Owner and Element depend on the kind: objective and -1 for objectives, pool and entry for
 * pool rules, progression and row for pass rows, item and -1 for wrapper and completion reads, the set's owner item and -1
 * for a chain value, and item and preview slot for a preview condition. Slot is the authored slot, and ViaShared is the
 * shared row the read sits in, or -1 when the owner reads it directly.
 */
public readonly record struct UnlockUse(UnlockUseKind Kind, int Owner, int Element, int Slot, int ViaShared);

/**
 * Reverse indexes over the installed tables: what names each reward site and slot-79 row, what reads each saved unlock row,
 * and what gives each item.
 */
public sealed class InvestmentReferences
{
    private const ushort Absent = ushort.MaxValue;
    private readonly Dictionary<ushort, List<RewardReference>> sites = [], slot79 = [];
    private readonly Dictionary<UnlockBinding, List<UnlockUse>> users = [];
    private readonly Dictionary<int, List<(int Pool, int Entry)>> poolsGiving = [];
    private readonly Dictionary<int, List<(int Progression, int Row)>> passRows = [];
    private readonly Dictionary<int, List<int>> previewedBy = [];
    private readonly Dictionary<int, List<(RewardReference, ushort)>> itemReferences = [];
    /** Slot-84 indices something references, sorted. */
    public IReadOnlyList<ushort> SiteIndices { get; private set; } = [];
    /** Slot-79 indices something references, sorted. */
    public IReadOnlyList<ushort> Slot79Indices { get; private set; } = [];

    private InvestmentReferences() { }

    /** Everything that names one reward site (root slot 84). */
    public IReadOnlyList<RewardReference> Sites(ushort site) => sites.GetValueOrDefault(site) ?? [];
    public IReadOnlyList<RewardReference> Slot79(ushort row) => slot79.GetValueOrDefault(row) ?? [];
    /** Every use of one saved flag or value row, whichever authored slot names it. */
    public IReadOnlyList<UnlockUse> Users(UnlockBinding binding) => users.GetValueOrDefault(binding) ?? [];
    /** Pool entries that grant the item. */
    public IReadOnlyList<(int Pool, int Entry)> PoolsGiving(int item) => poolsGiving.GetValueOrDefault(item) ?? [];
    /** Progression reward rows that pay the item. */
    public IReadOnlyList<(int Progression, int Row)> PassRowsOf(int item) => passRows.GetValueOrDefault(item) ?? [];
    /** Items whose reward preview shows the item. */
    public IReadOnlyList<int> PreviewedBy(int item) => previewedBy.GetValueOrDefault(item) ?? [];
    /**
     * The site and slot-79 references the item's own definition makes: acquire, then completions, then socket lanes in
     * lane order. Index is the slot-84 site, or the slot-79 row for SocketList.
     */
    public IReadOnlyList<(RewardReference Reference, ushort Index)> ItemReferences(int item) => itemReferences.GetValueOrDefault(item) ?? [];

    internal static InvestmentReferences Build(InvestmentTables tables, InvestmentItemLinks[] links, CancellationToken token)
    {
        var references = new InvestmentReferences();
        references.IndexSites(tables, links, token);
        new UnlockScan(tables.Unlocks, references.users).Run(tables, links, token);
        references.IndexGivers(tables, links);
        return references;
    }

    /** Completion operations first, then pool entries, acquire references and socket lanes, each in owner order. */
    private void IndexSites(InvestmentTables tables, InvestmentItemLinks[] links, CancellationToken token)
    {
        void Site(ushort index, RewardReferenceKind kind, int owner, int element = -1)
        { if (index != Absent) Add(sites, index, new(kind, owner, element)); }
        for (int item = 0; item < links.Length; item++)
        {
            Site(links[item].Primary, RewardReferenceKind.CompletionPrimary, item);
            Site(links[item].Secondary, RewardReferenceKind.CompletionSecondary, item);
        }
        foreach (var pool in tables.Pools)
            foreach (var entry in pool.Entries) Site(entry.Slot84Reference, RewardReferenceKind.PoolEntry, pool.Index, entry.Index);
        token.ThrowIfCancellationRequested();
        for (int item = 0; item < links.Length; item++) Site(links[item].AcquireSite, RewardReferenceKind.Acquire, item);
        for (int item = 0; item < links.Length; item++)
            for (int lane = 0; lane < links[item].SocketLanes.Length; lane++)
            {
                var (list, site) = links[item].SocketLanes[lane];
                Site(site, RewardReferenceKind.SocketSite, item, lane);
                if (list != Absent) Add(slot79, list, new(RewardReferenceKind.SocketList, item, lane));
            }
        for (int item = 0; item < links.Length; item++)
        {
            var link = links[item];
            void Own(ushort index, RewardReferenceKind kind, int element = -1)
            { if (index != Absent) Add(itemReferences, item, (new RewardReference(kind, item, element), index)); }
            Own(link.AcquireSite, RewardReferenceKind.Acquire);
            Own(link.Primary, RewardReferenceKind.CompletionPrimary);
            Own(link.Secondary, RewardReferenceKind.CompletionSecondary);
            for (int lane = 0; lane < link.SocketLanes.Length; lane++)
            {
                Own(link.SocketLanes[lane].Site, RewardReferenceKind.SocketSite, lane);
                Own(link.SocketLanes[lane].List, RewardReferenceKind.SocketList, lane);
            }
        }
        SiteIndices = sites.Keys.Order().ToArray();
        Slot79Indices = slot79.Keys.Order().ToArray();
    }

    private void IndexGivers(InvestmentTables tables, InvestmentItemLinks[] links)
    {
        foreach (var pool in tables.Pools)
            foreach (var entry in pool.Entries.Where(entry => entry.ItemIndex != Absent)) Add(poolsGiving, entry.ItemIndex, (pool.Index, entry.Index));
        foreach (var progression in tables.Progressions)
            foreach (var row in progression.Rewards.Where(row => row.ItemIndex < Absent)) Add(passRows, (int)row.ItemIndex, (progression.Index, row.Index));
        for (int item = 0; item < links.Length; item++)
            foreach (ushort shown in links[item].Previews.Select(preview => preview.Item).Where(shown => shown != Absent).Distinct())
                Add(previewedBy, shown, item);
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key, TValue value) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(value);
    }

    /**
     * Collects every authored read of a saved flag or value: objectives, pool entries, pass rows, wrappers, pursuit values,
     * quest chains and preview conditions. Shared rows are inlined as Sunrise inlines them, up to its nesting limit and
     * never around a cycle. One owner reading one row the same way twice counts as one use.
     */
    private sealed class UnlockScan(InvestmentUnlocks unlocks, Dictionary<UnlockBinding, List<UnlockUse>> users)
    {
        private readonly HashSet<(UnlockBinding, UnlockUse)> seen = [];
        private readonly HashSet<uint> visiting = [];

        public void Run(InvestmentTables tables, InvestmentItemLinks[] links, CancellationToken token)
        {
            foreach (var objective in tables.Objectives)
            {
                if ((objective.Index & 255) == 0) token.ThrowIfCancellationRequested();
                Expression(objective.Source, UnlockUseKind.ObjectiveSource, objective.Index);
                Expression(objective.TargetAdjustment, UnlockUseKind.ObjectiveTarget, objective.Index);
            }
            foreach (var pool in tables.Pools)
                foreach (var entry in pool.Entries)
                {
                    Expression(entry.Condition, UnlockUseKind.PoolCondition, pool.Index, entry.Index);
                    foreach (var modifier in entry.Modifiers) Expression(modifier.Condition, UnlockUseKind.PoolWeightRule, pool.Index, entry.Index);
                }
            token.ThrowIfCancellationRequested();
            foreach (var progression in tables.Progressions)
                foreach (var row in progression.Rewards)
                {
                    foreach (var member in row.Conditions.Members) Expression(member, UnlockUseKind.PassCondition, progression.Index, row.Index);
                    // Claim slot 0 means the row has no claim flag (InvestmentSeasonPass.ReadRow).
                    if (row.ClaimSlot != 0) Slot(InvestmentUnlocks.FlagOpcode, row.ClaimSlot, UnlockUseKind.PassClaim, progression.Index, row.Index);
                }
            for (int item = 0; item < links.Length; item++)
            {
                if ((item & 255) == 0) token.ThrowIfCancellationRequested();
                var link = links[item];
                Slot(InvestmentUnlocks.FlagOpcode, link.AcquiredFlagSlot, UnlockUseKind.WrapperAcquired, item);
                Slot(InvestmentUnlocks.ValueOpcode, link.CompletionValue, UnlockUseKind.CompletionValue, item);
                Slot(InvestmentUnlocks.ValueOpcode, link.ContextValue, UnlockUseKind.ContextValue, item);
                Slot(InvestmentUnlocks.ValueOpcode, link.QuestSetValueSlot, UnlockUseKind.ChainValue, item);
                foreach (var preview in link.Previews.Where(preview => preview.Condition != Absent))
                    Shared(preview.Condition, UnlockUseKind.PreviewCondition, item, preview.Slot, 0);
            }
        }

        private void Expression(InvestmentExpression expression, UnlockUseKind kind, int owner, int element = -1) =>
            Read(expression.Instructions, kind, owner, element, -1, 0);

        /** A slot an owner names directly. 0xFFFF names none. */
        private void Slot(uint opcode, uint slot, UnlockUseKind kind, int owner, int element = -1)
        {
            if (slot != Absent) Read([new(opcode, slot, -1)], kind, owner, element, -1, 0);
        }

        private void Read(IReadOnlyList<InvestmentUnlockInstruction> instructions, UnlockUseKind kind, int owner, int element, int via, int depth)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Opcode == InvestmentUnlocks.SharedOpcode) { Shared(instruction.Operand, kind, owner, element, depth); continue; }
                if (unlocks.Binding(instruction) is not { } binding) continue;
                var use = new UnlockUse(kind, owner, element, (int)Math.Min(instruction.Operand, int.MaxValue), via);
                if (!seen.Add((binding, use))) continue;
                if (!users.TryGetValue(binding, out var list)) users[binding] = list = [];
                list.Add(use);
            }
        }

        private void Shared(uint row, UnlockUseKind kind, int owner, int element, int depth)
        {
            if (depth >= InvestmentUnlocks.ServerNestingLimit || row >= unlocks.SharedCount || !visiting.Add(row)) return;
            Read(unlocks.Shared((int)row).Instructions, kind, owner, element, (int)row, depth + 1);
            visiting.Remove(row);
        }
    }
}
