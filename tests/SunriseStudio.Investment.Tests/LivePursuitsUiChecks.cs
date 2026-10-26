using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;

/**
 * Real frames of the Quest and Pursuit pages and Live's Pursuits against a scripted game: the board and the quest's chain
 * detached; the Quest page offline with an objective's sources and their uses, and the Pursuit page offline; then connected
 * the Quest page, which reads nothing from the game, its Open in Live, the held quest's chain and a step it does not hold,
 * the sources' current values, the Pursuit page's Open in Live, the board's quests and bounties, a bounty chosen on the board,
 * and the board of a build that reports no pursuit progress.
 */
internal static class LivePursuitsUiChecks
{
    private const ulong QuestInstance = 0x4000000000000300, BountyInstance = 0x4000000000000500, DoneInstance = 0x4000000000000600;
    // The native accumulated state bit for a tracked item (state/account/inventory/item_state.h kTrackedItemFlag).
    private const uint TrackedItemFlag = 0x2;
    // The captured quest's step count bounds: enough steps to show the open one between others, few enough to read every node.
    private const int QuestFewestSteps = 3, QuestMostSteps = 5;
    // Bounties compared for the captured bounty.
    private const int BountyCandidates = 64;

    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("live-pursuits", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var (quest, sourcesObjective, chainRow, unheld, bounty, done) = Pick(ui.Work, catalog, tables);
        var account = new ScriptedAccount(catalog, tables)
        {
            HeldItems = [(QuestInstance, quest), (BountyInstance, bounty), (DoneInstance, done)], Chain = chainRow
        };
        var section = ui.Section;
        var inspector = section.Inspector;
        var chain = section.PursuitChain;
        var questPage = ui.Form.Pages.Pages.OfType<QuestPage>().Single();
        string HeldKey(ulong instance, InvestmentItem item) =>
            $"held:{(int)LiveInventoryScope.CharacterItem}:{instance:X16}:{item.Hash:X8}:{item.Bucket}";
        bool PursuitRead(InvestmentItem item) => section.PursuitDetail.Result?.Detail?.Item.Tag == item.Tag;
        bool QuestShown() => ui.ItemShown(quest, QuestPage.PageId) && questPage.Chain.Steps is { Steps: not null };
        bool ChainShown() => section.PursuitTab == PursuitBoardTab.Chain && chain.Steps.Steps is not null && chain.Shown is not null;

        // Detached, the board keeps both tabs, each with one in-game screen of empty slots, and the inspector shows the step's
        // installed data with no held progress or progress actions.
        ui.ShowLive("pursuits", $"item:{quest.Hash:X8}");
        yield return new("the board with no game", () => ui.LiveShown("pursuits") && PursuitRead(quest));
        LiveUi.Check(section.Cards.Quests.Length + section.Cards.Bounties.Length == 0 && section.Row == $"quests/item:{quest.Hash:X8}",
            $"The detached board listed cards, or Pursuits recorded {section.Row} for the asked-for quest.");
        ui.Capture("live-detached-pursuits.png");
        ui.Narrowest(true);
        yield return new("the board narrow with no game", () => ui.LiveShown("pursuits"));
        ui.Capture("live-detached-pursuits-narrow.png");
        ui.Narrowest(false);
        // Detached, the asked-for quest's chain draws under the attach sentence with no step held.
        ui.ShowLive("pursuits", $"chain/item:{quest.Hash:X8}");
        yield return LiveUi.Icons("the chain with no game", ChainShown, section.Icons, least: 0);
        LiveUi.Check(section.Row == $"chain/item:{quest.Hash:X8}" && chain.Shown!.Nodes.All(node => node.Tint != QuestChainView.HeldTint),
            $"Detached Pursuits recorded {section.Row} for the quest's chain or marked a step held.");
        ui.Capture("live-detached-pursuit-chain.png");

        // Offline, the Quest page lists an objective's sources with no Now column, then the other uses of one saved row.
        ui.Open(quest, QuestPage.PageId);
        yield return LiveUi.Icons("the Quest page", QuestShown, questPage.Icons, QuestFewestSteps);
        LiveUi.Check(!ui.Link.Attached, "Open in Live was enabled on the Quest page with no game attached.");
        ui.Capture("quest-offline.png");
        questPage.Inspector.ToggleSources(sourcesObjective);
        yield return new("the objective's sources", () => QuestShown() && questPage.Inspector.ListedSources.Any(row => row.Ordinal == sourcesObjective));
        ui.Capture("quest-sources.png");
        var objective = questPage.Inspector.ListedSources.First(row => row.Ordinal == sourcesObjective);
        int used = objective.Rows.ToList().FindIndex(row => row.Uses != 0 && row.Binding is not null);
        questPage.Inspector.ToggleUses(catalog, tables, sourcesObjective, used);
        yield return new("a saved row's uses", QuestShown);
        ui.Capture("quest-sources-uses.png");
        ui.Narrowest(true);
        yield return new("the Quest page narrow", QuestShown);
        ui.Capture("quest-narrow.png");
        ui.Narrowest(false);
        ui.Open(bounty, PursuitPage.PageId);
        yield return new("the Pursuit page", () => ui.ItemShown(bounty, PursuitPage.PageId));
        LiveUi.Check(!ui.Link.Attached, "Open in Live was enabled on the Pursuit page with no game attached.");
        ui.Capture("pursuit-offline.png");

        // Connected: the quest held with known progress, a tracked bounty whose progress this server does not save, and a bounty complete.
        account.Pursuits = Held(ui.Work, catalog, tables, quest, bounty, done);
        ui.Attach(account.Game);
        ui.ShowLive("pursuits", $"quests/{HeldKey(QuestInstance, quest)}");
        yield return new("the held pursuits", () => ui.Live.Pursuits.Value?.Held.Count == 3 && ui.LiveShown("pursuits"));
        // Attached, the Quest page draws installed data only: no held tint, held steps or progress, and it asks the game for nothing.
        ui.Form.CloseBrowserCategory();
        ui.Open(quest, QuestPage.PageId);
        yield return new("the Quest page attached", QuestShown);
        ScriptedGame.ForgetAsks(ui.Live);
        yield return new("the Quest page drawn again", QuestShown);
        LiveUi.Check(!ScriptedGame.Asked(ui.Live), "The Quest page asked the game for live data.");
        ui.Capture("quest-connected.png");
        // Open in Live on the quest page: Pursuits takes the step's held copy from the snapshot.
        ui.Live.Selected = null;
        var questTarget = StudioLiveLink.PursuitTarget(quest);
        ui.Link.Open(questTarget.View, questTarget.Row);
        yield return new("Open in Live on the quest", () => ui.LiveShown("pursuits") && ui.Live.Selected?.Instance == QuestInstance && PursuitRead(quest));
        LiveUi.Check(section.Row == $"quests/{HeldKey(QuestInstance, quest)}", $"Pursuits recorded {section.Row} for the held quest.");
        ui.Capture("live-pursuit-progress.png");
        // The held quest's chain marks the one held step; a click on a step the character does not hold shows it in place.
        ui.ShowLive("pursuits", $"chain/{HeldKey(QuestInstance, quest)}");
        yield return LiveUi.Icons("the held quest's chain", () => ChainShown() && ui.Live.Selected?.Instance == QuestInstance, section.Icons, least: 0);
        LiveUi.Check(section.Row == $"chain/{HeldKey(QuestInstance, quest)}"
            && chain.Shown!.Nodes.Count(node => node.Tint == QuestChainView.HeldTint && node.Subtitle.EndsWith("Held", StringComparison.Ordinal)) == 1,
            "The chain did not mark the one held step.");
        ui.Capture("live-pursuit-chain.png");
        ui.Narrowest(true);
        yield return new("the chain narrow", ChainShown);
        ui.Capture("live-pursuit-chain-narrow.png");
        ui.Narrowest(false);
        section.ChooseChainStep(ui.Live, unheld, [.. ui.Live.Held!.All.Where(row => row.BucketId == InvestmentPursuits.PursuitBucket)]);
        yield return new("the step not held", () => PursuitRead(unheld));
        LiveUi.Check(section.Row == $"chain/item:{unheld.Hash:X8}" && ui.Live.Selected?.BucketId != InvestmentPursuits.PursuitBucket,
            $"Pursuits recorded {section.Row} for a chain step that is not held.");
        ui.Capture("live-pursuit-chain-step.png");
        // Connected, each Now cell the game can read shows its saved value, read in batches while it is drawn.
        ui.ShowLive("pursuits", $"quests/{HeldKey(QuestInstance, quest)}");
        yield return new("the held quest again", () => PursuitRead(quest) && ui.Live.Selected?.Instance == QuestInstance);
        if (!inspector.SourcesShown(sourcesObjective)) inspector.ToggleSources(sourcesObjective);
        LiveUnlockSource[] Readable() => [.. inspector.ListedSources.Where(row => row.Ordinal == sourcesObjective)
            .SelectMany(row => row.Rows).Select(row => row.Live).OfType<LiveUnlockSource>()];
        yield return new("the sources' current values", () => Readable() is { Length: > 0 } readable && readable.All(source => ui.Live.Unlock(source) is not null));
        LiveUi.Check(Readable().All(source => ui.Live.Unlock(source) is { Known: true } value && value.Value == account.UnlockValue(source)),
            "The objective's sources did not keep the scripted game's values.");
        ui.Capture("live-pursuit-sources.png");
        ui.Narrowest(true);
        yield return new("the inspector narrow", () => PursuitRead(quest));
        ui.Capture("live-pursuits-narrow.png");
        ui.Narrowest(false);
        // As Open in Live on a quest step the character does not hold.
        ui.ShowLive("pursuits", $"item:{unheld.Hash:X8}");
        yield return new("a step not held", () => PursuitRead(unheld) && !section.PendingFirstCopy);
        LiveUi.Check(ui.Live.Selected?.BucketId != InvestmentPursuits.PursuitBucket && section.Row == $"quests/item:{unheld.Hash:X8}",
            $"Pursuits recorded {section.Row} for a step that is not held.");
        ui.Capture("live-pursuit-not-held.png");
        // The item page of a quest step keeps one line that links to the Quest page.
        ui.Open(quest);
        yield return new("the quest's item page", () => ui.ItemShown(quest));
        ui.Capture("live-quest.png");
        // Open in Live on the bounty's page shows its held copy on the Bounties tab.
        ui.Open(bounty, PursuitPage.PageId);
        yield return new("the Pursuit page attached", () => ui.ItemShown(bounty, PursuitPage.PageId));
        ui.Capture("pursuit-connected.png");
        var bountyTarget = StudioLiveLink.PursuitTarget(bounty);
        ui.Link.Open(bountyTarget.View, bountyTarget.Row);
        yield return new("Open in Live on the bounty", () => ui.LiveShown("pursuits") && ui.Live.Selected?.Instance == BountyInstance && PursuitRead(bounty));
        LiveUi.Check(section.Row == $"bounties/{HeldKey(BountyInstance, bounty)}", $"Open in Live on the bounty recorded {section.Row}.");
        ui.Capture("live-open-pursuit.png");

        // The game's own flag sorts the quest from the two bounties; the board alone, as history records it, drops the pursuit
        // asked for before.
        ui.ShowLive("pursuits", "quests");
        yield return LiveUi.Icons("the board's quests", () => ui.LiveShown("pursuits") && section.Cards.Quests.Length + section.Cards.Bounties.Length == 3,
            section.Icons);
        LiveUi.Check(section.ChosenPursuit is null && ui.Live.Selected?.BucketId != InvestmentPursuits.PursuitBucket
            && section.Cards.Quests.Single().Item?.Index == quest.Index && section.Cards.Bounties.Length == 2,
            "The board shown with nothing chosen kept a pursuit, or did not sort the held quest and bounties.");
        ui.Capture("live-pursuits-quests.png");
        ui.ShowLive("pursuits", "bounties");
        yield return new("the board's bounties", () => section.PursuitTab == PursuitBoardTab.Bounties);
        ui.Capture("live-pursuits-bounties.png");
        // As a click on the bounty's cell does: its copy is chosen in Live; the asset page opens only from Open.
        var card = section.Cards.Bounties.Single(row => row.Item?.Index == bounty.Index).Row;
        section.ChooseHeldPursuit(card);
        yield return new("the chosen bounty", () => ui.Live.Selected?.SameRow(card) == true && PursuitRead(bounty));
        LiveUi.Check(ui.LiveShown("pursuits") && section.Row == $"bounties/{HeldKey(BountyInstance, bounty)}",
            $"Choosing a bounty on the board left Live or recorded {section.Row}.");
        ui.Capture("live-pursuit-bounty.png");
        // Without pursuit progress the cards still come from the held rows, placed by the installed lifetime, with no progress drawn.
        account.PursuitProgress = false;
        ui.ShowLive("pursuits", "quests");
        yield return new("the board without progress", () => !ui.Live.Supports(LiveInvestmentOpcode.PursuitSnapshot) && ui.LiveShown("pursuits")
            && section.Cards.Quests.Length == 1 && section.Cards.Bounties.Length == 2);
        LiveUi.Check(section.Cards.Quests.Concat(section.Cards.Bounties).All(row => row is { Status: null, Progress: < 0, Unknown: false }),
            "The board without pursuit progress drew progress.");
        ui.Capture("live-pursuits-noprogress.png");
        Console.WriteLine($"investment_live_pursuits_ui quest=0x{quest.Hash:X8} unheld=0x{unheld.Hash:X8} bounty=0x{bounty.Hash:X8} done=0x{done.Hash:X8}");
    }

