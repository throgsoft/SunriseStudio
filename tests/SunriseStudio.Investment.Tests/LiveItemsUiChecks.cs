using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using SunriseStudio.Packages;

/**
 * Real frames of Live's Items against a scripted game: the stack chosen detached, then connected with its held row, Grant,
 * the Set quantity and Remove popups and a grant the game stopped part way, each sent through the actions' own requests;
 * the list as a grid; the item page's Open in Live, which chooses the item whatever Items showed; and Items' Open, which
 * shows the item in Assets while Items keeps its choice.
 */
internal static class LiveItemsUiChecks
{
    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("live-items", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var stack = Picks.Stack(catalog);
        var weapons = Picks.Weapons(ui.Work, catalog);
        var account = new ScriptedAccount(catalog, tables) { Stack = stack, Weapons = weapons };
        var section = ui.Section;
        var actions = section.Actions;
        var list = section.ItemList;
        // Items on this item: history records it, the list marks it and the detail draws its actions.
        bool ItemsOn(InvestmentItem item) => ui.LiveShown("items") && section.Row == $"item:{item.Hash:X8}" && list.Chosen == item.Index;
        var sent = account.Game.Sent;

        // As Open in Live on the stack's item page will: Items with the stack chosen, detached, every action disabled with the reason.
        ui.ShowLive("items", $"item:{stack.Hash:X8}");
        yield return LiveUi.Icons("Items with no game", () => ItemsOn(stack), section.Icons, 3);
        LiveUi.Check(LiveGate.Missing(ui.Live, ui.Live.Inventory, LiveNeed.Inventory)?.Text == LiveGate.AttachSentence,
            "Detached Items did not say to attach the game.");
        ui.Capture("live-detached-investment-items.png");
        ui.Narrowest(true);
        yield return new("Items narrow with no game", () => ItemsOn(stack));
        ui.Capture("live-detached-items-narrow.png");
        ui.Narrowest(false);

        // Connected, Items shows the stack's one held row and marks it in the list.
        ui.Attach(account.Game);
        yield return new("Items connected", () => ui.Live.Connected && ItemsOn(stack) && ui.Live.Held is not null);
        var row = ui.Live.Held!.Of(stack.Hash).Single();
        LiveUi.Check(LiveGate.Held(ui.Live)?.Badge(stack.Hash) == $"x{row.Quantity:N0}", "Items did not mark the stack the game holds.");
        // Choose the row, as a click on its line does, then Grant two of the stack, as Quantity and Grant do.
        ui.Live.Selected = row;
        actions.GrantQuantity = 2;
        var grant = actions.Grant(ui.Live, stack);
        yield return new("the grant", () => grant.Phase == LiveOperationPhase.Done);
        LiveUi.Check(sent[^1].Is("grant", stack.Hash, 2u), $"Grant sent {sent[^1]} for the stack Items shows.");
        // The row's Set quantity, to five more, and its Remove, each opened through the gating its button draws from: each popup
        // over Items, then its button.
        LiveUi.Check(LiveItemActions.Controls(ui.Work, stack, row, null) is { Quantity: true, Remove: true, Blocked: "" }
            && actions.OpenHeld(ui.Work, row, stack, remove: false), "Items did not offer Set quantity on the stack's plain account row.");
        actions.SetHeldQuantity(row.Quantity + 5);
        yield return new("the Set quantity popup", () => ItemsOn(stack), LiveUi.PopupSettle);
        ui.Capture("live-items.png");
        var set = actions.ApplyHeld(ui.Live, remove: false)!;
        yield return new("the quantity change", () => set.Phase == LiveOperationPhase.Done);
        LiveUi.Check(sent[^1].Is("quantity", row.Scope, row.Instance, row.DefinitionHash, row.MutationSerial, row.Quantity, row.Quantity + 5),
            $"Set quantity sent {sent[^1]} for the chosen row.");
        LiveUi.Check(actions.OpenHeld(ui.Work, row, stack, remove: true), "Items did not offer Remove on the stack's plain account row.");
        yield return new("the Remove popup", () => ItemsOn(stack), LiveUi.PopupSettle);
        ui.Capture("live-items-remove.png");
        var removed = actions.ApplyHeld(ui.Live, remove: true)!;
        yield return new("the removal", () => removed.Phase == LiveOperationPhase.Done);
        LiveUi.Check(sent[^1].Is("quantity", row.Scope, row.Instance, row.DefinitionHash, row.MutationSerial, row.Quantity, 0),
            $"Remove sent {sent[^1]} for the chosen row.");
        // A grant the game stopped part way: its line warns instead of showing done.
        account.NextGrantReason = "editor_disconnected";
        var partial = actions.Grant(ui.Live, stack);
        yield return new("the partial grant", () => partial.Phase == LiveOperationPhase.Done);
        LiveUi.Check(partial.StoppedPartWay, "A grant the game stopped part way read as done.");
        ui.Capture("live-items-partial-grant.png");
        ui.Narrowest(true);
        yield return new("Items narrow", () => ItemsOn(stack));
        ui.Capture("live-items-narrow.png");
        ui.Narrowest(false);
        ui.Form.SetBrowserGrid(true);
        yield return LiveUi.Icons("Items as a grid", () => ItemsOn(stack), section.Icons, 3);
        ui.Capture("live-items-grid.png");
        ui.Form.SetBrowserGrid(false);

        // Attached, the item page keeps its offline layout, with Open in Live enabled. Open in Live chooses the item in Items,
        // whatever Items showed: another item, searched for, on a kind that does not list the stack.
        ui.ShowLive("items", $"item:{weapons[0].Hash:X8}");
        yield return new("Items on a weapon", () => ItemsOn(weapons[0]));
        list.Search = weapons[0].Name;
        list.Kind = InvestmentItemKinds.Weapons;
        ui.Open(stack);
        yield return new("the item page connected", () => ui.ItemShown(stack));
        LiveUi.Check(ui.Link.Attached, "Open in Live stayed disabled on the item page with a game attached.");
        ui.Capture("item-connected.png");
        var target = StudioLiveLink.ItemTarget(stack);
        ui.Link.Open(target.View, target.Row);
        yield return new("Open in Live on the stack", () => ItemsOn(stack));
        LiveUi.Check(!ui.Tab.Requested && list.Search.Length == 0 && list.Kind == ItemCatalogView.AllItems,
            "Open in Live kept asking for its section, or kept Items' search or kind, which hid the linked item.");
        ui.Capture("live-open-item.png");

        // Items' Open shows the chosen item's page in Assets, model deferred, and Items keeps its choice.
        ui.ShowLive("items", $"item:{weapons[1].Hash:X8}");
        yield return new("Items on the other weapon", () => ItemsOn(weapons[1]));
        ui.Form.LiveContext().OpenAsset(weapons[1].Tag, InvestmentPage.PageId, null);
        yield return new("the weapon's item page", () => ui.ItemShown(weapons[1]));
        LiveUi.Check(section.Row == $"item:{weapons[1].Hash:X8}" && ui.Work.Document!.ModelDeferred,
            $"Items' Open left Items on {section.Row} or read the weapon's model.");
        Console.WriteLine($"investment_live_items_ui item=0x{stack.Hash:X8} weapons=0x{weapons[0].Hash:X8},0x{weapons[1].Hash:X8} sent={sent.Count}");
    }
}
