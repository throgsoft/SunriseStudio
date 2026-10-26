using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * A draw table's closure as a read-only node graph: the wrapping item, every pool one draw can reach, and the items
 * between pools that open their own pool when acquired. Each entry is a node row with its quantity and weight. Nodes on a
 * cycle are flagged, because Sunrise then refuses the whole reward table (state/build_data/rewards/reward_catalog.cpp valid_depth).
 */
internal sealed class RewardClosureGraph
{
    /** The largest draw table closure in the measured install reaches 26 pools. A larger closure draws as the tree. */
    internal const int PoolLimit = 26;
    /** Node width in canvas units, the same as the quest canvas: room for an item name, its quantity and its weight. */
    private const float NodeWidth = 300;
    /** Row text scale and the room beside the rows: NodeCanvas DrawNode starts rows 12 units in and clips 6 before the edge. */
    private const float RowScale = .82f, RowMargin = 18;
    /** The rows a node always shows in full. NodeCanvas draws MaxCodeLines rows, the last as "..." when more exist. */
    private const int ShownRows = 7;
    /**
     * Canvas height bounds in font heights. The canvas fills the page below the toolbar, with room for at least a few node
     * rows and at most the largest measured closure, so the sections below stay one scroll away.
     */
    private const float MinimumFonts = 16, MaximumFonts = 40;
    private const string Dot = InvestmentTableViews.Dot;
    private const string CycleTip = "Part of a cycle. Sunrise refuses the whole reward table when a pool contains itself.";
    private readonly NodeCanvas canvas = new() { ShowEdgeLabels = false };
    private string selected = "";
    private (InvestmentTables? Tables, int Item, ushort Pool, float Font) key;
    private Graph? cached;

    /** The nodes, links and tooltips of one closure. A graph that is TooLarge has no nodes or links. */
    internal sealed record Graph(IReadOnlyList<CanvasNode> Nodes, IReadOnlyList<CanvasEdge> Edges, IReadOnlyDictionary<string, string> Tips,
        int Pools, bool TooLarge);

    /** Forgets node positions and the selected node before another item is shown. */
    public void Reset() { canvas.Reset(); selected = ""; }

    /** The graph of the wrapper's closure, rebuilt when the tables, item or font size change. */
    public Graph Model(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItem item, InvestmentRewardWrapper wrapper)
    {
        var wanted = (tables, item.Index, wrapper.Pool, ImGui.GetFontSize());
        if (cached is not null && key == wanted) return cached;
        key = wanted;
        // Rows draw at RowScale of the UI font, so a name measured in the UI font may fill the row room divided by that scale.
        float room = (NodeWidth - RowMargin) / RowScale;
        return cached = Build(catalog, tables, item.Index, wrapper, (name, suffix) => StudioUi.Fit(name, room - ImGui.CalcTextSize(suffix).X) + suffix);
    }

    /** Draws the Frame all and Arrange buttons on the caller's current line. */
    public void Toolbar(Graph graph)
    {
        if (StudioUi.IconButton("reward-graph-frame", StudioIcon.Frame, "Frame all")) canvas.Frame(View);
        ImGui.SameLine();
        if (StudioUi.IconButton("reward-graph-arrange", StudioIcon.Tree, "Arrange")) canvas.Arrange(View, graph.Nodes, graph.Edges);
    }