    /**
     * A step of a short quest with a described computed objective that reads the most saved rows the game reads, whose chain
     * value the game reads too, so the canvas shows its neighbours and its sources show values; another step of it; of the
     * first named single-step bounties the one with the most described objectives; and a bounty that expires, reported complete.
     */
    private static (InvestmentItem Quest, int Objective, (LiveUnlockSource Source, int Value) Chain, InvestmentItem Unheld, InvestmentItem Bounty,
        InvestmentItem Done) Pick(StudioWorkspace work, InvestmentCatalog catalog, InvestmentTables tables)
    {
        int Readable(InvestmentPursuit pursuit, int ordinal) => pursuit.Objectives[ordinal] is var index && index < tables.Objectives.Count
            && tables.ObjectiveText(index).Length != 0 && tables.ProgressLane(tables.Objectives[index], pursuit) < 0
            ? tables.Unlocks.Reads(tables.Objectives[index].Source).Count(read => LiveUnlockSources.Of(tables.Unlocks, read.Instruction) is not null) : 0;
        LiveUnlockSource? ChainSource(InvestmentQuestSet chain) => LiveUnlockSources.Of(tables.Unlocks, new(InvestmentUnlocks.ValueOpcode, chain.ValueSlot, 0));
        var (quest, pursuit, ordinal) = catalog.Items.Where(item => item.Named && tables.PursuitShape(item.Index) == InvestmentPursuitShape.Quest
                && item.Bucket == InvestmentPursuits.PursuitBucket && !tables.Expires(item.Index))
            .Select(item => (item, catalog.ReadItem(item.Tag, work.Catalog!.Read(item.Tag)).Pursuit))
            .Where(row => row.Pursuit?.Chain is { Steps.Count: >= QuestFewestSteps and <= QuestMostSteps } chain
                && chain.Steps.Any(step => step.ItemIndex == row.item.Index) && ChainSource(chain) is not null)
            .SelectMany(row => Enumerable.Range(0, row.Pursuit!.Objectives.Count).Select(ordinal => (row.item, row.Pursuit, ordinal)))
            .MaxBy(row => Readable(row.Pursuit!, row.ordinal));
        LiveUi.Check(Readable(pursuit!, ordinal) > 0, "No installed quest step reads a saved row the game reads.");
        var chainSet = pursuit!.Chain!;
        var unheld = catalog.Items[chainSet.Steps.First(step => step.ItemIndex != quest.Index).ItemIndex];
        var bounty = catalog.Items.Where(item => item.Named && item.Bucket == InvestmentPursuits.PursuitBucket
                && tables.PursuitShape(item.Index) == InvestmentPursuitShape.Single && tables.Expires(item.Index))
            .Take(BountyCandidates).MaxBy(item => catalog.ReadItem(item.Tag, work.Catalog!.Read(item.Tag)).Pursuit?.Objectives
                .Count(index => tables.ObjectiveText(index).Length != 0) ?? 0)!;
        var done = catalog.Items.First(item => item.Named && item.Bucket == InvestmentPursuits.PursuitBucket && item.Index != bounty.Index
            && tables.PursuitShape(item.Index) == InvestmentPursuitShape.Single && tables.Expires(item.Index));
        LiveUi.Check(quest.Bucket == InvestmentPursuits.PursuitBucket && !tables.Expires(quest.Index) && tables.Expires(bounty.Index),
            "The captured quest and bounty are not held in the Pursuits bucket as one quest and one bounty.");
        return (quest, ordinal, (ChainSource(chainSet)!.Value, chainSet.Steps.First(step => step.ItemIndex == quest.Index).Value), unheld, bounty, done);
    }

