using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Projects;
using SunriseStudio.Rendering;

/**
 * Real frames of the Quest page's chain: the campaign, the one thirty-step chain whose steps each own a copy of it and show
 * no rewards, with its first and its last step inspected; the short chain with the most reward chips on one step; the short
 * chain whose rewards open the most pools; the first two again at the window's narrowest. Every capture waits until the
 * chain's steps are read and its icons arrive.
 */
internal static class QuestChainUiChecks
{
    private const BindingFlags Access = BindingFlags.Instance | BindingFlags.NonPublic;
    // Frames a capture waits after its state is reached, and frames the page's icon count must hold before it.
    private const int Settle = 5, IconSteady = 10;
    // The reward captures' chains have at most this many steps, so their nodes stay readable.
    private const int RewardChainSteps = 5;

    /** One capture: the chain step to open, the step its inspector shows, and whether the window is at its narrowest. */
    private sealed record Shot(string Name, InvestmentItem Open, int Step, bool Narrow);

    public static void Run(string install, string temporary, string? captures, string? keys)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckFrames(install, temporary, captures, keys); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Quest chain UI check failed.", failure);
    }

    private static void CheckFrames(string install, string temporary, string? captures, string? keys)
    {
        var project = StudioProject.Create(Path.Combine(temporary, "ui-chain"), "Quest chain");
        string settings = Path.Combine(temporary, "ui-chain-settings.json");
        new StudioSettings { DiskCacheEnabled = false }.Save(settings);
        using var form = new StudioForm(new(Game: install, Project: project.ManifestPath, Warp: true, Smoke: true, Frames: 20000,
            WaitForNames: true, SettingsPath: settings, KeySource: keys));
        var page = form.Pages.Pages.OfType<QuestPage>().Single();
        var chain = (QuestChainView)typeof(QuestPage).GetField("chain", Access)!.GetValue(page)!;
        var icons = (InvestmentIconCache)typeof(QuestPage).GetField("icons", Access)!.GetValue(page)!;
        string Shown() => (string?)typeof(StudioForm).GetField("shownPage", Access)!.GetValue(form) ?? "";
        var shots = new List<Shot>();
        int shot = -1, wait = 0, steady = 0, count = -1;
        Size? wide = null;
        string summary = "";
        form.FrameRendered += () =>
        {
            var work = form.Workspace;
            if (work.Error.Length != 0) throw new InvalidDataException(work.Error);
            if (!LiveUi.Catalog(form, out var catalog)) return;
            var tables = catalog.Tables();
            if (shot < 0)
            {
                (shots, summary) = Shots(catalog, tables, work.Catalog!);
                shot = 0; Show(form, shots[0], ref wide);
                return;
            }
            var current = shots[shot];
            if (Shown() != "asset:" + QuestPage.PageId || !work.InvestmentDetailCurrent || work.SelectedInvestmentItem?.Item.Tag != current.Open.Tag
                || chain.Steps.Steps is null || page.Row != QuestPage.StepRow(current.Step)) return;
            if (icons.Uploaded != count) { count = icons.Uploaded; steady = 0; }
            if (++steady < IconSteady || ++wait < Settle) return;
            if (captures is not null)
            {
                Directory.CreateDirectory(captures);
                ((Dx11Renderer)typeof(StudioForm).GetField("renderer", Access)!.GetValue(form)!).CapturePng(Path.Combine(captures, current.Name + ".png"));
            }
            if (++shot == shots.Count) { form.Close(); return; }
            wait = 0; steady = 0; count = -1;
            Show(form, shots[shot], ref wide);
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(shot == shots.Count && shots.Count != 0, $"The quest chain captures stopped at {shot} of {shots.Count}.");
        Console.WriteLine($"investment_chain_ui captures={shots.Count} {summary}");
    }

    /** Opens the shot's step on the Quest page with its inspected step, at the default size or the window's narrowest. */
    private static void Show(StudioForm form, Shot shot, ref Size? wide)
    {
        wide ??= form.Size;
        form.Size = shot.Narrow ? wide.Value with { Width = form.MinimumSize.Width } : wide.Value;
        form.Workspace.Open(shot.Open.Tag);
        form.ShowAssetPage(QuestPage.PageId, QuestPage.StepRow(shot.Step));
    }

    /** The captures, each on the step it inspects, and a line naming the chains they show. */
    private static (List<Shot> Shots, string Summary) Shots(InvestmentCatalog catalog, InvestmentTables tables, SunriseStudio.Packages.PackageCatalog packages)
    {
        var owners = InvestmentTableChecks.QuestOwners(catalog, tables);
        int owner = InvestmentTableChecks.Campaign(catalog, tables);
        var campaign = catalog.ReadQuestSet(owner);
        var first = catalog.Items[campaign.Steps[0].ItemIndex];
        // Per chain: the most rewards one step shows, and the steps' rewards that open a pool.
        (int Most, int Pools) Rewards(InvestmentQuestSet chain) => chain.Steps
            .Select(step => catalog.ReadItem(catalog.Items[step.ItemIndex].Tag, packages.Read(catalog.Items[step.ItemIndex].Tag)).Pursuit?.Previews ?? [])
            .Aggregate((0, 0), (sum, previews) => (Math.Max(sum.Item1, previews.Count), sum.Item2 + previews.Count(preview => tables.WrapperPool(preview.ItemIndex) >= 0)));
        var shortChains = owners.Select(owner => catalog.ReadQuestSet(owner)).Where(chain => chain.Steps.Count is > 1 and <= RewardChainSteps && chain.Error.Length == 0)
            .Select(chain => (Chain: chain, Rewards: Rewards(chain))).ToArray();
        var rich = shortChains.MaxBy(row => row.Rewards.Most);
        var pooled = shortChains.MaxBy(row => (row.Rewards.Pools, row.Rewards.Most));
        Check(rich.Rewards.Most > 3 && pooled.Rewards.Pools > 0, "No short chain shows several rewards on a step, or a pool.");
        Shot Last(string name, InvestmentQuestSet chain, bool narrow) => new(name, catalog.Items[chain.Steps[^1].ItemIndex], chain.Steps[^1].Member, narrow);
        return ([
            new("quest-chain-campaign", first, 0, false),
            new("quest-chain-campaign-last", first, campaign.Steps[^1].Member, false),
            new("quest-chain-campaign-narrow", first, 0, true),
            Last("quest-chain-rewards", rich.Chain, false),
            Last("quest-chain-rewards-narrow", rich.Chain, true),
            Last("quest-chain-pools", pooled.Chain, false)
        ], $"campaign={owner}:{campaign.Steps.Count} rewards_chain={rich.Chain.OwnerItem}:{rich.Chain.Steps.Count}:{rich.Rewards.Most} "
            + $"pool_chain={pooled.Chain.OwnerItem}:{pooled.Chain.Steps.Count}:{pooled.Rewards.Pools}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
