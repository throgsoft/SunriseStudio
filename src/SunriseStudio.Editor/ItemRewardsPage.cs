using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Rendering;
using Sites = SunriseStudio.Editor.RewardSitesPage.RewardIndexTable;

namespace SunriseStudio.Editor;

/** How a Rewards row draws its text. */
internal enum RewardLineStyle { Normal, Muted, Failure }
/** What a Rewards row's button opens; None draws no button. */
internal enum RewardLinkKind { None, Item, Vendor, Pool, Site, Slot79Row, PassRow }

/**
 * One Rewards row: text, a muted detail, the tooltip, the item whose colour and icon it uses (-1 for none) and its link.
 * Target and Element name the link's row: an item, vendor, pool or site index, or a progression and its reward row.
 */
internal readonly record struct RewardLine(string Text, string Detail = "", string Tip = "", int Item = -1,
    RewardLinkKind Link = RewardLinkKind.None, int Target = -1, int Element = -1, RewardLineStyle Style = RewardLineStyle.Normal);

/** What the Rewards page shows for one item, built once per detail, table and vendor snapshot. */
internal sealed record RewardsModel(IReadOnlyList<RewardLine> DrawTable, (string Value, string Tip)? AcquiredFlag,
    IReadOnlyList<RewardLine> Sites, IReadOnlyList<RewardLine> Previews, IReadOnlyList<RewardLine> GivenBy);

/** An item's rewards: its draw table as a tree or graph, the reward sites it references, its preview and what gives it. */
public sealed class ItemRewardsPage : IStudioPage, IFramePreparedPage, IDisposable
{
    public const string PageId = "sunrise.item-rewards";
    private const string Dot = InvestmentTableViews.Dot;
    private const ushort Absent = ushort.MaxValue;
    private const string DrawTableTip = "Extracted: what opening this item can give. Activity reward mappings are not in this install.";
    private const string PreviewTip = InvestmentTableViews.PreviewTip;
    private const string GivenByTip = "Activity and bounty sources are server policy; this install does not ship them. "
        + "Activity reward mappings resolve into a root slot this install does not ship.";
    private const string Inferred = "Studio reads this index as a reward site reference; no Sunrise reader confirms it.\n" + StudioUi.MayBeInaccurate;
    private const string CompletionTip = InvestmentTableViews.CompletionTip;
    private readonly InvestmentTablesLoader loader = new();
    private readonly InvestmentFieldPopup popup = new();
    private readonly InvestmentPendingMarks pending = new();
    private readonly InvestmentRewardTree tree = new();
    private readonly RewardClosureGraph graph = new();
    private readonly InvestmentIconCache icons = new();
    internal InvestmentIconCache Icons => icons;
    private InvestmentCatalog? requestedVendors;
    private (InvestmentItemDetail? Detail, InvestmentTables? Tables, InvestmentVendorSales? Vendors) modelKey;
    private RewardsModel? model;
    private uint shownItem;
    private bool graphView;
    /** True when the draw table shows as its closure graph instead of the tree. */
    internal bool GraphView { get => graphView; set => graphView = value; }
    public string Id => PageId;
    public string Title => "Rewards";

