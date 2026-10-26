using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;

/**
 * Real frames of the Assets browser and the table pages: the home with its TABLES rows, each table opened from its row, the
 * Weapons item catalog offline, a click on a catalog row or grid cell with Live and then Assets in front, the held marks
 * with a game attached, a list whose rows are not items, and the Rewards, reward sites and reward pools pages. The plug
 * picker's list is a weapon socket's allowed plugs.
 */
internal static class LiveBrowserUiChecks
{
    // Height added at UI scale 1 for the Assets home capture, so every TABLES row shows without scrolling.
    private const int HomeRoom = 200;
    // Height added at UI scale 1 for the Rewards captures, so all four sections of a short draw table show without scrolling.
    private const int RewardsRoom = 360;
    // Item entries the icon tree capture shows at least, so several icons draw one under another.
    private const int TreeItems = 4;
    // The captured graph's closure size, enough pools to show nesting while every node stays readable.
    private const int GraphPools = 4;

    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("live-browser", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var form = ui.Form;
        var weapons = Picks.Weapons(ui.Work, catalog);
        var stack = Picks.Stack(catalog);
        var account = new ScriptedAccount(catalog, tables) { Stack = stack, Weapons = weapons };
        bool Grid() => form.Settings.BrowserGrid;
        bool CatalogOn(string kind) => form.ItemsOpen && form.ItemBrowser.Kind == kind;

        // Installed content: the home lists the four TABLES rows in place of the table categories, each with rows to open.
        LiveUi.Check(StudioCategories.Tables.Select(row => row.Label).SequenceEqual(["Season pass", "Reward pools", "Reward sites", "Buckets"])
            && StudioCategories.Tables.All(row => ui.Work.CategoryCounts.GetValueOrDefault(row.Category) > 0)
            && StudioCategories.All.Where(item => item.Group == StudioCategories.TablesGroup).All(item => !item.Home),
            "The Assets home does not list the four TABLES rows in place of the table categories.");
        form.ShowItems();
        form.CloseBrowserCategory();
        ui.Taller(HomeRoom);
        yield return new("the Assets home", () => !form.ItemsOpen);
        ui.Capture("browser-home-tables.png");
        ui.Taller(0);
        // Each TABLES row opens its table straight on its page, as a click on the row does.
        // Buckets and Reward sites open on their lists with nothing chosen, so they draw no icons to wait for.
        var tableIcons = new Dictionary<string, InvestmentIconCache>
        {
            [SeasonPassPage.PageId] = form.Pages.Pages.OfType<SeasonPassPage>().Single().Icons,
            [RewardPoolsPage.PageId] = form.Pages.Pages.OfType<RewardPoolsPage>().Single().Icons
        };
        foreach (var row in StudioCategories.Tables)
        {
            form.OpenTable(row);
            bool Opened() => ui.Shown == "asset:" + row.Page && ui.Work.Document?.Entry.TagHash == row.Tag(ui.Work.Catalog!);
            yield return tableIcons.TryGetValue(row.Page, out var icons) ? LiveUi.Icons($"the {row.Label} table", Opened, icons, 3)
                : new UiWait($"the {row.Label} table", Opened);
            ui.Capture($"table-{row.Label.ToLowerInvariant().Replace(' ', '-')}.png");
        }

        // An item category lists in the item catalog; icons are read from the installation, so its offline grid shows them.
        form.SetBrowserGrid(true);
        form.OpenBrowserCategory(InvestmentItemKinds.Weapons);
        yield return LiveUi.Icons("the Weapons grid", () => CatalogOn(InvestmentItemKinds.Weapons) && Grid(), form.BrowserIcons, 3);
        ui.Capture("browser-grid-absent.png");
        // The plug picker lists exactly a weapon socket's allowed plugs, and a click on one chooses it.
        var socket = weapons.SelectMany(weapon => catalog.ReadItem(weapon.Tag, ui.Work.Catalog!.Read(weapon.Tag)).Sockets).First(row => row.AllowedPlugs.Count > 1);
        var plugOptions = InvestmentPage.PlugOptions(socket);
        var plugs = new ItemCatalogView();
        var plug = Click(ui.Work, catalog, plugs, Drawn.Picker, plugOptions);
        LiveUi.Check(plugs.Rows(ui.Work, catalog, plugOptions).Select(item => item.Index).Order()
                .SequenceEqual(socket.AllowedPlugs.Select(index => (int)index).Where(index => index < catalog.Items.Count).Distinct().Order())
            && plug is not null && socket.AllowedPlugs.Contains((ushort)plug.Index),
            $"The plug picker did not list exactly the socket's allowed plugs, or chose {plug?.Name ?? "nothing"}.");

        // A click in the Weapons catalog, on a row and then on a grid cell, first with Live in front and then with Assets: the
        // item opens with its model deferred, the catalog marks its row, Assets comes forward on the Investment page showing
        // it, and Live's Items keeps the item it had chosen.
        ui.ShowLive("items", $"item:{stack.Hash:X8}");
        yield return new("Items in front", () => ui.LiveShown("items"));
        string itemsRow = ui.Section.Row!;
        for (int stage = 0; stage < 4; stage++)
        {
            var target = weapons[stage % 2];
            bool asGrid = stage % 2 == 1, fromLive = stage < 2;
            form.SetBrowserGrid(asGrid);
            if (fromLive) ui.ShowLive("items", null);
            yield return new($"the catalog with {(fromLive ? "Live" : "Assets")} in front",
                () => CatalogOn(InvestmentItemKinds.Weapons) && Grid() == asGrid && (fromLive ? ui.LiveShown("items") : ui.Shown == "asset:" + InvestmentPage.PageId));
            var view = new ItemCatalogView(kindMenu: false) { Kind = InvestmentItemKinds.Weapons, Search = $"{target.Hash:X8}" };
            var clicked = Click(ui.Work, catalog, view, asGrid ? Drawn.Grid : Drawn.Rows, default);
            LiveUi.Check(view.Count(ui.Work, catalog, default) == 1 && clicked?.Index == target.Index,
                $"A click on {target.Name}'s {(asGrid ? "cell" : "row")} chose {clicked?.Name ?? "nothing"}.");
            form.OpenCatalogItem(clicked!);
            yield return new($"{target.Name} opened from the catalog", () => ui.Work.Document?.Entry.TagHash == target.Tag
                && form.ItemBrowser.Chosen == target.Index && ui.ItemShown(target));
            LiveUi.Check(ui.Work.Document!.ModelDeferred && ui.Section.Row == itemsRow,
                $"{target.Name} opened from the browser with its model read, or changed Live's Items to {ui.Section.Row}.");
            ui.Capture($"browser-open-{(fromLive ? "from-live" : "assets")}{(asGrid ? "-grid" : "")}.png");
        }

        // With a game attached the catalog marks what the game holds: the equipped weapon, and the other one's count. Its badges
        // keep the inventory read while it shows, and ask for nothing else.
        ui.Attach(account.Game);
        ui.ShowLive("inventory", null);
        yield return new("the game", () => ui.Live.Connected && ui.Live.Held is not null);
        form.SetBrowserGrid(false);
        ui.Form.ShowAssetPage(InvestmentPage.PageId);
        yield return new("the item page", () => ui.Shown == "asset:" + InvestmentPage.PageId);
        ScriptedGame.ForgetAsks(ui.Live);
        yield return LiveUi.Icons("the Weapons list attached", () => CatalogOn(InvestmentItemKinds.Weapons), form.BrowserIcons, 3);
        LiveUi.Check(ui.Live.Inventory.WantedUntil != 0 && ui.Live.SeasonPass.WantedUntil == 0 && ui.Live.Pursuits.WantedUntil == 0,
            "The browser's item catalog did not keep the inventory read, or asked the game for more.");
        var held = LiveGate.Held(ui.Live)!;
        LiveUi.Check(held.Badge(weapons[0].Hash) == "Equipped" && held.Badge(weapons[1].Hash) == "x1",
            $"The item catalog marked the weapons {held.Badge(weapons[0].Hash)} and {held.Badge(weapons[1].Hash)}.");
        ui.Capture("browser-weapons.png");
        form.SetBrowserGrid(true);
        yield return LiveUi.Icons("the Weapons grid attached", () => CatalogOn(InvestmentItemKinds.Weapons) && Grid(), form.BrowserIcons, 3);
        ui.Capture("browser-grid.png");
        // A list whose rows are not items keeps rows and draws the toggle disabled; Back leaves the item catalog for the home first.
        string other = StudioCategories.All.Select(item => item.Name).First(name => !InvestmentItemKinds.Categories.Contains(name)
            && ui.Work.CategoryRows.GetValueOrDefault(name) is { Length: > 0 } rows && rows.All(row => row.Entry.Reference != InvestmentCatalog.ItemClass));
        form.CloseBrowserCategory();
        form.OpenBrowserCategory(other);
        yield return new($"the {other} list", () => !form.ItemsOpen);
        LiveUi.Check(Grid() && form.AssetGridRefusal() == "Only item lists have icons.", $"{other} offered the grid or changed the grid choice.");
        ui.Capture("browser-other-rows.png");
        form.SetBrowserGrid(false);
        form.CloseBrowserCategory();
        yield return new("the Assets home", () => !form.ItemsOpen);
        ui.Capture("browser-home.png");

        // The site the most pool entries name, so its rows link to Reward pools, opened through its TABLES row.
        var references = tables.References;
        ushort site = references.SiteIndices.MaxBy(index => references.Sites(index).Count(row => row.Kind == RewardReferenceKind.PoolEntry));
        var sitesPage = form.Pages.Pages.OfType<RewardSitesPage>().Single();
        form.OpenTable(StudioCategories.Tables.Single(row => row.Page == RewardSitesPage.PageId));
        form.ShowAssetPage(RewardSitesPage.PageId, RewardSitesPage.RowOf(RewardSitesPage.RewardIndexTable.Sites, site));
        yield return LiveUi.Icons("the reward site", () => ui.Shown == "asset:" + RewardSitesPage.PageId
            && form.ShownRow() == RewardSitesPage.RowOf(RewardSitesPage.RewardIndexTable.Sites, site), sitesPage.Icons, least: 0);
        ui.Capture("reward-sites.png");
        // The slot-79 row the most socket lanes name; its referrers are clipped.
        ushort row79 = references.Slot79Indices.MaxBy(index => references.Slot79(index).Count);
        form.ShowAssetPage(RewardSitesPage.PageId, RewardSitesPage.RowOf(RewardSitesPage.RewardIndexTable.Slot79, row79));
        yield return LiveUi.Icons("the slot-79 row", () => form.ShownRow() == RewardSitesPage.RowOf(RewardSitesPage.RewardIndexTable.Slot79, row79),
            sitesPage.Icons, least: 0);
        ui.Capture("reward-sites-slot79.png");

        var rewards = form.Pages.Pages.OfType<ItemRewardsPage>().Single();
        var (engram, graphed, largest, listed) = RewardItems(catalog, tables);
        ui.Taller(RewardsRoom);
        IEnumerable<UiWait> ShowRewards(InvestmentItem item, bool graph, string capture)
        {
            rewards.GraphView = graph;
            ui.Open(item, ItemRewardsPage.PageId);
            yield return LiveUi.Icons($"{item.Name}'s rewards", () => ui.ItemShown(item, ItemRewardsPage.PageId)
                && ui.Work.Investment?.VendorSalesLoaded == true, rewards.Icons, least: 0);
            ui.Capture(capture + ".png");
        }
        foreach (var wait in ShowRewards(engram, false, "rewards-item")) yield return wait;
        // The item page keeps one Rewards line that links to the Rewards page.
        form.ShowAssetPage(InvestmentPage.PageId);
        yield return new("the engram's item page", () => ui.ItemShown(engram));
        ui.Capture("rewards-row.png");
        // A tree of items, each with its icon, beside nested pools with the pool glyph, at the default size and the narrowest.
        foreach (var wait in ShowRewards(listed, false, "rewards-tree-icons")) yield return wait;
        ui.Narrowest(true);
        yield return new("the tree narrow", () => ui.ItemShown(listed, ItemRewardsPage.PageId));
        ui.Capture("rewards-tree-icons-narrow.png");
        ui.Narrowest(false);
        foreach (var wait in ShowRewards(graphed, true, "rewards-graph")) yield return wait;
        foreach (var wait in ShowRewards(largest, true, "rewards-graph-largest")) yield return wait;
        // A weapon: no draw table, and its socket lanes name reward sites and slot-79 rows.
        rewards.GraphView = false;
        ui.Open(weapons[0], ItemRewardsPage.PageId);
        yield return new("the weapon's rewards", () => ui.ItemShown(weapons[0], ItemRewardsPage.PageId));
        ui.Capture("rewards-empty.png");
        ui.Taller(0);
        var pools = form.Pages.Pages.OfType<RewardPoolsPage>().Single();
        ui.Work.Open(tables.TableTag(InvestmentRootSlots.Pools));
        form.ShowAssetPage(RewardPoolsPage.PageId, RewardPoolsPage.RowOf(tables.WrapperPool(listed.Index)));
        yield return LiveUi.Icons("the wrapper's pool", () => ui.Shown == "asset:" + RewardPoolsPage.PageId, pools.Icons, 3);
        ui.Capture("reward-pools.png");
        ui.Narrowest(true);
        yield return new("the pool narrow", () => ui.Shown == "asset:" + RewardPoolsPage.PageId);
        ui.Capture("reward-pools-narrow.png");
        ui.Narrowest(false);
        Console.WriteLine($"investment_live_browser_ui weapons=0x{weapons[0].Hash:X8},0x{weapons[1].Hash:X8} rows_only={other} site={site} "
            + $"slot79_row={row79} engram=0x{engram.Hash:X8} graph=0x{graphed.Hash:X8} largest=0x{largest.Hash:X8} plugs={plugOptions.Candidates!.Count}");
    }

