using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Projects;

/**
 * Real frames of a walk through the investment pages and Live's Investment subtabs by the links users follow, then Back to
 * its start and Forward to its end. Every place must show again with the document, page and row it showed when visited.
 * Each frame logs whether work runs, its status and whether Back and Forward are enabled, so a page that keeps Back disabled
 * is named with the read that held it.
 */
internal static class NavigationUiChecks
{
    private const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;
    // Frames a place waits after it shows, so the pages' own reads have started and ended.
    private const int Settle = 5;
    // How long Back or Forward may stay disabled, and how long a place may take to show.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    /** One place of the walk: how its link gets there, and whether the frame shows what the link asked for. */
    private sealed record Place(string Name, Action Go, Func<bool> Arrived);

    /** What a place showed: the open document, the shown page and the page's row. */
    private readonly record struct Shown(uint Document, string Page, string? Row)
    {
        public override string ToString() => $"{Document:X8} {Page} {Row ?? "-"}";
    }

    public static void Run(string install, string temporary, string? captures, string? keys)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckFrames(install, temporary, captures, keys); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Navigation UI check failed.", failure);
    }

    private static void CheckFrames(string install, string temporary, string? captures, string? keys)
    {
        var project = StudioProject.Create(Path.Combine(temporary, "ui-navigation"), "Navigation");
        string settings = Path.Combine(temporary, "ui-navigation-settings.json");
        new StudioSettings { DiskCacheEnabled = false }.Save(settings);
        using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Warp: true, Smoke: true, Frames: 40000,
            WaitForNames: true, SettingsPath: settings, KeySource: keys));
        var section = ScriptedGame.Investment(form);
        var log = new StringBuilder("place,move,frame,ms,ready,busy,blocking,status,back,forward,page,row,document\n");
        var places = new List<Place>();
        var seen = new List<Shown>();
        // The walk visits places 0..n-1, then goes Back to 0, then Forward to n-1; target is the place the current move shows.
        int target = 0, frame = 0, wait = 0;
        string move = "link";
        bool moved = false;
        var clock = Stopwatch.StartNew();
        long moveStart = 0, busyStart = -1, longestBusy = 0, longestWait = 0;
        string longestStatus = "", slowest = "";
        string Page() => (string?)typeof(StudioForm).GetField("shownPage", Access)!.GetValue(form) ?? "";
        Shown Now() => new(form.Workspace.Document?.Entry.TagHash ?? 0, Page(), Row(form, section, Page()));
        object? Call(string method) => typeof(StudioForm).GetMethod(method, Access)!.Invoke(form, []);
        void Start(string next) { move = next; moved = false; wait = 0; moveStart = clock.ElapsedMilliseconds; busyStart = -1; }
        form.FrameRendered += () =>
        {
            var work = form.Workspace;
            frame++;
            if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
            long now = clock.ElapsedMilliseconds;
            if (places.Count != 0 && target < places.Count)
            {
                var shown = Now();
                log.Append(CultureInfo.InvariantCulture, $"{places[target].Name},{move},{frame},{now - moveStart},{form.EditorReady},{work.IsBusy},")
                    .Append(CultureInfo.InvariantCulture, $"{work.IsBlocking},\"{work.Status}\",{work.CanNavigateBack},{work.CanNavigateForward},")
                    .Append(CultureInfo.InvariantCulture, $"{shown.Page},{shown.Row},{shown.Document:X8}\n");
                if (work.IsBusy && busyStart < 0) busyStart = now;
                if (work.IsBusy && now - busyStart >= longestBusy) { longestBusy = now - busyStart; longestStatus = work.Status; }
                if (!work.IsBusy) busyStart = -1;
                if (now - moveStart > Limit.TotalMilliseconds)
                    throw new InvalidDataException($"{move} to {places[target].Name} did not end in {now - moveStart} ms: it shows {shown}"
                        + (move == "link" ? "" : $", recorded {seen[target]}") + $"; Back={work.CanNavigateBack}, Forward={work.CanNavigateForward}, "
                        + $"the longest read was \"{longestStatus}\" for {longestBusy} ms.");
            }
            if (!LiveUi.Catalog(form, out var catalog)) return;
            if (places.Count == 0) { places.AddRange(Walk(form, section, catalog, catalog.Tables())); Start("link"); }
            var place = places[target];
            if (move == "link")
            {
                if (!moved) { place.Go(); moved = true; return; }
                if (!place.Arrived() || ++wait < Settle) return;
                seen.Add(Now());
                if (++target < places.Count) { Start("link"); return; }
                target -= 2; Start("back"); return;
            }
            bool back = move == "back";
            if (!moved)
            {
                if (!(back ? work.CanNavigateBack : work.CanNavigateForward)) return;
                if (now - moveStart > longestWait) { longestWait = now - moveStart; slowest = $"{move}_to_{place.Name}"; }
                Call(back ? "NavigateBack" : "NavigateForward"); moved = true; return;
            }
            if (Now() != seen[target] || ++wait < Settle) return;
            if (back && target == 0) { target = 1; Start("forward"); return; }
            if (!back && target == places.Count - 1)
            {
                Check(!work.CanNavigateForward, "Forward stayed enabled at the end of the walk.");
                target = places.Count; form.Close(); return;
            }
            target += back ? -1 : 1; Start(move);
        };
        Application.Run(form);
        if (captures is not null) File.WriteAllText(Path.Combine(captures, "navigation-walk.csv"), log.ToString());
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(places.Count != 0 && target == places.Count, $"The navigation walk stopped at {(target < places.Count ? places[target].Name : target)} on {move}.");
        Console.WriteLine($"investment_navigation_ui places={places.Count} back=restored forward=restored longest_wait={slowest}:{longestWait}ms "
            + "rows=" + string.Join(",", seen.Where(row => row.Row is not null).Select(row => row.Row)));
    }

    /** The shown page's row: Live's shown section's, else the asset page's. */
    private static string? Row(StudioForm form, LiveInvestmentSection section, string page)
    {
        if (page.StartsWith("live:investment", StringComparison.Ordinal)) return section.Row;
        if (!page.StartsWith("asset:", StringComparison.Ordinal)) return null;
        return form.Pages.Pages.FirstOrDefault(row => row.Id == page["asset:".Length..]) is IStudioRowPage shown ? shown.Row : null;
    }

    /**
     * Every investment page and Live subtab, reached by their links: an engram's Rewards and its pool, an item that wraps it,
     * a second engram's pool, two season pass rows and the item between them, two reward sites with Buckets between them, a
     * quest and another of its steps, a pursuit, then Live's Items, Inventory, Pursuits, two steps on Pursuits' chain, Season
     * pass and Activity, and an item page Live opens.
     */
    private static IEnumerable<Place> Walk(StudioForm form, LiveInvestmentSection section, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var work = form.Workspace;
        string Page() => (string?)typeof(StudioForm).GetField("shownPage", Access)!.GetValue(form) ?? "";
        StudioPageContext Links() => new(work, form.AssetPreviewViewport!, _ => { }, ShowPage: form.ShowAssetPage);
        bool On(uint document, string page) => work.Document?.Entry.TagHash == document && Page() == "asset:" + page;
        bool Read(InvestmentItem item) => work.InvestmentDetailCurrent && work.SelectedInvestmentItem?.Item.Tag == item.Tag;
        uint passTag = tables.TableTag(InvestmentRootSlots.Progressions), poolTag = tables.TableTag(InvestmentRootSlots.Pools);
        uint root = tables.Root!.Tag;
        bool Wraps(InvestmentItem item) => item.Named && tables.WrapperPool(item.Index) is >= 0 and var pool && pool < tables.Pools.Count
            && tables.Pools[pool].Entries.Count != 0 && tables.RewardGraph.WrappersOf(pool).Count > 1;
        var engram = catalog.Items.First(Wraps);
        int pool = tables.WrapperPool(engram.Index);
        var wrapper = catalog.Items[tables.RewardGraph.WrappersOf(pool).First(item => item != engram.Index)];
        var second = catalog.Items.First(item => Wraps(item) && tables.WrapperPool(item.Index) != pool);
        int secondPool = tables.WrapperPool(second.Index);
        var ladder = SeasonPassPage.Largest(tables);
        var passRows = ladder.Rewards.Where(row => row.Error.Length == 0 && row.ItemIndex < catalog.Items.Count).Take(2).ToArray();
        var passItem = catalog.Items[(int)passRows[0].ItemIndex];
        var sites = tables.References.SiteIndices.Take(2).ToArray();
        var bucketItem = catalog.Items.First(item => item.Named && tables.Bucket(item.Bucket) is not null);
        // A quest step whose chain has another step to choose on the Quest page and on Live's chain.
        var quest = catalog.Items.First(item => item.Named && tables.QuestOwner(item.Index) is >= 0 and var owner
            && catalog.ReadQuestSet(owner).Steps is var steps && steps.Any(step => step.ItemIndex == item.Index)
            && steps.Any(step => step.ItemIndex != item.Index && step.ItemIndex < catalog.Items.Count));
        var chain = catalog.ReadQuestSet(tables.QuestOwner(quest.Index));
        var otherStep = chain.Steps.First(step => step.ItemIndex != quest.Index && step.ItemIndex < catalog.Items.Count);
        var other = catalog.Items[otherStep.ItemIndex];
        var bounty = catalog.Items.First(item => item.Named && tables.PursuitShape(item.Index) == InvestmentPursuitShape.Single
            && item.Bucket == InvestmentPursuits.PursuitBucket);
        void Show(InvestmentItem item, string page) { work.Open(item.Tag); form.ShowAssetPage(page); }
        void Follow(RewardLine line) => ItemRewardsPage.Follow(Links(), catalog, tables, line);
        void Live(string view, string? row)
        {
            typeof(StudioForm).GetField("focusLive", Access)!.SetValue(form, true);
            ScriptedGame.Tab(form).Show("investment", view, row);
        }
        bool LiveOn(string view, string? row) => Page() == "live:investment:" + view && section.Row == row;
        string PassRow(int row) => SeasonPassPage.RowOf(ladder.Index, row);
        // A link that opens a table on its page with a row chosen, as Rewards' Open in Season pass does.
        void Table(uint tag, string page, string row) { work.Open(tag); form.ShowAssetPage(page, row); }
        yield return new("rewards", () => Show(engram, ItemRewardsPage.PageId), () => On(engram.Tag, ItemRewardsPage.PageId) && Read(engram));
        yield return new("pool", () => Follow(new($"Pool {pool}", Link: RewardLinkKind.Pool, Target: pool)),
            () => On(poolTag, RewardPoolsPage.PageId) && Row(form, section, Page()) == RewardPoolsPage.RowOf(pool));
        // Used by: Open.
        yield return new("pool-wrapper", () => work.Select(wrapper.Tag), () => work.Document?.Entry.TagHash == wrapper.Tag && Read(wrapper));
        // The reward graph's item node: the item on its Rewards page.
        yield return new("second-rewards", () => Show(second, ItemRewardsPage.PageId), () => On(second.Tag, ItemRewardsPage.PageId) && Read(second));
        yield return new("second-pool", () => Follow(new($"Pool {secondPool}", Link: RewardLinkKind.Pool, Target: secondPool)),
            () => On(poolTag, RewardPoolsPage.PageId) && Row(form, section, Page()) == RewardPoolsPage.RowOf(secondPool));
        yield return new("pass", () => Table(passTag, SeasonPassPage.PageId, PassRow(passRows[0].Index)),
            () => On(passTag, SeasonPassPage.PageId) && Row(form, section, Page()) == PassRow(passRows[0].Index));
        // Season pass: Open item.
        yield return new("pass-item", () => work.Select(passItem.Tag), () => work.Document?.Entry.TagHash == passItem.Tag && Read(passItem));
        yield return new("second-pass", () => Table(passTag, SeasonPassPage.PageId, PassRow(passRows[1].Index)),
            () => On(passTag, SeasonPassPage.PageId) && Row(form, section, Page()) == PassRow(passRows[1].Index));
        yield return new("site", () => Follow(new($"Site {sites[0]}", Link: RewardLinkKind.Site, Target: sites[0])),
            () => On(root, RewardSitesPage.PageId) && Row(form, section, Page()) == RewardSitesPage.RowOf(RewardSitesPage.RewardIndexTable.Sites, sites[0]));
        yield return new("bucket-item", () => Show(bucketItem, InvestmentPage.PageId), () => On(bucketItem.Tag, InvestmentPage.PageId) && Read(bucketItem));
        // The item page's Open in Buckets.
        yield return new("buckets", () => Table(root, BucketsPage.PageId, BucketsPage.RowOf(bucketItem.Bucket)),
            () => On(root, BucketsPage.PageId) && Row(form, section, Page()) == BucketsPage.RowOf(bucketItem.Bucket));
        yield return new("second-site", () => Follow(new($"Site {sites[1]}", Link: RewardLinkKind.Site, Target: sites[1])),
            () => On(root, RewardSitesPage.PageId) && Row(form, section, Page()) == RewardSitesPage.RowOf(RewardSitesPage.RewardIndexTable.Sites, sites[1]));
        yield return new("quest", () => Show(quest, QuestPage.PageId), () => On(quest.Tag, QuestPage.PageId) && Read(quest));
        // A history link to another step of the chain: the same page inspecting that step.
        yield return new("quest-step", () => form.ShowAssetPage(QuestPage.PageId, QuestPage.StepRow(otherStep.Member)),
            () => On(quest.Tag, QuestPage.PageId) && Row(form, section, Page()) == QuestPage.StepRow(otherStep.Member));
        yield return new("pursuit", () => Show(bounty, PursuitPage.PageId), () => On(bounty.Tag, PursuitPage.PageId) && Read(bounty));
        // Each asset page's Open in Live, then the subtabs' own links.
        yield return new("live-items", () => Live(LiveInvestmentSection.ItemsView, $"item:{bucketItem.Hash:X8}"),
            () => LiveOn(LiveInvestmentSection.ItemsView, $"item:{bucketItem.Hash:X8}"));
        yield return new("live-inventory", () => Live(LiveInvestmentSection.InventoryView, $"bucket:{bucketItem.Bucket}"),
            () => LiveOn(LiveInvestmentSection.InventoryView, $"bucket:{bucketItem.Bucket}"));
        yield return new("live-pursuits", () => Live(LiveInvestmentSection.PursuitsView, $"item:{bounty.Hash:X8}"),
            () => Page() == "live:investment:" + LiveInvestmentSection.PursuitsView && section.Row?.EndsWith($"item:{bounty.Hash:X8}") == true);
        // Pursuits' chain with the quest's step chosen, then another of its steps.
        yield return new("live-chain", () => Live(LiveInvestmentSection.PursuitsView, $"chain/item:{quest.Hash:X8}"),
            () => LiveOn(LiveInvestmentSection.PursuitsView, $"chain/item:{quest.Hash:X8}"));
        yield return new("live-chain-step", () => Live(LiveInvestmentSection.PursuitsView, $"chain/item:{other.Hash:X8}"),
            () => LiveOn(LiveInvestmentSection.PursuitsView, $"chain/item:{other.Hash:X8}"));
        yield return new("live-season-pass", () => Live(LiveInvestmentSection.SeasonPassView, PassRow(passRows[1].Index)),
            () => LiveOn(LiveInvestmentSection.SeasonPassView, PassRow(passRows[1].Index)));
        yield return new("live-activity", () => Live(LiveInvestmentSection.ActivityView, null), () => LiveOn(LiveInvestmentSection.ActivityView, null));
        // A subtab's Open in Assets.
        yield return new("live-opened-item", () => Show(passItem, InvestmentPage.PageId), () => On(passItem.Tag, InvestmentPage.PageId) && Read(passItem));
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