    // The item and display index tables carry the item class but are not items.
    public bool CanOpen(StudioDocument document) => document.Entry.Reference == InvestmentCatalog.ItemClass
        && document.Entry.TagHash is not (InvestmentCatalog.ItemIndexTag or InvestmentCatalog.DisplayIndexTag);

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer)
    {
        popup.Invalidate(workspace);
        icons.Prepare(workspace.InvestmentDetailCurrent ? workspace.Investment : null, renderer);
    }

    public void Dispose() => icons.Dispose();

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (!loader.Ready(workspace, out var catalog, out var tables)) return;
        workspace.RequestInvestmentDetail();
        if (!workspace.InvestmentDetailCurrent)
        {
            if (workspace.IsBusy) { ImGui.TextDisabled("Reading item"); return; }
            if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
            if (ImGui.Button("Retry")) workspace.RetryInvestmentDetail();
            return;
        }
        if (workspace.SelectedInvestmentItem is not { } detail) { ImGui.TextDisabled("Not an item"); return; }
        // Vendor sale rows are read per vendor, so the index of who sells what waits for the first page that asks.
        if (!catalog.VendorSalesLoaded && !workspace.IsBusy && !ReferenceEquals(requestedVendors, catalog))
        { requestedVendors = catalog; workspace.Run(workspace.LoadInvestmentVendorSales); }
        var vendors = catalog.VendorSalesLoaded ? catalog.VendorSales() : null;
        if (!ReferenceEquals(modelKey.Detail, detail) || !ReferenceEquals(modelKey.Tables, tables) || !ReferenceEquals(modelKey.Vendors, vendors))
        { modelKey = (detail, tables, vendors); model = Build(catalog, tables, detail, vendors); }
        if (shownItem != detail.Item.Tag) { shownItem = detail.Item.Tag; tree.Reset(); graph.Reset(); }
        DrawDrawTable(context, catalog, tables, detail, model!);
        StudioUi.Heading("REWARD SITES", tables.Slot84.State == InvestmentSlotState.Omitted
            ? "Root slot 84 is not in this install, so no site's body is shown; Sunrise refuses or ignores these references."
            : "Site rows are present but not decoded.");
        DrawLines(context, catalog, tables, "sites", model!.Sites);
        StudioUi.Heading("PREVIEW", PreviewTip);
        if (detail.Pursuit?.Previews.Count > 0) ImGui.TextDisabled("Shown in the game's pursuit screen; not a grant.");
        DrawLines(context, catalog, tables, "previews", model.Previews);
        StudioUi.Heading("GIVEN BY", GivenByTip);
        DrawLines(context, catalog, tables, "given", model.GivenBy);
        popup.Draw(workspace, refusal: _ => InvestmentRewardTree.QuantityRefusal(catalog, tables, popup));
    }

    /** Draws the draw table's summary and pool lines, its acquisition flag, then the pool as a tree or a graph. */
    private void DrawDrawTable(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, InvestmentItemDetail detail,
        RewardsModel rewards)
    {
        StudioUi.Heading("DRAW TABLE", DrawTableTip);
        if (detail.Wrapper is not { } wrapper) { DrawLines(context, catalog, tables, "draw", rewards.DrawTable); return; }
        DrawLine(context, catalog, tables, rewards.DrawTable[0]);
        StudioUi.Help("Weights are relative. Conditions, class and earlier picks change the odds, so Studio shows no chances.");
        if (wrapper.Error.Length != 0) ImGui.TextColored(StudioTheme.Failure, wrapper.Error);
        DrawLine(context, catalog, tables, rewards.DrawTable[1]);
        if (rewards.AcquiredFlag is { } flag) StudioUi.Facts("item-rewards-flag", [("Acquired flag", flag.Value, flag.Tip)]);
        Toggle("Tree", !graphView, false);
        ImGui.SameLine();
        Toggle("Graph", graphView, true);
        var built = graphView ? graph.Model(catalog, tables, detail.Item, wrapper) : null;
        if (built is { TooLarge: false })
        {
            ImGui.SameLine();
            graph.Toolbar(built);
            graph.Draw(context, catalog, tables, detail.Item, built, icons);
            return;
        }
        if (built is not null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Too large to graph");
            StudioUi.Tip($"This draw table reaches {InvestmentTableViews.Count(built.Pools, "pool")}; the graph shows at most "
                + $"{RewardClosureGraph.PoolLimit}, the most any draw table on the measured install reaches.");
        }
        tree.Draw(context, catalog, tables, wrapper.Pool, popup, pending, icons, "item-rewards");
    }

    /** One half of the Tree / Graph switch. The button of the shown view stays highlighted. */
    private void Toggle(string label, bool active, bool graph)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)(active ? ImGuiCol.Header : ImGuiCol.Button)]);
        if (ImGui.SmallButton(label + "##reward-view")) graphView = graph;
        ImGui.PopStyleColor();
        StudioUi.Tip(graph ? "Show the pools this draw table reaches as a graph" : "Show the pool as a tree with editable weights and quantities");
    }

    /** Draws one section's lines, clipped to the visible rows. Every line is one frame high. */
    private unsafe void DrawLines(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, string id,
        IReadOnlyList<RewardLine> lines)
    {
        ImGui.PushID(id);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(lines.Count, ImGui.GetFrameHeightWithSpacing());
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ImGui.PushID(i);
                DrawLine(context, catalog, tables, lines[i]);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.PopID();
    }

    /**
     * Draws the item's icon, the text, the muted detail and the link at the right edge. The text is shortened to leave room
     * for the link. A line without an item keeps the icon's room, so the texts of a section line up.
     */
    private void DrawLine(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, RewardLine line)
    {
        var style = ImGui.GetStyle();
        float start = ImGui.GetCursorPosX(), width = ImGui.GetContentRegionAvail().X;
        // A muted line, such as a section's empty line, keeps no icon room, so the muted lines of all sections line up.
        if (line.Style != RewardLineStyle.Muted)
        {
            float side = ImGui.GetFrameHeight();
            icons.Draw(catalog, line.Item, side);
            ImGui.SameLine();
        }
        string link = LinkLabel(line.Link);
        float linkWidth = link.Length == 0 ? 0 : ImGui.CalcTextSize(link).X + style.FramePadding.X * 2;
        float room = start + width - ImGui.GetCursorPosX() - (link.Length == 0 ? 0 : linkWidth + style.ItemSpacing.X);
        ImGui.AlignTextToFramePadding();
        string text = StudioUi.Fit(line.Text, room);
        if (line.Style == RewardLineStyle.Failure) ImGui.TextColored(StudioTheme.Failure, text);
        else if (line.Style == RewardLineStyle.Muted) ImGui.TextDisabled(text);
        else if (line.Item >= 0 && line.Item < catalog.Items.Count) ImGui.TextColored(StudioTheme.Rarity(catalog.Items[line.Item].Tier), text);
        else ImGui.TextUnformatted(text);
        StudioUi.Tip(text == line.Text ? line.Tip : $"{line.Text}\n{line.Tip}".TrimEnd());
        if (line.Detail.Length != 0 && text == line.Text)
        {
            ImGui.SameLine();
            string detail = StudioUi.Fit(line.Detail, room - ImGui.CalcTextSize(text).X - style.ItemSpacing.X);
            ImGui.TextDisabled(detail);
            StudioUi.Tip(detail == line.Detail ? line.Tip : $"{line.Detail}\n{line.Tip}".TrimEnd());
        }
        if (link.Length == 0) return;
        ImGui.SameLine(start + width - linkWidth);
        if (ImGui.SmallButton(link)) Follow(context, catalog, tables, line);
    }

    internal static string LinkLabel(RewardLinkKind link) => link switch
    {
        RewardLinkKind.Item or RewardLinkKind.Vendor => "Open",
        RewardLinkKind.Pool => "Open in Reward pools",
        RewardLinkKind.Site or RewardLinkKind.Slot79Row => "Open in Reward sites",
        RewardLinkKind.PassRow => "Open in Season pass",
        _ => ""
    };

    /** Opens the line's target. A table row opens its table on that table's page with the row selected. */
    internal static void Follow(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, RewardLine line)
    {
        var workspace = context.Workspace;
        switch (line.Link)
        {
            case RewardLinkKind.Item: workspace.Open(catalog.Items[line.Target].Tag); break;
            case RewardLinkKind.Vendor: workspace.Open(catalog.Vendors[line.Target].Tag); break;
            case RewardLinkKind.Pool:
                workspace.Open(tables.TableTag(InvestmentRootSlots.Pools));
                context.ShowPage?.Invoke(RewardPoolsPage.PageId, RewardPoolsPage.RowOf(line.Target));
                break;
            case RewardLinkKind.Site or RewardLinkKind.Slot79Row:
                workspace.Open(tables.Root!.Tag);
                context.ShowPage?.Invoke(RewardSitesPage.PageId,
                    RewardSitesPage.RowOf(line.Link == RewardLinkKind.Site ? Sites.Sites : Sites.Slot79, (ushort)line.Target));
                break;
            case RewardLinkKind.PassRow:
                workspace.Open(tables.TableTag(InvestmentRootSlots.Progressions));
                context.ShowPage?.Invoke(SeasonPassPage.PageId, SeasonPassPage.RowOf(line.Target, line.Element));
                break;
        }
    }

    /** Builds every section's rows for one item. A link to a table page is offered only when that table is present. */
    internal static RewardsModel Build(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItemDetail detail, InvestmentVendorSales? vendors)
    {
        int item = detail.Item.Index;
        var (drawTable, flag) = DrawTableLines(tables, detail);
        return new(drawTable, flag, SiteLines(tables, detail), PreviewLines(catalog, tables, detail), GivenByLines(catalog, tables, item, vendors));
    }

    /** The wrapper's summary and pool lines, or one line saying the item has no draw table or it could not be read. */
    private static (RewardLine[] Lines, (string, string)? Flag) DrawTableLines(InvestmentTables tables, InvestmentItemDetail detail)
    {
        if (detail.Wrapper is not { } wrapper)
            return detail.Diagnostics.FirstOrDefault(line => line.StartsWith("Rewards: ", StringComparison.Ordinal)) is { } failure
                ? ([new("Draw table not read.", Tip: failure["Rewards: ".Length..], Style: RewardLineStyle.Failure)], null)
                : ([new("No draw table", Tip: "This item has no wrapper block, so opening it gives nothing from the client data.",
                    Style: RewardLineStyle.Muted)], null);
        var pool = wrapper.Pool < tables.Pools.Count ? tables.Pools[wrapper.Pool] : null;
        string summary = (wrapper.OpensOnAcquisition ? "Opens when acquired" : "Kept as an item when acquired")
            + string.Concat(wrapper.Selections.Select(selection => Dot + $"Draws {selection.Count} from "
                + (selection.Category == InvestmentRewards.AnyCategory ? "rows marked Any" : $"category 0x{selection.Category:X8}")));
        string tip = (wrapper.OpensOnAcquisition ? "Sunrise picks the rewards when it grants the item, with its own draw, not the retail one."
            : "The item stays in the inventory until it is opened.") + string.Concat(wrapper.Selections.Select(selection => "\nExtracted."
                + (pool is null ? "" : $" Base total {InvestmentTableViews.Weight((float)InvestmentRewardGraph.BaseTotal(pool, selection.Category))}: "
                    + "the weights this selection draws from, before conditions, class and earlier picks.")));
        bool usable = pool is not null && InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Pools));
        RewardLine poolLine = new($"Pool {wrapper.Pool}" + (pool is null ? " (unreadable)" : Dot + InvestmentTableViews.Count(pool.Entries.Count, "entry", "entries")),
            Tip: pool is null ? "The reward pool table has no such row." : $"Pool {wrapper.Pool}\n0x{pool.Hash:X8}",
            Link: usable ? RewardLinkKind.Pool : RewardLinkKind.None, Target: wrapper.Pool,
            Style: pool is null ? RewardLineStyle.Failure : RewardLineStyle.Muted);
        (string, string)? flag = wrapper.AcquiredFlagSlot == Absent ? ("None", "The wrapper names no acquisition flag.")
            : (tables.Unlocks.FlagLabel(wrapper.AcquiredFlagSlot).Text, tables.Unlocks.FlagLabel(wrapper.AcquiredFlagSlot).Detail);
        return ([new(summary, Tip: tip), poolLine], flag);
    }

    /** The item's own references, then every pool entry in its draw table's closure that names a site. */
    private static RewardLine[] SiteLines(InvestmentTables tables, InvestmentItemDetail detail)
    {
        int item = detail.Item.Index;
        bool linked = tables.Root is not null;
        var lines = new List<RewardLine>();
        foreach (var (reference, index) in tables.References.ItemReferences(item))
        {
            bool list = reference.Kind == RewardReferenceKind.SocketList;
            string kind = reference.Kind switch
            {
                RewardReferenceKind.Acquire => "Acquire",
                RewardReferenceKind.CompletionPrimary => "Completion (primary)",
                RewardReferenceKind.CompletionSecondary => "Completion (secondary)",
                _ => $"Socket {reference.Element}"
            };
            string tip = reference.Kind is RewardReferenceKind.CompletionPrimary or RewardReferenceKind.CompletionSecondary ? CompletionTip
                : list ? "Studio reads this index as a row of root slot 79; no Sunrise reader confirms it.\n" + StudioUi.MayBeInaccurate : Inferred;
            lines.Add(new(list ? $"Slot 79 row {index}" : $"Site {index}", kind, tip,
                Link: !linked ? RewardLinkKind.None : list ? RewardLinkKind.Slot79Row : RewardLinkKind.Site, Target: index));
        }
        if (detail.Wrapper is { } wrapper)
            foreach (int pool in tables.RewardGraph.Closure(wrapper.Pool))
                foreach (var entry in tables.Pools[pool].Entries.Where(entry => entry.Slot84Reference != Absent))
                    lines.Add(new($"Site {entry.Slot84Reference}", $"Pool {pool}, entry {entry.Index}", InvestmentRewardTree.SiteTip(tables, entry),
                        Link: linked ? RewardLinkKind.Site : RewardLinkKind.None, Target: entry.Slot84Reference));
        if (lines.Count == 0) lines.Add(new("None referenced", Style: RewardLineStyle.Muted));
        if (detail.Diagnostics.FirstOrDefault(line => line.StartsWith("Objectives: ", StringComparison.Ordinal)) is { } failure)
            lines.Add(new("Completion references not read.", Tip: failure["Objectives: ".Length..], Style: RewardLineStyle.Failure));
        return [.. lines];
    }

    /** The reward previews the pursuit screen shows, each with its quantity and condition. */
    private static RewardLine[] PreviewLines(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItemDetail detail)
    {
        var previews = detail.Pursuit?.Previews ?? [];
        if (previews.Count == 0) return [new("No preview", Style: RewardLineStyle.Muted)];
        return [.. previews.Select(preview =>
        {
            bool known = preview.ItemIndex < catalog.Items.Count;
            string detailText = $"x{preview.Quantity.ToString(CultureInfo.InvariantCulture)}" + (preview.Condition is null ? "" : Dot + "Conditional");
            return new RewardLine(InvestmentTableViews.ItemLabel(catalog, preview.ItemIndex), detailText,
                preview.Condition is { } condition ? InvestmentTableViews.ConditionTip(tables.Unlocks, condition) : "",
                known ? preview.ItemIndex : -1, known ? RewardLinkKind.Item : RewardLinkKind.None, preview.ItemIndex,
                Style: known ? RewardLineStyle.Normal : RewardLineStyle.Failure);
        })];
    }

    /** Pools, season pass rows and vendors that give the item, and pursuits that preview it. */
    private static RewardLine[] GivenByLines(InvestmentCatalog catalog, InvestmentTables tables, int item, InvestmentVendorSales? vendors)
    {
        var references = tables.References;
        var lines = new List<RewardLine>();
        bool pools = InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Pools));
        foreach (var (pool, entry) in references.PoolsGiving(item))
        {
            var row = tables.Pools[pool].Entries[entry];
            lines.Add(new($"Pool {pool}, entry {entry}", RewardPoolsPage.Label(catalog, tables, pool),
                $"Quantity {row.Quantity:N0}{Dot}weight {InvestmentTableViews.Weight(row.Weight)}",
                Link: pools ? RewardLinkKind.Pool : RewardLinkKind.None, Target: pool, Element: entry));
        }
        bool pass = InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Progressions));
        foreach (var (progression, index) in references.PassRowsOf(item))
        {
            var row = tables.Progressions[progression].Rewards[index];
            lines.Add(new($"Season pass rank {row.Rank.ToString(CultureInfo.InvariantCulture)}{Dot}progression {progression}", "",
                $"Row {index}{Dot}quantity {row.Quantity:N0}", Link: pass ? RewardLinkKind.PassRow : RewardLinkKind.None,
                Target: progression, Element: index));
        }
        if (vendors is null) lines.Add(new("Reading vendors.", Style: RewardLineStyle.Muted));
        else
        {
            foreach (int vendor in vendors.Selling(item))
                lines.Add(new(catalog.Vendors[vendor].Name, "", $"Vendor {vendor}{Dot}{catalog.Vendors[vendor].Hash:X8}",
                    Link: RewardLinkKind.Vendor, Target: vendor));
            if (vendors.Problems.Count != 0)
                lines.Add(new($"{InvestmentTableViews.Count(vendors.Problems.Count, "vendor")} not read.", Tip: string.Join("\n", vendors.Problems),
                    Style: RewardLineStyle.Failure));
        }
        foreach (int owner in references.PreviewedBy(item))
            lines.Add(new($"{InvestmentTableViews.ItemLabel(catalog, owner)} (preview)", "", PreviewTip, owner, RewardLinkKind.Item, owner));
        if (lines.Count == 0) lines.Add(new("Nothing in this install gives this item.", Style: RewardLineStyle.Muted));
        return [.. lines];
    }
}
