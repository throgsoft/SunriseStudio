using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;

namespace SunriseStudio.Editor;

/** The reward-site indices (root slot 84) and slot 79 rows that installed data references, with who references each one. */
public sealed class RewardSitesPage : IStudioPage, IStudioRowPage, IFramePreparedPage, IDisposable
{
    public const string PageId = "sunrise.reward-sites";
    /** ROOT SLOTS takes at most this share of the page height, so the referenced rows keep the rest. */
    private const float SlotsShare = .3f;
    /** The list column's share of the page and its bounds in font heights, as on the Reward pools page. */
    private const float ListShare = .35f, ListMinimumFonts = 14, ListMaximumFonts = 26;
    /** A focused row scrolls to the middle of the list: half the visible rows sit above it. */
    private const int CentreDivisor = 2;
    /** Root slot 79. Studio reads each socket lane's list index as a row of this table. */
    private const int Slot79 = 79;
    private const string Dot = InvestmentTableViews.Dot;
    private const string Inferred = "; no Sunrise reader confirms it.\n" + StudioUi.MayBeInaccurate;
    private static readonly string[] TableNames = ["Reward sites", "Slot 79"];
    // In ReferenceGroup order.
    private static readonly string[] KindNames = ["All references", "Completions", "Pool entries", "Acquire", "Socket lanes"];
    // The row ids' table names, in RewardIndexTable order.
    private static readonly string[] RowTables = ["sites", "slot79"];
    // A row that history or a link asked for. The first draw after the tables are read selects it.
    private (RewardIndexTable Table, ushort Index)? requested;
    private readonly InvestmentTablesLoader loader = new();
    private readonly InvestmentIconCache icons = new();
    internal InvestmentIconCache Icons => icons;
    private int table, kind;
    private int selected = -1;
    private bool scrollToSelected;
    private string search = "";
    private SiteIndex? index;
    private (SiteIndex? Index, int Table, int Kind, string Search) shownKey;
    private ushort[] shown = [];
    public string Id => PageId;
    public string Title => "Reward sites";

    /** Which root table a referenced index names. */
    public enum RewardIndexTable { Sites, Slot79 }
    /** The kind filter's entries. Every reference kind belongs to exactly one group after All. */
    private enum ReferenceGroup { All, Completions, PoolEntries, Acquire, SocketLanes }
    /** One row of ROOT SLOTS: the slot, its name, whether this install ships it, and how many references name its rows. */
    internal readonly record struct RootSlot(int Slot, string Table, string State, string References);
    /** One referrer as the list and detail show it. */
    private readonly record struct Referrer(RewardReference Reference, string Text, string Detail);
    /** Counts, root slots, problems and referrer labels of one table snapshot. */
    private sealed record SiteIndex(InvestmentTables Tables, string Counts, string CountsTip, IReadOnlyList<RootSlot> Slots,
        IReadOnlyList<string> Problems, Dictionary<RewardReference, Referrer> Referrers);

    public bool CanOpen(StudioDocument document) =>
        AssetCategories.IsStructured(document.Entry) && document.Entry.Reference == InvestmentRoot.RootClass;

    public string? Row => requested is { } wanted ? RowOf(wanted.Table, wanted.Index)
        : selected is >= 0 and <= ushort.MaxValue ? RowOf((RewardIndexTable)table, (ushort)selected) : null;

