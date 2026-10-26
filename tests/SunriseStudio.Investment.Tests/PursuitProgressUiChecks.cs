using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;

/**
 * Real frames of Set progress and Complete objectives in Live's Pursuits against a scripted game built from the installation:
 * a quest step whose objectives are all saved rows, which shows no held copy detached and offers both controls connected,
 * both popups opened through the controls' gating and sent through their own requests, the game's refusal of the change, a
 * step with item-lane progress, which offers Set progress only on its saved rows and shows a Set progress result without
 * Complete objectives, and a held bounty, which offers neither. Steps are chosen by rule, so the captured items follow the
 * installation.
 */
internal static class PursuitProgressUiChecks
{
    private const ulong SavedInstance = 0x4000000000000700, MixedInstance = 0x4000000000000800, BountyInstance = 0x4000000000000900;
    // Height added at UI scale 1, so the Overview shows Complete objectives and every objective of the captured steps.
    private const int ProgressRoom = 240;
    // The scripted game refuses every objective change with this label, as when the row moved since Studio read it.
    private const string Refusal = "value_changed";

    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("pursuit-progress", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var (saved, mixed, bounty) = PickSteps(catalog, tables, ui.Work);
        var savedKinds = Kinds(tables, saved.Pursuit);
        // Held reports the last objective complete, so the popup opens on an incomplete one, a value where there is one.
        int valueOrdinal = Math.Max(0, System.Array.IndexOf(savedKinds[..^1], InvestmentProgressKind.Value));
        var mixedKinds = Kinds(tables, mixed.Pursuit);
        int laneOrdinal = System.Array.IndexOf(mixedKinds, InvestmentProgressKind.ItemValue);
        LiveUi.Check(laneOrdinal >= 0, "The mixed step has no item-lane objective.");
        var account = new ScriptedAccount(catalog, tables)
        {
            HeldItems = [(SavedInstance, saved.Item), (MixedInstance, mixed.Item), (BountyInstance, bounty.Item)],
            Pursuits = [Held(tables, SavedInstance, saved, LivePursuitFlags.ObjectivesKnown, LivePursuitReason.ObjectivesIncomplete),
                Held(tables, MixedInstance, mixed, LivePursuitFlags.ConditionsKnown, LivePursuitReason.ItemProgressNotSaved),
                Held(tables, BountyInstance, bounty, LivePursuitFlags.Bounty | LivePursuitFlags.ObjectivesKnown, LivePursuitReason.ObjectivesIncomplete)]
        };
        account.Refusals["objective"] = Refusal;
        var section = ui.Section;
        var inspector = section.Inspector;
        var sent = account.Game.Sent;
        bool ShowingLive(InvestmentItem item) => ui.LiveShown("pursuits") && section.PursuitDetail.Result?.Detail?.Item.Tag == item.Tag;

        // Detached the game reports no held copy, so the inspector shows none and draws neither control.
        ui.Taller(ProgressRoom);
        ui.ShowLive("pursuits", $"item:{saved.Item.Hash:X8}");
        yield return new("the step with no game", () => ShowingLive(saved.Item));
        LiveUi.Check(inspector.ShownCopy(ui.Live, saved.Item) is null, "The detached inspector showed a held copy.");
        ui.Capture("quest-progress-detached.png");

        // Connected, a step whose objectives are all saved rows offers Complete objectives and Set progress on each.
        ui.Attach(account.Game);
        yield return new("the held step", () => ui.Live.Pursuits.Value?.Held.Count == account.Pursuits.Count && ShowingLive(saved.Item)
            && ui.Live.Selected?.Instance == SavedInstance);
        var copy = inspector.ShownCopy(ui.Live, saved.Item)!;
        var offers = PursuitInspector.Offers(tables, saved.Pursuit, copy);
        LiveUi.Check(copy.Instance == SavedInstance && offers.Complete && offers.Set.All(shown => shown),
            "A connected step whose objectives are all saved rows did not offer Complete objectives and Set progress on each.");
        ui.Capture("quest-progress-live.png");
        // Set progress on the value, typed to its target as the Target button does, then Apply: the game refuses the change.
        LiveUi.Check(inspector.OpenSetProgress(ui.Work, tables, saved.Item, saved.Pursuit, copy, valueOrdinal),
            "Set progress did not open on a saved value of the held step.");
        var progress = copy.Objectives[valueOrdinal];
        inspector.SetProgressValue(progress.Target);
        yield return new("the Set progress popup", () => ShowingLive(saved.Item), LiveUi.PopupSettle);
        ui.Capture("quest-set-progress.png");
        var refused = inspector.ApplyProgress(ui.Live, set: true)!;
        yield return new("the refused change", () => refused.Phase == LiveOperationPhase.Refused);
        LiveUi.Check(sent[^1].Is("objective", copy.Instance, copy.ItemHash, copy.MutationSerial, progress.ObjectiveHash, (byte)valueOrdinal,
            progress.Value, progress.Target), $"Set progress sent {sent[^1]} for the shown copy.");
        ui.Capture("quest-progress-refused.png");
        LiveUi.Check(inspector.OpenComplete(ui.Work, tables, saved.Item, saved.Pursuit, copy), "Complete objectives did not open on the held step.");
        yield return new("the Complete objectives popup", () => ShowingLive(saved.Item), LiveUi.PopupSettle);
        ui.Capture("quest-complete-objectives.png");
        var complete = inspector.ApplyProgress(ui.Live, set: false)!;
        yield return new("the completion", () => complete.Phase == LiveOperationPhase.Done);
        LiveUi.Check(sent[^1].Is("complete", copy.Instance, copy.ItemHash, copy.MutationSerial), $"Complete objectives sent {sent[^1]}.");

        // The item lane offers no Set progress and the step no Complete objectives; its saved rows keep Set progress.
        ui.ShowLive("pursuits", $"item:{mixed.Item.Hash:X8}");
        yield return new("the mixed step", () => ShowingLive(mixed.Item) && ui.Live.Selected?.Instance == MixedInstance);
        var mixedOffers = PursuitInspector.Offers(tables, mixed.Pursuit, inspector.ShownCopy(ui.Live, mixed.Item));
        LiveUi.Check(!mixedOffers.Complete && !mixedOffers.Set[laneOrdinal] && Enumerable.Range(0, mixedKinds.Length)
                .Where(ordinal => mixedKinds[ordinal] is InvestmentProgressKind.Flag or InvestmentProgressKind.Value).All(ordinal => mixedOffers.Set[ordinal]),
            "A step with item-lane progress offered Complete objectives or Set progress on the lane, or none on its saved rows.");
        ui.Capture("quest-progress-mixed.png");
        // Without Complete objectives, a Set progress result still shows on the copy's operation line.
        var mixedCopy = inspector.ShownCopy(ui.Live, mixed.Item)!;
        int savedOrdinal = Enumerable.Range(0, mixedKinds.Length).First(ordinal => mixedOffers.Set[ordinal]
            && mixedCopy.Objectives[ordinal].Flags.HasFlag(LivePursuitObjectiveFlags.Known));
        LiveUi.Check(inspector.OpenSetProgress(ui.Work, tables, mixed.Item, mixed.Pursuit, mixedCopy, savedOrdinal),
            "Set progress did not open on a saved row of the step with item-lane progress.");
        inspector.SetProgressValue(mixedCopy.Objectives[savedOrdinal].Value + 1);
        var mixedSet = inspector.ApplyProgress(ui.Live, set: true)!;
        yield return new("the mixed step's refused change on its line", () => mixedSet.Phase == LiveOperationPhase.Refused
            && ShowingLive(mixed.Item) && inspector.ShownOperation == mixedSet);
        ui.Capture("quest-progress-mixed-result.png");
        // A held bounty: this server does not save when a bounty expires, so neither control draws, and neither opens.
        ui.ShowLive("pursuits", $"item:{bounty.Item.Hash:X8}");
        yield return new("the held bounty", () => ShowingLive(bounty.Item) && ui.Live.Selected?.Instance == BountyInstance);
        var bountyCopy = inspector.ShownCopy(ui.Live, bounty.Item)!;
        var bountyOffers = PursuitInspector.Offers(tables, bounty.Pursuit, bountyCopy);
        LiveUi.Check(bountyCopy.Instance == BountyInstance && !bountyOffers.Complete && bountyOffers.Set.All(shown => !shown)
            && !inspector.OpenComplete(ui.Work, tables, bounty.Item, bounty.Pursuit, bountyCopy)
            && PursuitProgress.CompleteRefusal(tables, bounty.Pursuit, bountyCopy, "").Text == PursuitProgress.BountyNotSaved,
            "A held bounty offered or opened Complete objectives or Set progress, or was refused for another reason.");
        ui.Capture("pursuit-progress-bounty.png");
        ui.Taller(0);
        Console.WriteLine($"investment_progress_ui saved={saved.Item.Index}:0x{saved.Item.Hash:X8} value_ordinal={valueOrdinal} "
            + $"mixed={mixed.Item.Index}:0x{mixed.Item.Hash:X8} lane_ordinal={laneOrdinal} bounty={bounty.Item.Index}:0x{bounty.Item.Hash:X8} "
            + $"sent={sent.Count}");
    }