    /**
     * Draws the canvas with each item node's icon. Double-clicking a pool opens it in Reward pools, and double-clicking
     * another item opens its Rewards page.
     */
    public void Draw(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, InvestmentItem item, Graph graph,
        InvestmentIconCache icons)
    {
        var workspace = context.Workspace;
        float font = ImGui.GetFontSize();
        float height = Math.Clamp(ImGui.GetContentRegionAvail().Y, MinimumFonts * font, MaximumFonts * font);
        ImGui.BeginChild("reward-graph", new(0, height), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        // Zero keeps an item node's icon frame while its icon loads.
        var nodes = graph.Nodes.Select(node => node.Key.StartsWith(ItemKey, StringComparison.Ordinal)
            ? node with { Icon = icons.Get(catalog, int.Parse(NodeCanvas.KeyTail(node.Key), CultureInfo.InvariantCulture))?.ImGuiId ?? 0 } : node).ToArray();
        canvas.Draw(View, nodes, graph.Edges, false, ref selected, new()
        {
            Open = node =>
            {
                int index = int.Parse(NodeCanvas.KeyTail(node), CultureInfo.InvariantCulture);
                if (node.StartsWith(PoolKey, StringComparison.Ordinal))
                {
                    workspace.Open(tables.TableTag(InvestmentRootSlots.Pools));
                    context.ShowPage?.Invoke(RewardPoolsPage.PageId, RewardPoolsPage.RowOf(index));
                }
                else if (index != item.Index) { workspace.Open(catalog.Items[index].Tag); context.ShowPage?.Invoke(ItemRewardsPage.PageId, null); }
            }
        });
        ImGui.EndChild();
        if (graph.Tips.TryGetValue(canvas.HoveredNode, out string? tip))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextUnformatted(tip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private const string PoolKey = "pool/", ItemKey = "item/";
    private const string View = "reward-closure";

    /**
     * Builds the wrapping item's node first, then the pools in closure order, then every automatic wrapper between pools.
     * @param fitRow Joins an entry's name and its quantity and weight suffix, shortening the name to the node's width.
     * @param limit The most pools to build. A larger closure is reported as too large and builds no nodes.
     */
    internal static Graph Build(InvestmentCatalog catalog, InvestmentTables tables, int item, InvestmentRewardWrapper wrapper,
        Func<string, string, string> fitRow, int limit = PoolLimit)
    {
        var closure = tables.RewardGraph.Closure(wrapper.Pool);
        if (closure.Count > limit) return new([], [], new Dictionary<string, string>(), closure.Count, true);
        var nodes = new List<CanvasNode>();
        var edges = new List<CanvasEdge>();
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<int> { item };
        void Link(string source, string target)
        {
            if (!edges.Any(edge => edge.Source == source && edge.Target == target)) edges.Add(new(source, target, false));
        }
        if (closure.Count != 0) Link(ItemKey + item, PoolKey + wrapper.Pool);
        foreach (int pool in closure)
        {
            var row = tables.Pools[pool];
            var rows = new List<string>();
            foreach (var entry in row.Entries)
            {
                string weight = Dot + InvestmentTableViews.Weight(entry.Weight);
                if (entry.NestedPool != ushort.MaxValue)
                {
                    bool readable = entry.NestedPool < tables.Pools.Count;
                    rows.Add($"Pool {entry.NestedPool}" + (readable ? "" : " (unreadable)") + weight);
                    if (readable) Link(PoolKey + pool, PoolKey + entry.NestedPool);
                }
                else if (entry.ItemIndex != ushort.MaxValue)
                {
                    rows.Add(fitRow(InvestmentTableViews.ItemLabel(catalog, entry.ItemIndex), $" x{entry.Quantity.ToString(CultureInfo.InvariantCulture)}{weight}"));
                    if (tables.OpensOnAcquisition(entry.ItemIndex) && tables.WrapperPool(entry.ItemIndex) is >= 0 and var opened
                        && opened < tables.Pools.Count)
                    {
                        Link(PoolKey + pool, ItemKey + entry.ItemIndex);
                        Link(ItemKey + entry.ItemIndex, PoolKey + opened);
                        if (!items.Contains(entry.ItemIndex)) items.Add(entry.ItemIndex);
                    }
                }
                else rows.Add((entry.Slot84Reference != ushort.MaxValue ? $"Site {entry.Slot84Reference}" : "No item") + weight);
            }
            string entries = InvestmentTableViews.Count(row.Entries.Count, "entry", "entries");
            nodes.Add(new(PoolKey + pool, $"Pool {pool}", entries, Width: NodeWidth, Rows: rows,
                Tint: row.Error.Length != 0 ? StudioTheme.Failure : (Vector4?)null));
            tips[PoolKey + pool] = $"Pool {pool}{Dot}0x{row.Hash:X8}\n{entries}. Rows: item x quantity{Dot}weight, or a nested pool{Dot}weight."
                + (row.Entries.Count > ShownRows ? "\nReward pools lists every entry." : "") + (row.Error.Length != 0 ? "\n" + row.Error : "")
                + "\nDouble-click to open in Reward pools.";
        }
        foreach (int shown in items)
        {
            string name = InvestmentTableViews.ItemLabel(catalog, shown);
            bool opens = shown == item ? wrapper.OpensOnAcquisition : tables.OpensOnAcquisition(shown);
            var rows = shown == item ? wrapper.Selections.Select(selection => $"Draws {selection.Count} from "
                + (selection.Category == InvestmentRewards.AnyCategory ? "rows marked Any" : $"category 0x{selection.Category:X8}")).ToList() : null;
            // The wrapping item goes first, so the layout starts from it.
            nodes.Insert(shown == item ? 0 : nodes.Count,
                new(ItemKey + shown, name, opens ? "Opens when acquired" : "Kept as an item when acquired", Width: NodeWidth, Rows: rows));
            string identity = shown < catalog.Items.Count ? $"\nItem {shown}{Dot}{catalog.Items[shown].Hash:X8}" : "";
            tips[ItemKey + shown] = name + identity + (shown == item ? "\nThis item's draw table." : "\nIts pool opens when it is acquired.\nDouble-click to open its rewards.");
        }
        var cyclic = Cyclic(nodes, edges);
        for (int i = 0; i < nodes.Count; i++)
            if (cyclic.Contains(nodes[i].Key))
            {
                nodes[i] = nodes[i] with { Subtitle = nodes[i].Subtitle + Dot + "cycle", Tint = StudioTheme.Failure };
                tips[nodes[i].Key] += "\n" + CycleTip;
            }
        return new(nodes, edges, tips, closure.Count, false);
    }

    /** Keys of the nodes that can reach themselves. */
    private static HashSet<string> Cyclic(IReadOnlyList<CanvasNode> nodes, IReadOnlyList<CanvasEdge> edges)
    {
        var next = edges.GroupBy(edge => edge.Source).ToDictionary(group => group.Key, group => group.Select(edge => edge.Target).ToArray());
        var cyclic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(next.GetValueOrDefault(node.Key, []));
            while (pending.TryPop(out string? at))
            {
                if (at == node.Key) { cyclic.Add(at); break; }
                if (seen.Add(at)) foreach (string target in next.GetValueOrDefault(at, [])) pending.Push(target);
            }
        }
        return cyclic;
    }
}