    public void ShowRow(string row)
    {
        if (row.Split(':') is ["site", var name, var text] && Array.IndexOf(RowTables, name) is >= 0 and var shown
            && ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ushort index))
            requested = ((RewardIndexTable)shown, index);
    }

    /** The row id that history and links use for a referenced row: "site:sites:<index>" or "site:slot79:<index>". */
    internal static string RowOf(RewardIndexTable table, ushort index) =>
        string.Create(CultureInfo.InvariantCulture, $"site:{RowTables[(int)table]}:{index}");

    public void PrepareFrame(StudioWorkspace workspace, Dx11Renderer renderer) => icons.Prepare(workspace.Investment, renderer);

    public void Dispose() => icons.Dispose();

    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (!loader.Ready(workspace, out var catalog, out var tables)) return;
        if (tables.Root is null || workspace.Document?.Entry.TagHash != tables.Root.Tag)
        {
            ImGui.TextDisabled("Not the investment root");
            StudioUi.Tip("The investment tables were read from another root.");
            return;
        }
        var sites = Index(workspace, catalog, tables);
        if (requested is { } wanted && (table != (int)wanted.Table || selected != wanted.Index))
        { table = (int)wanted.Table; kind = (int)ReferenceGroup.All; search = ""; selected = wanted.Index; scrollToSelected = true; }
        requested = null;
        ImGui.TextUnformatted("Reward sites");
        ImGui.SameLine();
        ImGui.TextDisabled(sites.Counts);
        StudioUi.Tip(sites.CountsTip);
        StudioUi.Help(tables.Slot84.State == InvestmentSlotState.Omitted
            ? "Indices into root tables this install does not ship. Studio lists who references each one; the rows themselves are not here."
            : "Indices into root slots 84 and 79. Studio lists who references each one; their rows are not decoded.");
        DrawProblems(sites);
        DrawRootSlots(sites);
        DrawFilters();
        var available = ImGui.GetContentRegionAvail();
        float font = ImGui.GetFontSize();
        float listWidth = Math.Clamp(available.X * ListShare, Math.Min(available.X, font * ListMinimumFonts), font * ListMaximumFonts);
        ImGui.BeginChild("site-list", new(listWidth, 0), ImGuiChildFlags.Borders);
        DrawList(sites);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("site-detail", Vector2.Zero, ImGuiChildFlags.None);
        if (selected >= 0 && References(tables, (RewardIndexTable)table, (ushort)selected) is { Count: > 0 } references)
            DrawDetail(context, catalog, sites, references);
        else ImGui.TextDisabled(table == (int)RewardIndexTable.Sites ? "Choose a site" : "Choose a row");
        ImGui.EndChild();
    }

    private SiteIndex Index(StudioWorkspace workspace, InvestmentCatalog catalog, InvestmentTables tables)
    {
        if (index is { } cached && ReferenceEquals(cached.Tables, tables)) return cached;
        var references = tables.References;
        string counts = $"{InvestmentTableViews.Count(references.SiteIndices.Count, "site")}{Dot}"
            + $"{InvestmentTableViews.Count(references.Slot79Indices.Count, "slot 79 row")} referenced";
        string countsTip = $"Root slot {InvestmentRootSlots.Slot84}{Dot}{State(tables.Slot84.State)}{Dot}"
            + $"class 0x{InvestmentTables.Slot84Class:X8}, rows 0x{InvestmentTables.Slot84Stride:X}\n{InvestmentTables.Slot84Note}";
        var problems = tables.Diagnostics.Where(line => line.StartsWith("Reward pools: ", StringComparison.Ordinal))
            .Select(line => "Pool references not read: " + line["Reward pools: ".Length..]).ToList();
        // Item references come from the item definitions the catalog read. An item it could not read adds no references.
        int unread = catalog.Diagnostics.Count(line => line.StartsWith("Item ", StringComparison.Ordinal));
        if (unread != 0) problems.Add($"Item references not read: {InvestmentTableViews.Count(unread, "item")} could not be read.");
        var referrers = new Dictionary<RewardReference, Referrer>();
        foreach (var reference in references.SiteIndices.SelectMany(references.Sites).Concat(references.Slot79Indices.SelectMany(references.Slot79)))
            referrers.TryAdd(reference, Describe(catalog, tables, reference));
        return index = new(tables, counts, countsTip, RootSlots(tables, workspace.Catalog is { } packages ? packages.ContainsTag : _ => false),
            problems, referrers);
    }

    /** Slot 84, slot 79 when the root has it, then every other omitted slot in slot order. Only slot 84 has a table name. */
    internal static IReadOnlyList<RootSlot> RootSlots(InvestmentTables tables, Func<uint, bool> installed)
    {
        if (tables.Root is not { } root) return [];
        int Total(IReadOnlyList<ushort> indices, Func<ushort, IReadOnlyList<RewardReference>> referrers) => indices.Sum(row => referrers(row).Count);
        var references = tables.References;
        var rows = new List<RootSlot>
        {
            new(InvestmentRootSlots.Slot84, TableNames[(int)RewardIndexTable.Sites], State(tables.Slot84.State),
                $"{Total(references.SiteIndices, references.Sites):N0}")
        };
        if (Slot79 < root.SlotCount)
            rows.Add(new(Slot79, $"Slot {Slot79}", State(root.SlotTag(Slot79), installed), $"{Total(references.Slot79Indices, references.Slot79):N0}"));
        for (int slot = 0; slot < root.SlotCount; slot++)
            if (slot is not (InvestmentRootSlots.Slot84 or Slot79) && root.SlotTag(slot) == InvestmentRoot.OmittedSlot)
                rows.Add(new(slot, $"Slot {slot}", State(root.SlotTag(slot), installed), InvestmentTableViews.NoValue));
        return rows;
    }

    private static string State(InvestmentSlotState state) => state switch
    {
        InvestmentSlotState.Omitted => "Not in this install",
        InvestmentSlotState.Loaded => "Present",
        InvestmentSlotState.WrongClass => "Unexpected class",
        _ => "Not installed"
    };

    private static string State(uint tag, Func<uint, bool> installed) =>
        tag == InvestmentRoot.OmittedSlot ? "Not in this install" : installed(tag) ? "Present" : "Not installed";

    /** The referrer's name and what it is: an item and the field naming the row, or a pool entry and its pool. */
    private static Referrer Describe(InvestmentCatalog catalog, InvestmentTables tables, RewardReference reference) => reference.Kind switch
    {
        RewardReferenceKind.PoolEntry => new(reference, $"Pool {reference.Owner}, entry {reference.Element}",
            reference.Owner < tables.Pools.Count ? RewardPoolsPage.Label(catalog, tables, reference.Owner) : ""),
        RewardReferenceKind.CompletionPrimary => new(reference, InvestmentTableViews.ItemLabel(catalog, reference.Owner), "Completion (primary)"),
        RewardReferenceKind.CompletionSecondary => new(reference, InvestmentTableViews.ItemLabel(catalog, reference.Owner), "Completion (secondary)"),
        RewardReferenceKind.Acquire => new(reference, InvestmentTableViews.ItemLabel(catalog, reference.Owner), "Acquire"),
        _ => new(reference, InvestmentTableViews.ItemLabel(catalog, reference.Owner), $"Socket {reference.Element}")
    };

    private static IReadOnlyList<RewardReference> References(InvestmentTables tables, RewardIndexTable table, ushort row) =>
        table == RewardIndexTable.Sites ? tables.References.Sites(row) : tables.References.Slot79(row);

    private static ReferenceGroup GroupOf(RewardReferenceKind kind) => kind switch
    {
        RewardReferenceKind.CompletionPrimary or RewardReferenceKind.CompletionSecondary => ReferenceGroup.Completions,
        RewardReferenceKind.PoolEntry => ReferenceGroup.PoolEntries,
        RewardReferenceKind.Acquire => ReferenceGroup.Acquire,
        _ => ReferenceGroup.SocketLanes
    };

    private static string GroupWord(ReferenceGroup group) => group switch
    {
        ReferenceGroup.Completions => "Completion", ReferenceGroup.PoolEntries => "Pool entry", ReferenceGroup.Acquire => "Acquire", _ => "Socket lane"
    };

    /** What Sunrise does with references of this group, as far as Studio knows. SiteTip gives one pool entry's outcome. */
    private static string GroupTip(ReferenceGroup group, RewardIndexTable table, InvestmentTables tables) => group switch
    {
        ReferenceGroup.Completions => "Sunrise reads it as a completion operation and reports the operation body unavailable.",
        ReferenceGroup.PoolEntries => tables.Slot84.State == InvestmentSlotState.Omitted
            ? "Sunrise skips these references, since this install does not ship root slot 84." : "Sunrise refuses a draw that picks one of these rows.",
        _ when table == RewardIndexTable.Slot79 => "Studio reads this index as a row of root slot 79" + Inferred,
        _ => "Studio reads this index as a reward site reference" + Inferred
    };

    private static void DrawProblems(SiteIndex sites)
    {
        if (sites.Problems.Count == 0) return;
        ImGui.PushStyleColor(ImGuiCol.Text, StudioTheme.Failure);
        bool open = ImGui.CollapsingHeader($"Problems ({sites.Problems.Count})###site-problems");
        ImGui.PopStyleColor();
        for (int i = 0; open && i < sites.Problems.Count; i++) ImGui.TextWrapped(sites.Problems[i]);
    }

    /** Draws the ROOT SLOTS table, at most SlotsShare of the page tall. */
    private static void DrawRootSlots(SiteIndex sites)
    {
        StudioUi.Heading("ROOT SLOTS", "Root slots this install does not ship, with slots 84 and 79 always listed.");
        float row = ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().CellPadding.Y;
        float height = Math.Min((sites.Slots.Count + 1) * row + ImGui.GetStyle().CellPadding.Y, ImGui.GetContentRegionAvail().Y * SlotsShare);
        if (!ImGui.BeginTable("root-slots", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new(0, height)))
            return;
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Table", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("References", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        foreach (var slot in sites.Slots)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(slot.Slot.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); ImGui.TextUnformatted(slot.Table);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(slot.State);
            ImGui.TableNextColumn();
            if (slot.References == InvestmentTableViews.NoValue) ImGui.TextDisabled(slot.References); else ImGui.TextUnformatted(slot.References);
        }
        ImGui.EndTable();
    }

    private void DrawFilters()
    {
        float padding = ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(TableNames.Max(name => ImGui.CalcTextSize(name).X) + padding);
        if (ImGui.Combo("##site-table", ref table, TableNames, TableNames.Length)) selected = -1;
        StudioUi.Tip("Which root table the listed indices name");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(KindNames.Max(name => ImGui.CalcTextSize(name).X) + padding);
        ImGui.Combo("##site-kind", ref kind, KindNames, KindNames.Length);
        StudioUi.Tip("Which references to list");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##find-site", "Find index or item", ref search, 256);
    }

    /**
     * Draws the referenced rows in index order, each with its reference count, kinds and first referrer. The kind filter
     * narrows both the rows and their referrers; the search narrows the rows.
     */
    private unsafe void DrawList(SiteIndex sites)
    {
        var tables = sites.Tables;
        var current = (RewardIndexTable)table;
        IEnumerable<Referrer> Matching(ushort row) => References(tables, current, row)
            .Where(reference => (ReferenceGroup)kind == ReferenceGroup.All || GroupOf(reference.Kind) == (ReferenceGroup)kind)
            .Select(reference => sites.Referrers[reference]);
        if (!ReferenceEquals(shownKey.Index, sites) || shownKey.Table != table || shownKey.Kind != kind || shownKey.Search != search)
        {
            shownKey = (sites, table, kind, search);
            var indices = current == RewardIndexTable.Sites ? tables.References.SiteIndices : tables.References.Slot79Indices;
            shown = indices.Where(row => Matching(row).Any(referrer => search.Length == 0
                || row.ToString(CultureInfo.InvariantCulture).StartsWith(search, StringComparison.Ordinal)
                || referrer.Text.Contains(search, StringComparison.OrdinalIgnoreCase) || referrer.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        if (shown.Length == 0) { ImGui.TextDisabled("No referenced row matches."); return; }
        if (!ImGui.BeginTable("sites", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Index", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("References", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Kinds", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("First referrer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        // Scroll by whole rows, so the frozen header never covers half a row.
        float rowHeight = ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().CellPadding.Y;
        int focused = scrollToSelected ? Array.IndexOf(shown, (ushort)Math.Clamp(selected, 0, ushort.MaxValue)) : -1;
        if (focused >= 0) ImGui.SetScrollY(Math.Max(0, focused - (int)(ImGui.GetWindowHeight() / rowHeight) / CentreDivisor) * rowHeight);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(shown.Length);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ushort row = shown[i];
                var referrers = Matching(row).ToArray();
                ImGui.PushID(row);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Selectable(row.ToString(CultureInfo.InvariantCulture), row == selected, ImGuiSelectableFlags.SpanAllColumns)) selected = row;
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{referrers.Length:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", referrers.Select(referrer => GroupOf(referrer.Reference.Kind)).Distinct().Order().Select(GroupWord)));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(referrers[0].Text);
                StudioUi.Tip($"{referrers[0].Text}\n{referrers[0].Detail}");
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndTable();
        scrollToSelected = false;
    }

    /** Draws every referrer of the selected row, grouped by kind and clipped, since one slot 79 row has over a thousand. */
    private unsafe void DrawDetail(StudioPageContext context, InvestmentCatalog catalog, SiteIndex sites, IReadOnlyList<RewardReference> references)
    {
        var tables = sites.Tables;
        var current = (RewardIndexTable)table;
        string title = current == RewardIndexTable.Sites ? $"Site {selected}" : $"Slot {Slot79} row {selected}";
        ImGui.TextUnformatted(title);
        string count = InvestmentTableViews.Count(references.Count, "reference");
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(count).X));
        ImGui.TextDisabled(count);
        foreach (var group in references.GroupBy(reference => GroupOf(reference.Kind)).OrderBy(group => group.Key))
        {
            StudioUi.Heading($"{KindNames[(int)group.Key].ToUpperInvariant()} ({group.Count():N0})", GroupTip(group.Key, current, tables));
            var rows = group.ToArray();
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(rows.Length, ImGui.GetFrameHeightWithSpacing());
            while (clipper.Step())
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++) DrawReferrer(context, catalog, tables, icons, sites.Referrers[rows[i]], current);
            clipper.End(); clipper.Destroy();
        }
    }

    /** The Quest or Pursuit page that shows a completion referrer's pursuit, or null for any other reference. */
    internal static string? CompletionPage(InvestmentTables tables, InvestmentCatalog catalog, RewardReference reference) =>
        reference.Kind is not (RewardReferenceKind.CompletionPrimary or RewardReferenceKind.CompletionSecondary) ? null
        : tables.PursuitShape(reference.Owner) switch
        {
            InvestmentPursuitShape.Quest => QuestPage.PageId,
            InvestmentPursuitShape.Single when catalog.Items[reference.Owner].Bucket == InvestmentPursuits.PursuitBucket => PursuitPage.PageId,
            _ => null
        };

    /**
     * Draws one referrer: its icon, name and kind, and a link to the page that shows the reference. A pool entry has no icon
     * but keeps its room, so the names in a group line up.
     */
    private static void DrawReferrer(StudioPageContext context, InvestmentCatalog catalog, InvestmentTables tables, InvestmentIconCache icons,
        Referrer referrer, RewardIndexTable table)
    {
        var workspace = context.Workspace;
        var reference = referrer.Reference;
        ImGui.PushID($"{reference.Kind}-{reference.Owner}-{reference.Element}");
        bool item = reference.Kind != RewardReferenceKind.PoolEntry && reference.Owner < catalog.Items.Count;
        icons.Draw(catalog, item ? reference.Owner : -1, ImGui.GetFrameHeight());
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (item)
        {
            var owner = catalog.Items[reference.Owner];
            ImGui.TextColored(StudioTheme.Rarity(owner.Tier), owner.Name);
            StudioUi.Tip($"Item {owner.Index}{Dot}{owner.Hash:X8}");
        }
        else ImGui.TextUnformatted(referrer.Text);
        uint poolTable = tables.TableTag(InvestmentRootSlots.Pools);
        // A completion opens on the Quest or Pursuit page that shows it. Every other item reference opens its Rewards page.
        string page = !item ? "" : CompletionPage(tables, catalog, reference) ?? ItemRewardsPage.PageId;
        string link = item ? page switch { QuestPage.PageId => "Open in Quest", PursuitPage.PageId => "Open in Pursuit", _ => "Open in Rewards" }
            : reference.Kind == RewardReferenceKind.PoolEntry && InvestmentTableViews.Usable(poolTable) ? "Open in Reward pools" : "";
        // The detail gives way to the link, so the link stays inside the column.
        var style = ImGui.GetStyle();
        float linkWidth = link.Length == 0 ? 0 : ImGui.CalcTextSize(link).X + style.FramePadding.X * 2 + style.ItemSpacing.X;
        ImGui.SameLine();
        string detail = StudioUi.Fit(referrer.Detail, ImGui.GetContentRegionAvail().X - linkWidth);
        ImGui.TextDisabled(detail);
        string tip = reference.Kind == RewardReferenceKind.PoolEntry && reference.Owner < tables.Pools.Count
            && reference.Element < tables.Pools[reference.Owner].Entries.Count
            ? InvestmentRewardTree.SiteTip(tables, tables.Pools[reference.Owner].Entries[reference.Element]) : GroupTip(GroupOf(reference.Kind), table, tables);
        StudioUi.Tip(detail == referrer.Detail ? tip : $"{referrer.Detail}\n{tip}");
        if (link.Length != 0) ImGui.SameLine();
        if (item && ImGui.SmallButton(link)) { workspace.Open(catalog.Items[reference.Owner].Tag); context.ShowPage?.Invoke(page, null); }
        if (!item && link.Length != 0 && ImGui.SmallButton(link))
        {
            workspace.Open(poolTable);
            context.ShowPage?.Invoke(RewardPoolsPage.PageId, RewardPoolsPage.RowOf(reference.Owner));
        }
        ImGui.PopID();
    }
}