    /**
     * The Rewards captures' items: an engram with a reward site in its closure, preferring one something gives, then the
     * fewest sites and the shortest pool; a draw table reaching GraphPools pools; the draw table reaching the most pools; and
     * the shortest pool listing TreeItems items, preferring one that also nests a pool.
     */
    private static (InvestmentItem Engram, InvestmentItem Graphed, InvestmentItem Largest, InvestmentItem Listed) RewardItems(InvestmentCatalog catalog,
        InvestmentTables tables)
    {
        IReadOnlyList<int> Closure(InvestmentItem item) => tables.RewardGraph.Closure(tables.WrapperPool(item.Index));
        bool Wraps(InvestmentItem item) => item.Named && tables.WrapperPool(item.Index) is >= 0 and var pool
            && pool < tables.Pools.Count && tables.Pools[pool].Entries.Count != 0;
        int Sites(InvestmentItem item) => Closure(item).Sum(pool => tables.Pools[pool].Entries.Count(entry => entry.Slot84Reference != ushort.MaxValue));
        bool Given(InvestmentItem item) => tables.References.PoolsGiving(item.Index).Count + tables.References.PassRowsOf(item.Index).Count != 0;
        IReadOnlyList<InvestmentRewardEntry> Entries(InvestmentItem item) => tables.Pools[tables.WrapperPool(item.Index)].Entries;
        var engram = catalog.Items.Where(item => Wraps(item) && Sites(item) != 0).OrderBy(item => Given(item) ? 0 : 1).ThenBy(Sites)
            .ThenBy(item => Entries(item).Count).First();
        var graphed = catalog.Items.First(item => Wraps(item) && Closure(item).Count == GraphPools);
        var largest = catalog.Items.Where(item => tables.WrapperPool(item.Index) >= 0).MaxBy(item => Closure(item).Count)!;
        var listed = catalog.Items.Where(item => Wraps(item) && Entries(item).Count(entry => entry.ItemIndex < catalog.Items.Count) >= TreeItems)
            .OrderBy(item => Entries(item).Any(entry => entry.NestedPool != ushort.MaxValue) ? 0 : 1).ThenBy(item => Entries(item).Count).First();
        return (engram, graphed, largest, listed);
    }