    /**
     * The quest with every objective known and incomplete; the bounty as this server reports it, tracked with its objectives
     * unknown because item progress is not saved; and the other bounty with every objective at its target.
     */
    private static IReadOnlyList<LiveHeldPursuit> Held(StudioWorkspace work, InvestmentCatalog catalog, InvestmentTables tables, InvestmentItem quest,
        InvestmentItem bounty, InvestmentItem done)
    {
        IEnumerable<InvestmentObjective> Objectives(InvestmentItem item) => catalog.ReadItem(item.Tag, work.Catalog!.Read(item.Tag)).Pursuit!.Objectives
            .Where(index => index < tables.Objectives.Count).Take(LiveInvestmentProtocol.PursuitObjectiveCapacity).Select(index => tables.Objectives[index]);
        return
        [
            new(QuestInstance, quest.Hash, 1, 1, 0, LivePursuitFlags.ObjectivesKnown | LivePursuitFlags.ConditionsKnown, LivePursuitReason.ObjectivesIncomplete,
                [.. Objectives(quest).Select((row, ordinal) => new LivePursuitObjective(row.Hash, ordinal, row.CompletionValue, LivePursuitObjectiveFlags.Known))]),
            new(BountyInstance, bounty.Hash, 2, 1, TrackedItemFlag, LivePursuitFlags.Bounty | LivePursuitFlags.ConditionsKnown,
                LivePursuitReason.ItemProgressNotSaved,
                [.. Objectives(bounty).Select(row => new LivePursuitObjective(row.Hash, 0, row.CompletionValue, LivePursuitObjectiveFlags.None))]),
            new(DoneInstance, done.Hash, 3, 1, 0, LivePursuitFlags.Bounty | LivePursuitFlags.ConditionsKnown | LivePursuitFlags.ObjectivesKnown
                    | LivePursuitFlags.ObjectivesComplete | LivePursuitFlags.Eligible, LivePursuitReason.ManualActionNotEstablished,
                [.. Objectives(done).Select(row => new LivePursuitObjective(row.Hash, row.CompletionValue, row.CompletionValue,
                    LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete))])
        ];
    }
}