    /**
     * The first quest step (item index order, held in Pursuits, no lifetime, two objectives up to the game's capacity) whose
     * objectives all read one saved row Studio binds as Sunrise does, with a value among them; the first such step with an item-lane objective beside a
     * saved one; and the first single-step bounty whose objectives all read one saved row, so being a bounty alone withholds
     * both controls, else the first single-step bounty.
     */
    private static ((InvestmentItem Item, InvestmentPursuit Pursuit) Saved, (InvestmentItem Item, InvestmentPursuit Pursuit) Mixed,
        (InvestmentItem Item, InvestmentPursuit Pursuit) Bounty) PickSteps(InvestmentCatalog catalog, InvestmentTables tables, StudioWorkspace work)
    {
        var steps = catalog.Items.Where(item => item.Named && item.Bucket == InvestmentPursuits.PursuitBucket)
            .Select(item => (item, pursuit: catalog.ReadItem(item.Tag, work.Catalog!.Read(item.Tag)).Pursuit))
            .Where(row => row.pursuit is { Objectives.Count: > 0 and <= LiveInvestmentProtocol.PursuitObjectiveCapacity }
                && row.pursuit.Objectives.All(index => index < tables.Objectives.Count))
            .Select(row => (row.item, Pursuit: row.pursuit!)).ToArray();
        static bool Saved(InvestmentProgressKind kind) => kind is InvestmentProgressKind.Flag or InvestmentProgressKind.Value;
        // Several objectives, so one can show complete beside those Complete objectives would set.
        bool Quest(InvestmentItem item, InvestmentPursuit pursuit) => tables.PursuitShape(item.Index) == InvestmentPursuitShape.Quest
            && !tables.Expires(item.Index) && pursuit.Objectives.Count > 1;
        var saved = steps.First(row => Quest(row.item, row.Pursuit) && Kinds(tables, row.Pursuit) is var kinds && kinds.All(Saved)
            && kinds.Contains(InvestmentProgressKind.Value));
        var mixed = steps.First(row => Quest(row.item, row.Pursuit) && Kinds(tables, row.Pursuit) is var kinds && kinds.Contains(InvestmentProgressKind.ItemValue)
            && kinds.Any(Saved));
        var bounties = steps.Where(row => tables.PursuitShape(row.item.Index) == InvestmentPursuitShape.Single && tables.Expires(row.item.Index)).ToArray();
        var bounty = bounties.FirstOrDefault(row => Kinds(tables, row.Pursuit).All(Saved), bounties[0]);
        return (saved, mixed, bounty);
    }

