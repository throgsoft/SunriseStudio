using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;

/**
 * Real frames of the status bar and Live's attachment: no game, World detached, a game attached that does not answer (World
 * asks once and Investment never starts it), an answering game attached while only an asset page is drawn (its link stays
 * unopened), the connected Activity, the game no longer answering, then its exit.
 */
internal static class LiveStatusUiChecks
{
    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("live-status", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var stack = Picks.Stack(catalog);
        var account = new ScriptedAccount(catalog, tables) { Stack = stack };
        var silent = new FakeInvestmentLink { State = LiveInvestmentLinkState.Unavailable, Diagnostic = "Investment bridge unavailable." };
        var world = ui.Form.LivePage;
        bool exited = false;
        int worldAsks = 0, finds = 0;
        ui.Open(stack);
        // Smoke runs list no games, so the item page and the status bar show the game as not attached.
        yield return new("the item page with no game", () => ui.ItemShown(stack) && ui.Live.State == LiveInvestmentState.Detached);
        LiveUi.Check(!ui.Tab.Attached && !ui.Link.Attached && InvestmentLinkStatus.Headline(ui.Live) == "Not attached",
            "The Live tab attached without Attach, Open in Live was enabled, or the status bar did not say so.");
        ui.Capture("status-not-attached.png");
        ui.Form.ShowAssetPage("Live");
        ui.Tab.Show("world", "", null);
        // Detached, World keeps its row, summary, scene and panel in place, disabled or with their empty lines.
        yield return new("World with no game", () => ui.Shown == "live:world");
        LiveUi.Check(!world.IsAttached && worldAsks == 0, "Detached World connected the world view.");
        ui.Capture("live-detached-world.png");

        // Attached from Live > Investment, whose drawn subtab opens the link, with a game that does not answer. World is no longer
        // drawn, so it does not connect.
        ui.ShowLive("inventory", null);
        yield return new("Inventory with no game", () => ui.LiveShown("inventory"));
        ui.Attach(silent, () => exited, () => worldAsks++);
        ui.Tab.FindGames = _ => { finds++; return []; };
        yield return new("a game that does not answer", () => ui.Live.State == LiveInvestmentState.NoAnswer && ui.LiveShown("inventory"));
        LiveUi.Check(ui.Tab.Attached && ui.Live.Process?.Id == Environment.ProcessId && !world.IsAttached && worldAsks == 0,
            "Attaching for Investment started the world view.");
        ui.Capture("live-attached-investment-only.png");
        ui.Tab.Show("world", "", null);
        // World asks once; its failure waits for Retry and leaves Investment attached.
        yield return new("World after its refusal", () => ui.Shown == "live:world");
        LiveUi.Check(worldAsks == 1 && !world.IsAttached && ui.Tab.Attached && ui.Live.State == LiveInvestmentState.NoAnswer,
            $"World asked for the world view {worldAsks} times or its failure detached the game.");
        ui.Capture("live-world-retry.png");

        // The game that does not answer keeps its link until the user detaches; Attach then binds the answering one. With only the
        // item page and the browser drawn, nothing asks for investment, so its link stays unopened and the game gets no Hello.
        ui.Tab.Detach(gameClosed: false);
        LiveUi.Check(silent.Disposed && ui.Live.State == LiveInvestmentState.Detached && !ui.Tab.Attached, "Detach kept the investment link.");
        ui.Open(stack);
        yield return new("the item page", () => ui.ItemShown(stack));
        ui.Attach(account.Game, () => exited, () => worldAsks++);
        yield return new("the item page with an answering game attached", () => ui.ItemShown(stack));
        LiveUi.Check(ui.Tab.Attached && ui.Live.Pending && account.Game.Calls.Count == 0 && InvestmentLinkStatus.Headline(ui.Live) == "Investment not opened",
            $"Attaching opened the investment link before a live area was drawn: {ui.Live.State}, {account.Game.Calls.Count} requests.");
        ui.Capture("status-pending.png");

        // Live's Activity lists what Studio sent, with the chosen action's line.
        ui.ShowLive("inventory", null);
        yield return new("the answering game", () => ui.Live.Connected && ui.LiveShown("inventory"));
        var grant = ui.Section.Actions.Grant(ui.Live, stack);
        yield return new("the grant", () => grant.Phase == LiveOperationPhase.Done);
        ui.ShowLive("activity", $"op:{grant.Identity.Id:X16}");
        yield return new("Activity", () => ui.LiveShown("activity"));
        LiveUi.Check(ui.Section.Row == $"op:{grant.Identity.Id:X16}" && ui.Live.Operations.Contains(grant), $"Activity recorded {ui.Section.Row} for the grant.");
        ui.Capture("live-activity.png");

        // A game that answered and then stops is described apart from one that never answered.
        account.Game.State = LiveInvestmentLinkState.Unavailable;
        yield return new("the game no longer answering", () => ui.Live.State == LiveInvestmentState.NoAnswer);
        LiveUi.Check(InvestmentLinkStatus.Headline(ui.Live) == "Game stopped answering", "A game that stopped was described as never answering.");
        ui.Capture("live-stopped.png");
        account.Game.State = LiveInvestmentLinkState.Connected;
        ui.Tab.Show("world", "", null);
        // Attached with the investment link answering, World shows its own failure and Retry.
        yield return new("World with the game answering", () => ui.Live.Connected && ui.Shown == "live:world");
        ui.Capture("live-page.png");
        exited = true;
        // The game's exit detaches every section once and lists the games again, once.
        yield return new("the game's exit", () => !ui.Tab.Attached);
        LiveUi.Check(ui.Live.State == LiveInvestmentState.Lost && ui.Live.StateText == LiveInvestmentSession.GameClosed && finds == 1 && !world.IsAttached,
            $"The game's exit did not end in Lost with one list refresh ({finds}).");
        ui.Capture("live-closed.png");
        Console.WriteLine($"investment_live_status_ui world_asks={worldAsks} pending=unopened finds={finds}");
    }
}
