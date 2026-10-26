using System.Diagnostics;
using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Viewport;

/** The workspace's investment session against a scripted game: what it reads, drops, sends and never sends again. */
internal static class LiveInvestmentSessionChecks
{
    private const ulong Account = 0x4000000000000002, Warlock = 0x4000000000000003, Hunter = 0x4000000000000004;
    private const ulong Content = 0x1F2E3D4C5B6A7988, Started = ScriptedGame.Started;
    private const uint ItemHash = 0x80A10001;
    private static readonly LiveInvestmentContext Bound = new(Account, Warlock, Content);
    private static readonly ulong Everything = ScriptedGame.Capabilities();
    private static readonly LiveInvestmentReadiness Ready = ScriptedGame.Ready;
    private const LiveInvestmentPublication Owned = ScriptedGame.Owned;

    public static void Run()
    {
        var fake = new FakeInvestmentLink();
        var opened = new List<int>();
        using var session = new LiveInvestmentSession { OpenLink = id => { opened.Add(id); return fake; } };
        long now = 10_000;

        // Detached, the session opens nothing however long it polls; Attach binds the given game and nothing else.
        now += 10 * LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        Check(session.State == LiveInvestmentState.Detached && !session.Attached && !session.Requesting && opened.Count == 0,
            "A detached session opened a link.");
        session.Attach(new LiveProcess(42, ""));
        Check(session.State == LiveInvestmentState.Connecting && session.Attached && session.Process?.Id == 42 && opened.SequenceEqual([42]),
            "Attach did not bind exactly the given game.");
        Check(Next(session, fake, now) == "hello", "Connecting did not ask who the game is.");
        Check(session.Blocked(LiveInvestmentOpcode.GrantItem, LiveAction.ItemKey(ItemHash), LiveInvestmentReadiness.Items)
            == "The game is not connected.", "An action was allowed before the game answered.");
        Answer(session, fake, now, Hello(Bound, 2, Everything & ~LiveInvestmentProtocol.Capability(LiveInvestmentOpcode.GrantItem),
            LiveInvestmentReadiness.None, LiveInvestmentPublication.None));
        Check(session.Connected && session.Character?.CharacterClass == 2 && session.GameStartedAt == DateTime.FromFileTime((long)Started),
            "The hello was not adopted.");
        Gating(session, fake, ref now);

        // A view is read only while a page wants it, and a reply bound to another character is dropped.
        session.Want(LiveViewKind.Inventory);
        Check(Next(session, fake, now) == "inventory", "A wanted inventory was not read.");
        Answer(session, fake, now, Inventory(Bound, 5));
        Check(session.Inventory.Value?.Items.Single().Quantity == 5 && session.StateText.Length == 0, "The inventory was not adopted.");
        session.Selected = LiveHeld.All(session.Inventory.Value!).Single();
        now += 2 * LiveInvestmentSession.RefreshMilliseconds;
        Quiet(session, fake, now, "An unwanted view or an unwatched hello was read.");
        session.Want(LiveViewKind.Inventory);
        // A drawn view also keeps the game's identity fresh, and that comes first.
        Answer(session, fake, now, Hello(Bound, 2, Everything, Ready, Owned), "hello");
        Check(Next(session, fake, now) == "inventory", "A view wanted again was not read again.");
        Answer(session, fake, now, Inventory(Bound with { CharacterSoid = Hunter }, 7));
        Check(session.Inventory.Value is null && session.Selected is null && session.StateText == "Character changed",
            "A snapshot of another character was adopted or kept the old selection.");
        Check(Next(session, fake, now) == "hello", "A stale snapshot did not ask who the game is again.");
        Answer(session, fake, now, Hello(Bound with { CharacterSoid = Hunter }, 1, Everything, Ready, Owned));
        Check(session.Character?.Soid == Hunter && session.StateText == "Character changed", "The new character was not adopted.");

        // A grant runs to a terminal receipt, invalidates its views and is acknowledged.
        var hunter = Bound with { CharacterSoid = Hunter };
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, Inventory(hunter, 1), "inventory");
        var grant = session.Submit(new LiveGrantItem(ItemHash, "Fixture rifle", 3));
        string blocked = session.Blocked(LiveInvestmentOpcode.GrantItem, LiveAction.ItemKey(ItemHash), LiveInvestmentReadiness.Items);
        Check(blocked == "Waiting for the last request.", $"A second action on a pending target was allowed: {blocked}");
        Check(grant.Identity.Expected == hunter && grant.Target.StartsWith("Hunter", StringComparison.Ordinal),
            "The operation is not bound to the selected character.");
        Answer(session, fake, now, FakeInvestmentLink.Receipt(grant.Identity, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Queued),
            $"grant {grant.Identity.Id:X16}");
        Check(grant is { Phase: LiveOperationPhase.Waiting, Text: "Queued" }, "A queued receipt did not wait.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(grant.Identity, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Published),
            $"read {grant.Identity.Id:X16}");
        Check(grant is { Phase: LiveOperationPhase.Done, Text: "Granted 3" } && session.Inventory.InvalidatedAt == now
            && session.Pursuits.InvalidatedAt == now && session.SeasonPass.InvalidatedAt != now, "A terminal grant did not invalidate exactly its views.");
        Check(Next(session, fake, now) == "inventory", "An invalidated view was not read again.");
        Answer(session, fake, now, Inventory(hunter, 4));
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(grant.Identity, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Published),
            $"ack {grant.Identity.Id:X16}");

        // A submit without an answer is unknown and never resent; Check result only reads.
        var lost = session.Submit(new LiveSetExperience(10, 20));
        Check(Next(session, fake, now) == $"experience {lost.Identity.Id:X16}", "The XP action was not sent.");
        fake.Fail(new LiveInvestmentOutcomeUnknownException(lost.Identity, "The game did not answer within 5 seconds."));
        Settle(session, now);
        Check(lost is { Phase: LiveOperationPhase.Unknown, Text: "Outcome unknown" }, "A timed-out action was not unknown.");
        for (int tick = 0; tick < 5; tick++) { now += LiveInvestmentSession.RefreshMilliseconds; Quiet(session, fake, now, null); }
        Check(fake.Calls.Count(call => call == $"experience {lost.Identity.Id:X16}") == 1, "An action was resent after a timeout.");
        session.CheckAgain(lost);
        Answer(session, fake, now, new LiveInvestmentReceipt(lost.Identity.Id, LiveInvestmentOpcode.None, LiveReceiptState.Unknown, "",
            LiveInvestmentWait.None, 0, 0, 0, 0, default), $"read {lost.Identity.Id:X16}");
        Check(lost.Phase == LiveOperationPhase.NotReceived && fake.Calls.Count(call => call.StartsWith("experience", StringComparison.Ordinal)) == 1,
            "Check result did not read only, or did not prove the action was never received.");

        // Refusals keep their label's sentence; a label Studio does not know is shown as sent.
        var refused = session.Submit(new LiveClaimPassRow(3, ItemHash, "Fixture rifle"));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(refused.Identity, LiveInvestmentOpcode.ClaimSeasonPassReward,
            LiveReceiptState.Refused, "rank"), $"claim {refused.Identity.Id:X16}");
        Check(refused.Text == "Refused: The season rank is too low.", $"A known label was not described: {refused.Text}");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(refused.Identity, LiveInvestmentOpcode.ClaimSeasonPassReward,
            LiveReceiptState.Refused, "rank"), $"ack {refused.Identity.Id:X16}");
        var odd = session.Submit(new LiveResetPassClaim(3, ItemHash, "Fixture rifle"));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(odd.Identity, LiveInvestmentOpcode.ResetSeasonPassClaim,
            LiveReceiptState.Refused, "some_new_label"), $"reset {odd.Identity.Id:X16}");
        Check(odd.Text == "Refused: some_new_label", "An unknown label was not shown as sent.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(odd.Identity, LiveInvestmentOpcode.ResetSeasonPassClaim,
            LiveReceiptState.Refused, "some_new_label"), $"ack {odd.Identity.Id:X16}");

        // Detach makes an action in flight unknown.
        var cut = session.Submit(new LiveGrantItem(ItemHash, "Fixture rifle", 1));
        Check(Next(session, fake, now) == $"grant {cut.Identity.Id:X16}", "The last grant was not sent.");
        session.Detach(gameClosed: false);
        Check(cut.Phase == LiveOperationPhase.Unknown && fake.Disposed && session.State == LiveInvestmentState.Detached && !session.Attached
            && session.Inventory.Value is null && session.CheckBlocked(cut) == "The game is not connected.",
            "Detach did not end the link and leave the action unknown.");

        // A game that does not answer keeps its link and is retried quietly; no other game is chosen until the next Attach.
        var silent = new FakeInvestmentLink { State = LiveInvestmentLinkState.Unavailable, Diagnostic = "Investment bridge unavailable." };
        var answering = new FakeInvestmentLink();
        session.OpenLink = id => { opened.Add(id); return id == 44 ? answering : silent; };
        session.Attach(new LiveProcess(43, ""));
        Until(session, now, () => session.State == LiveInvestmentState.NoAnswer, "A silent game was not shown as not answering.");
        int silentOpens = opened.Count;
        for (int tick = 0; tick < 5; tick++)
        {
            now += LiveInvestmentSession.RefreshMilliseconds;
            session.Poll(now);
        }
        Check(opened.Count == silentOpens && !silent.Disposed && session.State == LiveInvestmentState.NoAnswer && session.Process?.Id == 43,
            "A silent game lost its link or another game was chosen.");
        session.Attach(new LiveProcess(44, ""));
        Check(silent.Disposed && session.State == LiveInvestmentState.Connecting && session.Process?.Id == 44, "Attach kept the silent game's link.");
        Reconnected(session, fake, answering, cut, ref now);

        // A link that closes is lost, and nothing is opened again.
        answering.State = LiveInvestmentLinkState.Closed;
        session.Poll(now);
        Check(session.State == LiveInvestmentState.Lost && session.StateText == LiveInvestmentSession.GameClosed && answering.Disposed,
            "A closed game was not lost.");
        int lostOpens = opened.Count;
        now += 10 * LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        Check(opened.Count == lostOpens && !session.Attached && !session.Requesting, "The session opened a link after the game closed.");

        // The Live tab's exit detection ends in Lost with the same sentence.
        var closing = new FakeInvestmentLink();
        session.OpenLink = id => { opened.Add(id); return closing; };
        session.Attach(new LiveProcess(45, ""));
        session.Detach(gameClosed: true);
        Check(session.State == LiveInvestmentState.Lost && session.StateText == LiveInvestmentSession.GameClosed && closing.Disposed
            && session.Process is null, "Detach after the game closed did not end in Lost.");
        Console.WriteLine($"investment_live_session connect=attach opened={opened.Count} reads=wanted stale=dropped timeout=unknown resend=none "
            + $"check=read_only reconnect=read_only silent=kept closed=lost operations={session.Operations.Count}");
        Gate();
        Unlocks();
        Progress();
        Payloads();
        LazyAttach();
        HeldIndex();
        HeldActions();
        NoCharacter();
    }

    /**
     * Set progress and Complete objectives: sent exactly as read, gated on the capability and on other requests for the same
     * instance, and a finished one drops the pursuit view and every saved row read.
     */
    private static void Progress()
    {
        const ulong instance = 0x4000000000000100;
        const uint objective = 0x80A1C0DE;
        var fake = new FakeInvestmentLink();
        using var workspace = new StudioWorkspace();
        var session = workspace.LiveInvestment;
        session.OpenLink = _ => fake;
        session.Attach(new LiveProcess(41, ""));
        long now = 10_000;
        Until(session, now, () => session.State == LiveInvestmentState.Connecting, "No game was connected.");
        ulong withoutEdits = ScriptedGame.Capabilities(LiveInvestmentOpcode.SetPursuitObjective, LiveInvestmentOpcode.CompletePursuitObjectives);
        Answer(session, fake, now, Hello(Bound, 2, withoutEdits, Ready, Owned), "hello");
        string key = LiveAction.InstanceKey(instance);
        const LiveInvestmentReadiness needs = LiveInvestmentReadiness.Items | LiveInvestmentReadiness.Objectives | LiveInvestmentReadiness.Pursuits;
        foreach (var opcode in new[] { LiveInvestmentOpcode.SetPursuitObjective, LiveInvestmentOpcode.CompletePursuitObjectives })
            Check(session.Blocked(opcode, key, needs) == "This Sunrise build does not support it.", $"{opcode} was allowed by a build without it.");
        Quiet(session, fake, now, "A build without pursuit edits was sent one.");

        ulong edits = Everything;
        Rehello(session, fake, ref now, Hello(Bound, 2, edits, Ready, Owned));
        Check(session.Blocked(LiveInvestmentOpcode.SetPursuitObjective, key, needs).Length == 0, "A ready game blocked Set progress.");
        Rehello(session, fake, ref now, Hello(Bound, 2, edits, Ready & ~LiveInvestmentReadiness.Objectives, Owned));
        Check(session.Blocked(LiveInvestmentOpcode.SetPursuitObjective, key, needs) == "The game is still reading objectives.",
            "Set progress did not wait for the objective catalog.");
        Rehello(session, fake, ref now, Hello(Bound, 2, edits, Ready, Owned));

        // Progress and quantity edits of one instance share its key, so a pending quantity change holds progress back.
        var row = new LiveHeld(LiveInventoryScope.CharacterItem, instance, ItemHash, 1, -3, 0, false);
        Check(LiveAction.HeldKey(row) == key, "A held instance and its pursuit progress do not share a key.");
        var quantity = session.Submit(new LiveSetQuantity(row, "Fixture bounty", 0));
        Check(session.Blocked(LiveInvestmentOpcode.SetPursuitObjective, key, needs) == "Waiting for the last request."
            && session.Blocked(LiveInvestmentOpcode.CompletePursuitObjectives, key, needs) == "Waiting for the last request.",
            "Progress was allowed while a quantity change of the same instance was pending.");
        Answer(session, fake, now, FakeInvestmentLink.Receipt(quantity.Identity, LiveInvestmentOpcode.SetQuantity, LiveReceiptState.Refused,
            "inventory_row"), $"quantity {quantity.Identity.Id:X16}");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(quantity.Identity, LiveInvestmentOpcode.SetQuantity, LiveReceiptState.Refused,
            "inventory_row"), $"ack {quantity.Identity.Id:X16}");

        // The game receives exactly the snapshot's copy, ordinal, hash and value, the whole int32 range included.
        var held = new LiveHeldPursuit(instance, ItemHash, -3, 1, 0, LivePursuitFlags.ObjectivesKnown, LivePursuitReason.ObjectivesIncomplete,
            [new(0x11, 1, 1, LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete), new(objective, -1, 100, LivePursuitObjectiveFlags.Known)]);
        var source = new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Account, 4);
        session.WantUnlock(source);
        Answer(session, fake, now, Values(Bound, [source]), "unlocks");
        var set = session.Submit(new LiveSetObjective(held, 1, objective, false, -1, int.MaxValue - 7, "Fixture bounty",
            "Fixture targets defeated"));
        Check(set.Action.TargetKey == key && set.Action.Summary == "Set Fixture targets defeated on Fixture bounty from -1 to 2,147,483,640",
            $"The set objective names another target or summary: {set.Action.Summary}");
        Check(Next(session, fake, now) == $"objective {set.Identity.Id:X16}"
            && fake.Sent[^1].Is("objective", instance, ItemHash, -3, objective, (byte)1, -1, int.MaxValue - 7),
            $"Set progress was not sent as read: {fake.Sent[^1]}");
        Answer(session, fake, now, FakeInvestmentLink.Receipt(set.Identity, LiveInvestmentOpcode.SetPursuitObjective, LiveReceiptState.Queued));
        Check(session.Unlock(source) is not null && set.Phase == LiveOperationPhase.Waiting, "A queued objective change dropped the saved rows or finished.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(set.Identity, LiveInvestmentOpcode.SetPursuitObjective, LiveReceiptState.Published),
            $"read {set.Identity.Id:X16}");
        Check(set is { Phase: LiveOperationPhase.Done, Text: "Progress set to 2,147,483,640" } && session.Pursuits.InvalidatedAt == now
            && session.Inventory.InvalidatedAt != now && session.Unlock(source) is null,
            "A finished objective change did not invalidate the pursuits and drop the saved rows.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(set.Identity, LiveInvestmentOpcode.SetPursuitObjective, LiveReceiptState.Published),
            $"ack {set.Identity.Id:X16}");

        var flag = new LiveSetObjective(held, 0, 0x11, true, 1, 0, "Fixture bounty", "Fixture flag");
        Check(flag is { Summary: "Clear Fixture flag on Fixture bounty", DoneText: "Objective cleared" }, "A flag change is not worded as one.");

        // Refusals read as the game's sentences.
        (string Label, string Text)[] refusals =
        [
            ("value_changed", "Refused: It changed in the game. Try again."),
            ("item_progress_not_saved", "Refused: This server does not save per-item progress."),
            ("expiry_not_saved", "Refused: This server does not save when a bounty expires."),
            ("objective_target", "Refused: No saved value completes every objective.")
        ];
        foreach (var (label, text) in refusals)
        {
            var complete = session.Submit(new LiveCompleteObjectives(held, "Fixture bounty"));
            Check(complete.Action.TargetKey == key, "Complete objectives names another target.");
            Answer(session, fake, now, FakeInvestmentLink.Receipt(complete.Identity, LiveInvestmentOpcode.CompletePursuitObjectives,
                LiveReceiptState.Refused, label), $"complete {complete.Identity.Id:X16}");
            Check(complete.Text == text, $"{label} reads \"{complete.Text}\".");
            now += LiveInvestmentConnection.ReceiptPollMilliseconds;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(complete.Identity, LiveInvestmentOpcode.CompletePursuitObjectives,
                LiveReceiptState.Refused, label), $"ack {complete.Identity.Id:X16}");
        }
        Console.WriteLine($"investment_live_progress sent=exact unsupported=quiet instance_key=shared refusals={refusals.Length} receipt=cleared");
    }

    /** Saved row reads: nine to a request while drawn, never without the command, and dropped with the character or a finished operation. */
    private static void Unlocks()
    {
        var fake = new FakeInvestmentLink();
        using var workspace = new StudioWorkspace();
        var session = workspace.LiveInvestment;
        session.OpenLink = _ => fake;
        session.Attach(new LiveProcess(41, ""));
        long now = 10_000;
        Until(session, now, () => session.State == LiveInvestmentState.Connecting, "No game was connected.");
        Answer(session, fake, now, Hello(Bound, 2, Everything & ~LiveInvestmentProtocol.Capability(LiveInvestmentOpcode.UnlockValues), Ready, Owned), "hello");
        var sources = Enumerable.Range(0, 14).Select(row => new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Account, (ushort)row)).ToArray();
        foreach (var source in sources) session.WantUnlock(source);
        Quiet(session, fake, now, "Saved rows were read from a build that does not report them.");
        Check(LiveGate.Missing(session, LiveNeed.Unlocks, session.Unlock(sources[0]) is not null, session.UnlockError)
            == ("This Sunrise build does not report current values.", ""), "A build without unlock reads did not say so.");

        Rehello(session, fake, ref now, Hello(Bound, 2, Everything, Ready, Owned));
        Check(LiveGate.Missing(session, LiveNeed.Unlocks, false, session.UnlockError) == ("Reading current values.", ""),
            "A build with unlock reads did not wait for them.");
        foreach (var source in sources) session.WantUnlock(source);
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].Length == LiveInvestmentProtocol.UnlockReadCapacity,
            "The first unlock read did not take a full request.");
        Answer(session, fake, now, Values(Bound, fake.Unlocks[^1]));
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].Length == sources.Length - LiveInvestmentProtocol.UnlockReadCapacity
            && fake.Unlocks[^2].Concat(fake.Unlocks[^1]).OrderBy(source => source.Index).SequenceEqual(sources), "Fourteen rows did not go out as nine then five.");
        Answer(session, fake, now, Values(Bound, fake.Unlocks[^1]));
        Check(sources.All(source => session.Unlock(source) is { Known: true } value && value.Value == Expected(source)) && session.UnlockError.Length == 0,
            "The values were not kept per source.");
        Quiet(session, fake, now, "Fresh rows were read again.");

        // A refresh later only the row still drawn is read again; a failed read keeps the last value and names the error.
        long half = LiveInvestmentSession.RefreshMilliseconds / 2;
        now += half;
        Quiet(session, fake, now, "Rows were read again before a refresh.");
        session.WantUnlock(sources[13]);
        now += half;
        Answer(session, fake, now, Hello(Bound, 2, Everything, Ready, Owned), "hello");
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].SequenceEqual([sources[13]]), "A row no page drew was read again.");
        fake.Fail(new InvalidOperationException("The game is still reading items."));
        Settle(session, now);
        Check(session.Unlock(sources[13]) is not null && session.UnlockError == "The game is still reading items."
            && LiveGate.Missing(session, LiveNeed.Unlocks, true, session.UnlockError) is null, "A failed read dropped the value or hid the error.");

        // Values belong to one character: a change clears them, and an answer for another character is dropped.
        long quarter = LiveInvestmentSession.RefreshMilliseconds / 4;
        now += quarter;
        Quiet(session, fake, now, "A failed row was read again before a refresh.");
        session.WantUnlock(sources[13]);
        Check(session.Unlock(sources[13]) is not null, "A drawn row lost its value.");
        now += LiveInvestmentSession.RefreshMilliseconds - quarter;
        var hunter = Bound with { CharacterSoid = Hunter };
        Answer(session, fake, now, Hello(hunter, 1, Everything, Ready, Owned), "hello");
        Check(session.Unlock(sources[13]) is null && session.UnlockError.Length == 0, "A character change kept the saved row values.");
        session.WantUnlock(sources[0]);
        Answer(session, fake, now, Values(Bound, [sources[0]]), "unlocks");
        Check(session.Unlock(sources[0]) is null, "Values read for another character were kept.");
        Answer(session, fake, now, Hello(hunter, 1, Everything, Ready, Owned), "hello");
        session.WantUnlock(sources[0]);
        Answer(session, fake, now, Values(hunter, [sources[0]]), "unlocks");
        Check(session.Unlock(sources[0]) is { Known: true }, "The new character's value was not kept.");

        // A finished operation may have changed any saved row, so every value is dropped and read again.
        var claim = session.Submit(new LiveClaimPassRow(3, ItemHash, "Fixture rifle"));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(claim.Identity, LiveInvestmentOpcode.ClaimSeasonPassReward, LiveReceiptState.Queued),
            $"claim {claim.Identity.Id:X16}");
        Check(session.Unlock(sources[0]) is not null, "A queued operation dropped the saved row values.");
        now += LiveInvestmentConnection.ReceiptPollMilliseconds;
        Answer(session, fake, now, FakeInvestmentLink.Receipt(claim.Identity, LiveInvestmentOpcode.ClaimSeasonPassReward, LiveReceiptState.Published),
            $"read {claim.Identity.Id:X16}");
        Check(session.Unlock(sources[0]) is null, "A finished claim kept the saved row values.");
        session.WantUnlock(sources[0]);
        Check(Next(session, fake, now) == "unlocks" && fake.Unlocks[^1].SequenceEqual([sources[0]]), "A dropped row was not read again.");
        Console.WriteLine($"investment_live_unlocks batches={fake.Unlocks.Count} capacity={LiveInvestmentProtocol.UnlockReadCapacity} unsupported=quiet "
            + "unwanted=quiet character=cleared receipt=cleared");
    }

    /**
     * Every operation reaches the game with each field as Studio read it, bound to the selected character; a grant the game
     * publishes with a reason stopped part way, and its line is marked as a warning instead of done.
     */
    private static void Payloads()
    {
        const ulong instance = 0x4000000000000100;
        const uint objective = 0x80A1C0DE;
        var (session, fake, workspace) = Connected(Hello(Bound, 2, Everything, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        var stack = new LiveHeld(LiveInventoryScope.CharacterStack, 0, ItemHash, 5, 2, 3, false);
        var held = new LiveHeldPursuit(instance, ItemHash, -3, 1, 0, LivePursuitFlags.ObjectivesKnown, LivePursuitReason.ObjectivesIncomplete,
            [new(0x11, 1, 1, LivePursuitObjectiveFlags.Known | LivePursuitObjectiveFlags.Complete), new(objective, -1, 100, LivePursuitObjectiveFlags.Known)]);
        (LiveAction Action, LiveInvestmentOpcode Opcode, string Kind, object[] Arguments)[] cases =
        [
            (new LiveGrantItem(ItemHash, "Fixture rifle", 3), LiveInvestmentOpcode.GrantItem, "grant", [ItemHash, 3u]),
            (new LiveSetQuantity(stack, "Fixture rifle", 9), LiveInvestmentOpcode.SetQuantity, "quantity",
                [LiveInventoryScope.CharacterStack, 0UL, ItemHash, 2, 5, 9]),
            (new LiveSetQuantity(stack, "Fixture rifle", 0), LiveInvestmentOpcode.SetQuantity, "quantity",
                [LiveInventoryScope.CharacterStack, 0UL, ItemHash, 2, 5, 0]),
            (new LiveSetExperience(10, 20), LiveInvestmentOpcode.SetSeasonalExperience, "experience", [10, 20]),
            (new LiveClaimPassRow(3, ItemHash, "Fixture rifle"), LiveInvestmentOpcode.ClaimSeasonPassReward, "claim", [(ushort)3, ItemHash]),
            (new LiveResetPassClaim(4, ItemHash, "Fixture rifle"), LiveInvestmentOpcode.ResetSeasonPassClaim, "reset", [(ushort)4, ItemHash]),
            (new LiveSetObjective(held, 1, objective, false, -1, int.MaxValue - 7, "Fixture bounty", "Fixture gates opened"),
                LiveInvestmentOpcode.SetPursuitObjective, "objective", [instance, ItemHash, -3, objective, (byte)1, -1, int.MaxValue - 7]),
            (new LiveCompleteObjectives(held, "Fixture bounty"), LiveInvestmentOpcode.CompletePursuitObjectives, "complete", [instance, ItemHash, -3])
        ];
        LiveOperation? done = null;
        foreach (var (action, opcode, kind, arguments) in cases)
        {
            var sent = session.Submit(action);
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, opcode, LiveReceiptState.Published), $"{kind} {sent.Identity.Id:X16}");
            var reached = fake.Sent[^1];
            Check(reached.Is(kind, arguments) && reached.Operation == sent.Identity && sent.Identity.Expected == Bound
                && sent.Phase == LiveOperationPhase.Done, $"{kind} reached the game as {reached}.");
            done ??= sent;
            now += LiveInvestmentConnection.ReceiptPollMilliseconds;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, opcode, LiveReceiptState.Published), $"ack {sent.Identity.Id:X16}");
        }
        var partial = session.Submit(new LiveGrantItem(ItemHash, "Fixture rifle", 3));
        Answer(session, fake, now, FakeInvestmentLink.Receipt(partial.Identity, LiveInvestmentOpcode.GrantItem, LiveReceiptState.Published,
            "editor_disconnected"), $"grant {partial.Identity.Id:X16}");
        Check(partial is { Phase: LiveOperationPhase.Done, StoppedPartWay: true, Text: "Stopped part way: Studio disconnected; the rest was not granted." }
            && !done!.StoppedPartWay && LiveOperationLine.Mark(partial).Icon == StudioIcon.Warning && LiveOperationLine.Mark(done).Icon == StudioIcon.Check,
            $"A grant the game stopped part way reads \"{partial.Text}\" or is marked as done.");
        Console.WriteLine($"investment_live_payloads operations={cases.Length} fields=exact partial=warning");
    }

    /**
     * AttachOnUse keeps the game until a live area is drawn: a held badge never opens it, a drawn live area opens it once, and
     * Detach forgets it.
     */
    private static void LazyAttach()
    {
        using var workspace = new StudioWorkspace();
        var live = workspace.LiveInvestment;
        var opened = new List<int>();
        var fake = new FakeInvestmentLink();
        live.OpenLink = id => { opened.Add(id); return fake; };
        live.AttachOnUse(new LiveProcess(47, ""));
        live.Poll(10_000);
        Check(live.Pending && !live.Attached && live.State == LiveInvestmentState.Detached && !live.Requesting && opened.Count == 0,
            "AttachOnUse opened the link before anything live was drawn.");
        Check(LiveGate.Held(live) is null && live.Pending && opened.Count == 0, "A held badge opened a pending link.");
        var missing = LiveGate.Missing(live, live.Inventory, LiveNeed.Inventory);
        Check(!live.Pending && opened.SequenceEqual([47]) && live.Process?.Id == 47 && missing?.Text == "Connecting to the game.",
            "A drawn live area did not open the pending link.");
        live.Use();
        Check(opened.Count == 1, "Use opened the link a second time.");
        live.Detach(gameClosed: false);
        live.AttachOnUse(new LiveProcess(48, ""));
        live.Detach(gameClosed: false);
        live.Use();
        Check(!live.Pending && !live.Attached && opened.SequenceEqual([47]), "Detach kept a pending attach.");
        Console.WriteLine("investment_live_lazy pending=quiet badge=quiet drawn=opens detach=forgets");
    }

    /**
     * The held rows grouped once per snapshot: every row, by bucket and by item, and a badge that prefers Equipped over a
     * count; the session keeps one index while its snapshot is the same object.
     */
    private static void HeldIndex()
    {
        const uint weapon = 0x80A10002, ammo = 0x80A10003, currency = 0x80A10004;
        var snapshot = new LiveInventorySnapshot(Bound, 1, [],
            [new LiveProfileRow(0x5000000000000001, currency, 1250, 4, 3, true)],
            [new LiveCharacterItem(0x4000000000000101, weapon, 1, 2, 1600, 0, 1, 0, LiveSocketPolicy.NativeDefaults, [], true),
                new LiveCharacterItem(0x4000000000000102, weapon, 1, 3, 1600, 0, 1, LiveInvestmentProtocol.Unequipped,
                    LiveSocketPolicy.NativeDefaults, [], true)],
            [new LiveCharacterStack(ammo, 4, 5, 2), new LiveCharacterStack(ammo, 3, 6, 2)]);
        var index = new LiveHeldIndex(snapshot);
        Check(index.All.SequenceEqual(LiveHeld.All(snapshot)) && index.All.Length == 5, "The index did not keep every held row.");
        Check(index.ByBucket[1].Length == 2 && index.ByBucket[2].Length == 2 && index.ByBucket[3].Single().DefinitionHash == currency
            && !index.ByBucket.ContainsKey(0), "The index did not group rows by bucket.");
        Check(index.Of(weapon).Length == 2 && index.Of(ammo).Sum(row => row.Quantity) == 7 && index.Of(0xDEADBEEF).Length == 0,
            "The index did not group rows by item.");
        Check(index.Badge(weapon) == "Equipped" && index.Badge(ammo) == "x7" && index.Badge(currency) == $"x{1250:N0}" && index.Badge(0xDEADBEEF) == "",
            "A badge did not prefer Equipped, or did not count every held copy.");

        var (session, fake, workspace) = Connected(Hello(Bound, 2, Everything, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        Check(session.Held is null, "An index was built before an inventory was read.");
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, Inventory(Bound, 5), "inventory");
        var first = session.Held;
        Check(first is not null && ReferenceEquals(first, session.Held) && ReferenceEquals(first.Snapshot, session.Inventory.Value),
            "The session built its index again for the same snapshot.");
        now += LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, Hello(Bound, 2, Everything, Ready, Owned), "hello");
        Answer(session, fake, now, Inventory(Bound, 6), "inventory");
        Check(!ReferenceEquals(first, session.Held) && session.Held!.Of(ItemHash).Single().Quantity == 6, "A new snapshot kept the old index.");
        Console.WriteLine("investment_live_held rows=5 buckets=3 badge=equipped_first cache=per_snapshot");
    }

    /** With no character selected, the character's views and saved rows are not read and say why; the account's pass still is. */
    private static void NoCharacter()
    {
        var (session, fake, workspace) = Connected(Hello(Bound with { CharacterSoid = 0 }, null, Everything, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        Check(session.Character is null && !session.Readable(LiveViewKind.Inventory) && !session.Readable(LiveViewKind.Pursuits)
            && !session.Readable(null) && session.Readable(LiveViewKind.SeasonPass), "Readability did not follow the selected character.");
        session.Want(LiveViewKind.Inventory);
        session.Want(LiveViewKind.Pursuits);
        session.WantUnlock(new LiveUnlockSource(LiveUnlockKind.Value, LiveUnlockBank.Character, 1));
        Quiet(session, fake, now, "A character's view was read with no character selected.");
        Check(LiveGate.Missing(session, session.Inventory, LiveNeed.Inventory)?.Text == LiveInvestmentProtocol.Describe("selected_character"),
            "The inventory did not say a character must be selected.");
        session.Want(LiveViewKind.SeasonPass);
        Check(Next(session, fake, now) == "pass", "The account's season pass was not read without a character.");
        Console.WriteLine("investment_live_no_character views=quiet pass=read");
    }

    /** A workspace session linked to a scripted game that has answered this hello. */
    private static (LiveInvestmentSession Session, FakeInvestmentLink Fake, StudioWorkspace Workspace) Connected(LiveInvestmentHello hello)
    {
        var fake = new FakeInvestmentLink();
        var workspace = new StudioWorkspace();
        var session = workspace.LiveInvestment;
        session.OpenLink = _ => fake;
        session.Attach(new LiveProcess(41, ""));
        Answer(session, fake, 10_000, hello, "hello");
        return (session, fake, workspace);
    }

    /** The scripted game's saved value of one row. */
    private static int Expected(LiveUnlockSource source) => source.Index * 10 - 7;

    private static LiveUnlockSnapshot Values(LiveInvestmentContext context, IReadOnlyList<LiveUnlockSource> sources) =>
        new(context, 3, [.. sources.Select(source => new LiveUnlockValue(source, true, Expected(source)))]);

    /** LiveGate's action reason and data line in every link state, each a sentence. */
    private static void Gate()
    {
        using var workspace = new StudioWorkspace();
        var live = workspace.LiveInvestment;
        var fake = new FakeInvestmentLink { State = LiveInvestmentLinkState.Opening, Diagnostic = "Opening the investment bridge." };
        live.OpenLink = _ => fake;
        long now = 10_000;
        var sentences = new List<string>();
        int states = 0;
        void Expect(string state, string reason, (string Text, string Tip)? line)
        {
            string actual = LiveGate.Reason(workspace, LiveInvestmentOpcode.GrantItem, LiveAction.ItemKey(ItemHash),
                LiveInvestmentReadiness.Items);
            var missing = LiveGate.Missing(live, live.Inventory, LiveNeed.Inventory);
            Check(actual == reason, $"{state}: the grant reason reads \"{actual}\".");
            Check(missing == line, $"{state}: the inventory line reads {missing}.");
            sentences.AddRange(new[] { actual, missing?.Text ?? "" }.Where(text => text.Length != 0));
            states++;
        }

        const string seeHeld = "Live shows what you hold once a game is attached.";
        Expect("Detached", LiveGate.AttachSentence, (LiveGate.AttachSentence, seeHeld));
        live.Attach(new LiveProcess(41, ""));
        Until(live, now, () => live.State == LiveInvestmentState.Connecting && live.StateText == fake.Diagnostic, "No game was connected.");
        Expect("Connecting", "Connecting to the game.", ("Connecting to the game.", fake.Diagnostic));
        fake.State = LiveInvestmentLinkState.Unavailable; fake.Diagnostic = "Investment bridge unavailable.";
        Until(live, now, () => live.State == LiveInvestmentState.NoAnswer, "A silent game was not shown as not answering.");
        Expect("NoAnswer before a Hello", "The game is not answering.", ("The game is not answering.", LiveGate.AttachHelp));
        fake.State = LiveInvestmentLinkState.Connected;
        Answer(live, fake, now, Hello(Bound, 2, Everything & ~LiveInvestmentProtocol.Capability(LiveInvestmentOpcode.GrantItem)
            & ~LiveInvestmentProtocol.Capability(LiveInvestmentOpcode.InventorySnapshot), Ready, Owned), "hello");
        Expect("Connected without the commands", "This Sunrise build does not support it.", ("This Sunrise build does not report inventory.", ""));
        Rehello(live, fake, ref now, Hello(Bound, 2, Everything, Ready, Owned));
        Expect("Connected before a snapshot", "", ("Reading inventory.", ""));
        Check(Next(live, fake, now) == "inventory", "A drawn placeholder did not ask for its view.");
        fake.Fail(new InvalidDataException("The inventory layout differs."));
        Settle(live, now);
        Expect("Connected after a failed read", "", ("Inventory not read.", "The inventory layout differs."));
        Rehello(live, fake, ref now, Hello(Bound, 2, Everything, Ready, Owned));
        live.Want(LiveViewKind.Inventory);
        Answer(live, fake, now, Inventory(Bound, 5), "inventory");
        Expect("Connected with a snapshot", "", null);
        Check(LiveGate.Reason(workspace, LiveInvestmentOpcode.GrantItem, LiveAction.ItemKey(ItemHash), LiveInvestmentReadiness.Items,
            ItemHash).Length == 0, "An item with no project change was refused as uninstalled.");
        fake.State = LiveInvestmentLinkState.Unavailable;
        Until(live, now, () => live.State == LiveInvestmentState.NoAnswer, "A game that stopped was not shown as not answering.");
        Expect("NoAnswer after a Hello", "The game stopped answering.", ("The game stopped answering.", LiveGate.StoppedHelp));
        fake.State = LiveInvestmentLinkState.VersionDiffers; fake.Diagnostic = "The game's investment link is version 3.";
        Until(live, now, () => live.State == LiveInvestmentState.Unsupported, "A version mismatch was not shown.");
        Expect("Unsupported", "The game's link version differs.", ("The game's link version differs.", fake.Diagnostic));
        fake.State = LiveInvestmentLinkState.Closed;
        Until(live, now, () => live.State == LiveInvestmentState.Lost, "A closed game was not lost.");
        Expect("Lost", LiveGate.AttachSentence, (LiveGate.AttachSentence, LiveInvestmentSession.GameClosed + " " + seeHeld));
        string? loose = sentences.FirstOrDefault(text => !text.EndsWith('.'));
        Check(loose is null, $"A gate line is not a sentence: {loose}");
        Console.WriteLine($"investment_live_gate states={states} sentences={sentences.Count}");
    }

    /** After another Attach, an action left unknown is never sent again and its receipt is only read. */
    private static void Reconnected(LiveInvestmentSession session, FakeInvestmentLink first, FakeInvestmentLink game, LiveOperation cut, ref long now)
    {
        string send = $"grant {cut.Identity.Id:X16}", read = $"read {cut.Identity.Id:X16}";
        Answer(session, game, now, Hello(Bound, 2, Everything, Ready, Owned), "hello");
        Quiet(session, game, now, "The session sent a request of its own after it attached again.");
        Check(cut.Phase == LiveOperationPhase.Unknown && session.CheckBlocked(cut).Length == 0, "The unknown grant changed when the game answered again.");
        session.CheckAgain(cut);
        Answer(session, game, now, new LiveInvestmentReceipt(cut.Identity.Id, LiveInvestmentOpcode.None, LiveReceiptState.Unknown, "",
            LiveInvestmentWait.None, 0, 0, 0, 0, default), read);
        Check(cut.Phase == LiveOperationPhase.NotReceived, "Check result after the reconnect did not read the receipt.");
        Rehello(session, game, ref now, Hello(Bound, 2, Everything, Ready, Owned, Started + 1));
        Check(session.CheckBlocked(cut) == "The game restarted; its results are gone.", "A restarted game was not named as having lost its results.");
        session.CheckAgain(cut);
        Quiet(session, game, now, "A receipt was read from a restarted game.");
        Check(first.Calls.Count(call => call == send) == 1 && !game.Calls.Contains(send) && game.Calls.Count(call => call == read) == 1,
            "An action was resent after the session attached again.");
    }

    /** Reasons come in the order the user can fix them. */
    private static void Gating(LiveInvestmentSession session, FakeInvestmentLink fake, ref long now)
    {
        string key = LiveAction.ItemKey(ItemHash);
        string Reason() => session.Blocked(LiveInvestmentOpcode.GrantItem, key, LiveInvestmentReadiness.Items);
        Check(Reason() == "This Sunrise build does not support it.", $"Gating did not start with the capability: {Reason()}");
        Rehello(session, fake, ref now, Hello(new(Account, 0, Content), null, Everything, LiveInvestmentReadiness.None, LiveInvestmentPublication.None));
        Check(Reason() == "Select a character in the game first.", $"Gating did not name the missing character: {Reason()}");
        Rehello(session, fake, ref now, Hello(Bound, 2, Everything, LiveInvestmentReadiness.Buckets, LiveInvestmentPublication.None));
        Check(Reason() == "The game is still reading items.", $"Gating did not name readiness: {Reason()}");
        Check(session.StateText == "Character changed", "A character change left no note.");
        Rehello(session, fake, ref now, Hello(Bound, 2, Everything, Ready, LiveInvestmentPublication.PeerPresent));
        Check(session.StateText.Length == 0, "A one-time note outlived the next unchanged hello.");
        Check(Reason() == LiveInvestmentProtocol.Describe("publication_peer"), $"Gating did not name the peer: {Reason()}");
        // The game reports content 0 until it has read its data, and an operation must name the content it was made for.
        Rehello(session, fake, ref now, Hello(Bound with { ContentHash = 0 }, 2, Everything, Ready, Owned));
        Check(Reason() == LiveInvestmentProtocol.Describe("not_ready"), $"Gating allowed an action without the game's content: {Reason()}");
        Rehello(session, fake, ref now, Hello(Bound, 2, Everything, Ready, Owned));
        Check(Reason().Length == 0, $"A ready game still blocked the grant: {Reason()}");
    }

    /**
     * Set quantity and Remove on held rows go through LiveItemActions.Controls, the gating the buttons draw from: an account row's
     * instance has no Remove while a character's instance keeps it, and of two stacks of one item, one of them in its recovery bucket,
     * each choice edits its own stack. An action never sent before Detach is not sent, so nothing changed.
     */
    private static void HeldActions()
    {
        const uint ammo = 0x80A10003, currency = 0x80A10004, armor = 0x80A10005;
        // A bucket and the recovery bucket a stack can also sit in, both invented for this check.
        const byte bucket = 2, lost = 9;
        var (session, fake, workspace) = Connected(Hello(Bound, 2, Everything, Ready, Owned));
        using var owner = workspace;
        long now = 10_000;
        InvestmentItem Item(uint hash, int stack, bool instanced) =>
            new(0, hash, 0, 0, "Fixture item", "", "", bucket, 0, stack, instanced, true);
        session.Want(LiveViewKind.Inventory);
        Answer(session, fake, now, new LiveInventorySnapshot(Bound, 1, [],
            [new LiveProfileRow(0x5000000000000001, armor, 1, 6, 4, true), new LiveProfileRow(0, currency, 900, 7, 4, true)],
            [new LiveCharacterItem(0x4000000000000101, armor, 1, 2, 1600, 0, 3, LiveInvestmentProtocol.Unequipped,
                LiveSocketPolicy.NativeDefaults, [], true)],
            [new LiveCharacterStack(ammo, 40, 5, bucket), new LiveCharacterStack(ammo, 7, 8, lost)]), "inventory");
        var held = session.Held!;
        var armorItem = Item(armor, 1, true);
        var accountInstance = held.Of(armor).Single(row => row.Scope == LiveInventoryScope.Profile);
        var characterInstance = held.Of(armor).Single(row => row.Scope == LiveInventoryScope.CharacterItem);
        var account = LiveItemActions.Controls(workspace, armorItem, accountInstance, null);
        var character = LiveItemActions.Controls(workspace, armorItem, characterInstance, null);
        var plain = LiveItemActions.Controls(workspace, Item(currency, 9999, false), held.Of(currency).Single(), null);
        var actions = new LiveItemActions();
        Check(account is { Remove: false, Quantity: false, Blocked: "" } && character is { Remove: true, Blocked: "" }
            && plain is { Remove: true, Quantity: true, Blocked: "" } && !actions.OpenHeld(workspace, accountInstance, armorItem, remove: true)
            && actions.OpenHeld(workspace, characterInstance, armorItem, remove: true),
            "Remove was offered on an account instance, which Sunrise refuses, or withheld from a character instance or plain stack.");
        Check(!actions.OpenHeld(workspace, characterInstance, armorItem, remove: false), "Set quantity opened on an instance.");

        // Two stacks of one item, one in its recovery bucket: each choice finds and edits its own stack.
        var stacks = held.Of(ammo);
        var ammoItem = Item(ammo, 100, false);
        Check(stacks.Length == 2 && !stacks[0].SameRow(stacks[1]) && stacks[1].SameRow(stacks[1] with { Quantity = 3, MutationSerial = 99 }),
            "Two stacks of one item in different buckets read as one row.");
        // Nothing draws the inventory from here on, so a finished change does not read it again.
        ScriptedGame.ForgetAsks(session);
        var keys = new HashSet<string>();
        foreach (var chosen in stacks)
        {
            session.Selected = chosen;
            var found = stacks.FirstOrDefault(session.Selected.SameRow);
            Check(ReferenceEquals(found, chosen) && actions.OpenHeld(workspace, chosen, ammoItem, remove: false), "The chosen stack was not the one found.");
            actions.SetHeldQuantity(chosen.Quantity + 1);
            var sent = actions.ApplyHeld(session, remove: false)!;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, LiveInvestmentOpcode.SetQuantity, LiveReceiptState.Published),
                $"quantity {sent.Identity.Id:X16}");
            Check(fake.Sent[^1].Is("quantity", LiveInventoryScope.CharacterStack, 0UL, ammo, chosen.MutationSerial, chosen.Quantity, chosen.Quantity + 1),
                $"Editing the stack in bucket {chosen.BucketId} sent {fake.Sent[^1]}.");
            keys.Add($"{chosen.MutationSerial}");
            now += LiveInvestmentConnection.ReceiptPollMilliseconds;
            Answer(session, fake, now, FakeInvestmentLink.Receipt(sent.Identity, LiveInvestmentOpcode.SetQuantity, LiveReceiptState.Published),
                $"ack {sent.Identity.Id:X16}");
        }
        Check(keys.Count == 2, "Both choices edited the same stack.");

        // Detach before an action is sent: it is marked not sent, and the game never receives it.
        var unsent = session.Submit(new LiveGrantItem(ammo, "Fixture item", 1));
        session.Detach(gameClosed: false);
        Check(unsent is { Phase: LiveOperationPhase.NotReceived, Text: "Not sent. Nothing changed." } && !fake.Calls.Contains($"grant {unsent.Identity.Id:X16}"),
            $"An action dropped before it was sent reads \"{unsent.Text}\", or reached the game.");
        Console.WriteLine("investment_live_held_actions account_instance=no_remove character_instance=remove recovery_stack=own_stack unsent=not_sent");
    }

    private static void Rehello(LiveInvestmentSession session, FakeInvestmentLink fake, ref long now, LiveInvestmentHello hello)
    {
        now += LiveInvestmentSession.RefreshMilliseconds;
        session.Poll(now);
        session.Watch();
        Answer(session, fake, now, hello, "hello");
    }

    /** Polls until the condition holds; the session sends requests on its own task. */
    private static void Until(LiveInvestmentSession session, long now, Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            session.Poll(now);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidDataException(message);
            Thread.Sleep(1);
        }
    }

    /** Polls until the session's task has sent its next request, and names it. */
    private static string Next(LiveInvestmentSession session, FakeInvestmentLink fake, long now)
    {
        var timer = Stopwatch.StartNew();
        string? name;
        while ((name = fake.Pending) is null)
        {
            session.Poll(now);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The session sent no request.");
            Thread.Sleep(1);
        }
        return name;
    }

    /** Answers the pending request, optionally checking its name, and polls until the session adopted the answer. */
    private static void Answer(LiveInvestmentSession session, FakeInvestmentLink fake, long now, object value, string? expected = null)
    {
        string name = Next(session, fake, now);
        if (expected is not null) Check(name == expected, $"The session asked {name} instead of {expected}.");
        fake.Reply(value);
        Settle(session, now);
    }

    private static void Settle(LiveInvestmentSession session, long now)
    {
        int revision = session.Revision;
        var timer = Stopwatch.StartNew();
        while (session.Revision == revision)
        {
            session.Poll(now);
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The session did not adopt the answer.");
            Thread.Sleep(1);
        }
    }

    /** A Poll sends nothing: only Poll starts a request, so one that leaves none running sent none. */
    private static void Quiet(LiveInvestmentSession session, FakeInvestmentLink fake, long now, string? message)
    {
        session.Poll(now);
        if (session.Requesting || fake.Pending is not null)
            throw new InvalidDataException(message ?? $"The session sent {fake.Pending ?? "a request"}.");
    }

    private static LiveInvestmentHello Hello(LiveInvestmentContext context, byte? characterClass, ulong capabilities,
        LiveInvestmentReadiness readiness, LiveInvestmentPublication publication, ulong started = Started) =>
        ScriptedGame.Hello(context, characterClass, capabilities, readiness, publication, started);

    private static LiveInventorySnapshot Inventory(LiveInvestmentContext context, int quantity) =>
        new(context, 1, [new LiveBucket(0, 0, 0, 10, 0)], [],
            [new LiveCharacterItem(0x4000000000000100, ItemHash, quantity, 2, 1600, 0, 0, LiveInvestmentProtocol.Unequipped,
                LiveSocketPolicy.NativeDefaults, [], true)], []);

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