    private static InvestmentProgressKind[] Kinds(InvestmentTables tables, InvestmentPursuit pursuit) =>
        [.. pursuit.Objectives.Select(index => Kind(tables, tables.Objectives[index]))];

    /** The objective's progress kind; a flag or value bound to no saved row reads as Other, since the game cannot read it either. */
    private static InvestmentProgressKind Kind(InvestmentTables tables, InvestmentObjective objective)
    {
        var source = tables.ProgressSource(objective);
        return source.Kind is InvestmentProgressKind.Flag or InvestmentProgressKind.Value
            && LiveUnlockSources.Of(tables.Unlocks, source.Instruction) is null ? InvestmentProgressKind.Other : source.Kind;
    }

    /**
     * One held copy as Sunrise reports it: objectives on no saved row unknown, the last of several complete, flags clear and
     * values a third of the way to their target.
     */
    private static LiveHeldPursuit Held(InvestmentTables tables, ulong instance, (InvestmentItem Item, InvestmentPursuit Pursuit) step,
        LivePursuitFlags flags, LivePursuitReason reason)
    {
        var objectives = step.Pursuit.Objectives.Select((index, ordinal) =>
        {
            var objective = tables.Objectives[index];
            var kind = Kind(tables, objective);
            if (kind is not (InvestmentProgressKind.Flag or InvestmentProgressKind.Value))
                return new LivePursuitObjective(objective.Hash, 0, objective.CompletionValue, LivePursuitObjectiveFlags.None);
            if (ordinal == step.Pursuit.Objectives.Count - 1 && ordinal > 0) return new LivePursuitObjective(objective.Hash, objective.CompletionValue, objective.CompletionValue,
                LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete);
            return new LivePursuitObjective(objective.Hash, kind == InvestmentProgressKind.Flag ? 0 : objective.CompletionValue / 3, objective.CompletionValue,
                LivePursuitObjectiveFlags.Known);
        }).ToArray();
        return new(instance, step.Item.Hash, 5, 1, 0, flags, reason, objectives);
    }
}