    private enum Drawn { Rows, Grid, Picker }

    /**
     * Draws the view in an ImGui context of its own, the way BrowserGridChecks drives a grid, and clicks its first row or cell;
     * a picker draws its bar over its rows, as a field popup shows it. The window's context is current again after it.
     * @return The item the click chose, as the view returns it.
     */
    private static unsafe InvestmentItem? Click(StudioWorkspace work, InvestmentCatalog catalog, ItemCatalogView view, Drawn drawn,
        ItemCatalogOptions options)
    {
        var shown = ImGui.GetCurrentContext();
        var context = ImGui.CreateContext();
        var icons = new InvestmentIconCache();
        try
        {
            ImGui.SetCurrentContext(context);
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(1200, 800); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            ImGui.GetStyle().WindowPadding = Vector2.Zero;
            InvestmentItem? clicked = null;
            void Frame(Vector2 mouse, bool? down = null)
            {
                io.AddMousePosEvent(mouse.X, mouse.Y);
                if (down is { } pressed) io.AddMouseButtonEvent(0, pressed);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("catalog-click", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
                clicked ??= drawn == Drawn.Picker ? view.DrawPicker("picker", work, catalog, icons, options)
                    : view.DrawList("items", work, catalog, icons, 1, drawn == Drawn.Grid, Vector2.Zero, options);
                ImGui.End(); ImGui.Render();
            }
            Frame(new(-100)); Frame(new(-100));
            // Inside the first cell, or the first row's top quarter, which its selectable covers; nothing here has padding, and a
            // picker's rows start under its bar.
            float top = drawn == Drawn.Picker ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y : 0;
            var point = drawn == Drawn.Grid ? IconGrid.CellSize(1) / 2 : new Vector2(IconList.RowHeight(1) * 4, top + IconList.RowHeight(1) / 4);
            Frame(point); Frame(point, true); Frame(point, false);
            return clicked;
        }
        finally { ImGui.DestroyContext(context); ImGui.SetCurrentContext(shown); icons.Dispose(); }
    }
}
