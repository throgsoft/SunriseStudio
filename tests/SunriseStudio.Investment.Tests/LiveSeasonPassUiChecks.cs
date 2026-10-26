using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;

/**
 * Real frames of the season pass page and Live's Season pass against a scripted game: the largest ladder detached with the
 * claim row chosen; the page offline with row chips, the item picker searched to a replacement, and a socketed row refused
 * the item; then connected the game's ladder with a claim, the page attached, its Open in Live, a saved item change that
 * leaves the game's pass matched, and a pass no installed progression matches.
 */
internal static class LiveSeasonPassUiChecks
{
    // Height added at UI scale 1 for the season pass page captures, so the chosen row's fields show without scrolling.
    private const int PassRoom = 160;

    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("live-season-pass", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var page = ui.Form.Pages.Pages.OfType<SeasonPassPage>().Single();
        var section = ui.Section;
        var ladder = SeasonPassPage.Largest(tables);
        int claimRow = ladder.Rewards.First(row => row.Rank is >= ScriptedAccount.ClaimedBelow and <= ScriptedAccount.Rank && row.Error.Length == 0
            && row.ItemIndex < catalog.Items.Count && row.ClaimSlot != 0).Index;
        // A single claimed reward with conditions and no sockets, which may take a perk, so every replacement line draws.
        int itemRow = ladder.Rewards.Where(row => row is { Quantity: 1, Sockets.Count: 0, Error: "" } && row.ClaimSlot != 0
                && !SeasonPassPage.Automatic(catalog, tables, (int)row.ItemIndex))
            .OrderByDescending(row => row.Conditions.Members.Count != 0).First().Index;
        var account = new ScriptedAccount(catalog, tables) { Ladder = ladder };
        void ShowPass(int row)
        {
            ui.Work.Open(tables.TableTag(InvestmentRootSlots.Progressions));
            ui.Form.ShowAssetPage(SeasonPassPage.PageId, SeasonPassPage.RowOf(ladder.Index, row));
        }
        bool PassShown() => ui.Shown == "asset:" + SeasonPassPage.PageId;

        // Detached, the largest ladder shows with the claim row chosen; XP, Set XP, Claim and Reset claim give the reason.
        ui.ShowLive("season-pass", $"pass:{ladder.Index}:{claimRow}");
        yield return LiveUi.Icons("the pass with no game", () => ui.LiveShown("season-pass"), section.Icons, 3);
        LiveUi.Check(section.Row == $"pass:{ladder.Index}:{claimRow}", $"Season pass recorded {section.Row} for the claim row.");
        ui.Capture("live-detached-season-pass.png");
        ui.Narrowest(true);
        yield return new("the pass narrow with no game", () => ui.LiveShown("season-pass"));
        ui.Capture("live-detached-season-pass-narrow.png");
        ui.Narrowest(false);

        ui.Taller(PassRoom);
        ShowPass(claimRow);
        yield return LiveUi.Icons("the season pass page", PassShown, page.Icons, 3);
        LiveUi.Check(!ui.Link.Attached, "Open in Live was enabled on the season pass page with no game attached.");
        ui.Capture("pass-absent.png");
        // A rank and lane several rows share, so the row chips show each row's icon.
        var shared = ladder.Rewards.Where(row => row.Rank >= 1 && row.Error.Length == 0)
            .GroupBy(row => (row.Rank, InvestmentSeasonPass.LooksLikeOwnership(row.Conditions, tables.Unlocks)))
            .First(group => group.Count() > 1).First();
        ui.Form.ShowAssetPage(SeasonPassPage.PageId, SeasonPassPage.RowOf(ladder.Index, shared.Index));
        yield return new("a row that shares its rank", () => PassShown() && page.Grid.SelectedRow == shared.Index);
        ui.Capture("pass-row-chips.png");
        // The item picker, searched for a replacement's name, lists it among fewer than every item and marks it chosen.
        var replacement = Replacement(catalog, tables, ladder.Rewards[itemRow]);
        ui.Form.ShowAssetPage(SeasonPassPage.PageId, SeasonPassPage.RowOf(ladder.Index, itemRow));
        EditItem(ui, page, ladder.Rewards[itemRow], replacement);
        yield return new("the item picker", () => PassShown() && page.ItemPicker.Chosen == replacement.Index);
        var listed = page.ItemPicker.Rows(ui.Work, catalog, default);
        LiveUi.Check(listed.Contains(replacement) && listed.Length < catalog.Items.Count,
            $"The item picker listed {listed.Length} items for {replacement.Name}.");
        ui.Capture("pass-item-edit.png");
        // A socketed row given an item that opens when acquired: Save is refused with Sunrise's reason.
        var socketed = ladder.Rewards.First(row => row.Sockets.Count != 0 && row.Error.Length == 0);
        var opening = catalog.Items.First(item => item.Named && tables.OpensOnAcquisition(item.Index));
        ui.Form.ShowAssetPage(SeasonPassPage.PageId, SeasonPassPage.RowOf(ladder.Index, socketed.Index));
        EditItem(ui, page, socketed, opening);
        yield return new("the refused item", () => PassShown() && page.ItemPicker.Chosen == opening.Index);
        ui.Capture("pass-item-refused.png");
        page.Popup.Clear();
        ui.Taller(0);

        // Connected: the game's ladder with its claim marks, the claim row's state and its last action.
        ui.Attach(account.Game);
        ui.ShowLive("season-pass", $"pass:{ladder.Index}:{claimRow}");
        yield return new("the game's pass", () => ui.Live.Connected && ui.LiveShown("season-pass") && section.PassMatched);
        var claim = ui.Live.Submit(new LiveClaimPassRow((ushort)claimRow, ui.Live.SeasonPass.Value!.Rows[claimRow].ItemHash, "claim"));
        yield return LiveUi.Icons("the claimed row", () => claim.Phase == LiveOperationPhase.Done && ui.LiveShown("season-pass"), section.Icons, 3);
        ui.Capture("live-season-pass.png");
        ui.Narrowest(true);
        yield return new("the game's pass narrow", () => ui.LiveShown("season-pass"));
        ui.Capture("live-season-pass-narrow.png");
        ui.Narrowest(false);

        // Attached, the page shows the ladder it showed offline; its Open in Live sends the row it shows to Live.
        ui.Taller(PassRoom);
        ShowPass(itemRow);
        yield return LiveUi.Icons("the pass page attached", () => PassShown() && page.Grid.SelectedRow == itemRow, page.Icons, 3);
        ui.Capture("pass-connected.png");
        var target = StudioLiveLink.PassTarget(page.Grid.Progression, page.Grid.SelectedRow);
        ui.Link.Open(target.View, target.Row);
        yield return LiveUi.Icons("Open in Live on the row", () => ui.LiveShown("season-pass") && section.Row == $"pass:{ladder.Index}:{itemRow}",
            section.Icons, 3);
        ui.Capture("live-open-pass.png");

        // A saved item change the game has not installed leaves the row out of matching, so the pass stays the game's.
        ui.Form.ShowAssetPage(SeasonPassPage.PageId);
        yield return new("the pass page again", PassShown);
        EditItem(ui, page, ladder.Rewards[itemRow], replacement);
        yield return new("the item draft", () => PassShown() && page.ItemPicker.Chosen == replacement.Index);
        LiveUi.Check(page.Popup.Save(ui.Work), "The connected item draft did not save.");
        yield return new("the saved change", () => ui.Work.Investment!.Tables().Progressions[ladder.Index].Rewards[itemRow].ItemIndex == replacement.Index);
        ui.Taller(0);
        ui.ShowLive("season-pass", null);
        yield return new("the game's pass with the change", () => ui.LiveShown("season-pass"));
        LiveUi.Check(section.PassMatched, "An uninstalled item change broke the match with the game's season pass.");
        ui.Work.Undo();
        yield return new("the undone change", () => ui.Work.Investment!.Tables().Progressions[ladder.Index].Rewards[itemRow].ItemIndex != replacement.Index);
        // Then the game reports a pass no installed progression matches, so no claim state is drawn.
        account.OtherPass = true;
        ui.ShowLive("season-pass", null);
        yield return new("a pass no progression matches", () => ui.Live.SeasonPass.Value?.Rows.Count == ladder.Rewards.Count - 1 && ui.LiveShown("season-pass"));
        LiveUi.Check(!section.PassMatched, "A season pass no progression matches drew the game's claim state.");
        ui.Capture("live-season-pass-other.png");
        Console.WriteLine($"investment_live_pass_ui progression={ladder.Index} claim_row={claimRow} item_row={itemRow}>{replacement.Index}");
    }

    /** A perk the row may take, else any other named item it may take, so the flip line and the claim lines all draw. */
    private static InvestmentItem Replacement(InvestmentCatalog catalog, InvestmentTables tables, InvestmentSeasonPassRow row)
    {
        var choices = catalog.Items.Where(item => item.Named && item.Index != row.ItemIndex
            && SeasonPassPage.Replacement(catalog, tables, row, item.Index) is { Refusal: "" }).ToArray();
        return choices.FirstOrDefault(item => SeasonPassPage.Automatic(catalog, tables, item.Index)) ?? choices[0];
    }

    /** The row's item draft as its Item button opens it, with the item's name typed into the picker's search and the item chosen. */
    private static void EditItem(LiveUi ui, SeasonPassPage page, InvestmentSeasonPassRow row, InvestmentItem item)
    {
        page.Popup.Begin(ui.Work, row.ItemField, $"Row {row.Index}");
        page.BeginItemEdit();
        page.ItemPicker.Search = item.Name;
        page.Popup.Value = item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
