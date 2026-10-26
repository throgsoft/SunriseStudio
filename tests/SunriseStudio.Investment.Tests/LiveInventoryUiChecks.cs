using SunriseStudio.Editor;
using SunriseStudio.Formats;
using SunriseStudio.Live;

/**
 * Real frames of Live's Inventory and the Buckets page against a scripted game: one bucket detached, with every installed
 * bucket listed by id; the Buckets page offline and attached, which shows no held cells; then connected every held item, the
 * equipped weapon chosen in its bucket as history restores it, two stacks of one item in two buckets each chosen and edited
 * on its own, the Pursuits bucket, and the grid.
 */
internal static class LiveInventoryUiChecks
{
    // Each of the two stacks holds one, so Set quantity can raise it to two within any stack limit.
    private const int StackQuantity = 1;

    public static void Run(string install, string temporary, string? captures, string? keys) =>
        LiveUi.Run("live-inventory", install, temporary, captures, keys, Script);

    private static IEnumerable<UiWait> Script(LiveUi ui, InvestmentCatalog catalog, InvestmentTables tables)
    {
        var weapons = Picks.Weapons(ui.Work, catalog);
        var stack = Picks.Stack(catalog);
        // A second character bucket holds another stack of the same item, as a recovery bucket does for an item its own bucket had no room for.
        byte other = tables.Buckets.First(row => row.Id != stack.Bucket && row.ArraySelector == 0).Id;
        var account = new ScriptedAccount(catalog, tables)
        {
            Stack = stack, Weapons = weapons, Stacks = [(stack, stack.Bucket, StackQuantity), (stack, other, StackQuantity)]
        };
        var section = ui.Section;
        var actions = section.Actions;
        byte bucket = weapons[0].Bucket;
        var bucketsPage = ui.Form.Pages.Pages.OfType<BucketsPage>().Single();
        void ShowBuckets()
        {
            ui.Work.Open(tables.Root!.Tag);
            ui.Form.ShowAssetPage(BucketsPage.PageId, BucketsPage.RowOf(bucket));
        }
        string HeldKey(LiveInventoryScope scope, ulong instance, InvestmentItem item, byte held) =>
            $"held:{(int)scope}:{instance:X16}:{item.Hash:X8}:{held}";
        string WeaponKey() => HeldKey(LiveInventoryScope.CharacterItem, ScriptedAccount.WeaponInstance, weapons[0], weapons[0].Bucket);

        // Detached, the bucket keeps its cells up to capacity, the Held column reads "-", and the list names every installed bucket by id.
        ui.ShowLive("inventory", $"bucket:{bucket}");
        yield return new("one bucket with no game", () => ui.LiveShown("inventory"));
        LiveUi.Check(section.Buckets.Shown.Select(row => (int)row.Id).SequenceEqual(tables.Buckets.Select(row => (int)row.Id).Order())
            && section.Row == $"bucket:{bucket}" && section.Buckets.Selected == bucket,
            $"Inventory listed other buckets or recorded {section.Row} for bucket {bucket}.");
        ui.Capture("live-detached-inventory.png");
        ui.Narrowest(true);
        yield return new("one bucket narrow", () => ui.LiveShown("inventory"));
        ui.Capture("live-detached-inventory-narrow.png");
        ui.Narrowest(false);
        ShowBuckets();
        yield return LiveUi.Icons("the Buckets page", () => ui.Shown == "asset:" + BucketsPage.PageId, bucketsPage.Icons, 3);
        ui.Capture("buckets-offline.png");

        ui.Attach(account.Game);
        ui.ShowLive("inventory", "bucket:all");
        yield return LiveUi.Icons("every held item", () => ui.Live.Connected && ui.LiveShown("inventory"), section.Icons, 3);
        ui.Capture("live-inventory-all.png");
        // As history restores a chosen held copy: the equipped weapon in its bucket.
        ui.ShowLive("inventory", $"bucket:{bucket}/{WeaponKey()}");
        yield return LiveUi.Icons("the equipped weapon", () => ui.LiveShown("inventory") && ui.Live.Selected?.Instance == ScriptedAccount.WeaponInstance,
            section.Icons);
        LiveUi.Check(section.Row == $"bucket:{bucket}/{WeaponKey()}",
            $"Inventory recorded {section.Row} for the equipped weapon.");
        ui.Capture("live-inventory-bucket.png");
        ui.Narrowest(true);
        yield return new("the equipped weapon narrow", () => ui.LiveShown("inventory"));
        ui.Capture("live-inventory-narrow.png");
        ui.Narrowest(false);
        // Two stacks of one item, both without an instance, in two buckets: history chooses each one, and the Set quantity its
        // gating opens edits that stack.
        for (int index = 0; index < account.Stacks.Length; index++)
        {
            byte held = account.Stacks[index].Bucket;
            string key = $"bucket:all/{HeldKey(LiveInventoryScope.CharacterStack, 0, stack, held)}";
            ui.ShowLive("inventory", key);
            yield return new($"stack {index + 1} of the item", () => ui.LiveShown("inventory")
                && ui.Live.Selected is { Scope: LiveInventoryScope.CharacterStack } chosen && chosen.BucketId == held);
            var row = ui.Live.Selected!;
            LiveUi.Check(section.Row == key && row.MutationSerial == ScriptedAccount.StackSerial + index
                && actions.OpenHeld(ui.Work, row, stack, remove: false), $"Inventory chose {section.Row} for the stack in bucket {held}.");
            actions.SetHeldQuantity(StackQuantity + 1);
            var set = actions.ApplyHeld(ui.Live, remove: false)!;
            yield return new($"the change to stack {index + 1}", () => set.Phase == LiveOperationPhase.Done);
            LiveUi.Check(account.Game.Sent[^1].Is("quantity", LiveInventoryScope.CharacterStack, 0UL, stack.Hash, row.MutationSerial,
                StackQuantity, StackQuantity + 1), $"Set quantity on the stack in bucket {held} sent {account.Game.Sent[^1]}.");
        }
        // The Pursuits bucket shows its held cells like any bucket, with a link to the board.
        section.Buckets.Select(InvestmentPursuits.PursuitBucket);
        yield return new("the Pursuits bucket", () => section.Row == $"bucket:{InvestmentPursuits.PursuitBucket}");
        ui.Capture("live-inventory-pursuits.png");
        ui.ShowLive("inventory", "bucket:all");
        ui.Form.SetBrowserGrid(true);
        yield return LiveUi.Icons("every held item as a grid", () => ui.LiveShown("inventory") && section.Buckets.Selected == BucketList.AllBuckets,
            section.Icons, 3);
        ui.Capture("live-inventory-grid.png");
        ui.Form.SetBrowserGrid(false);
        // Attached, the Buckets page keeps its offline layout: no held cells or counts.
        ShowBuckets();
        yield return LiveUi.Icons("the Buckets page attached", () => ui.Shown == "asset:" + BucketsPage.PageId, bucketsPage.Icons, 3);
        ui.Capture("buckets-connected.png");
        Console.WriteLine($"investment_live_inventory_ui bucket={bucket} buckets={section.Buckets.Shown.Count} weapon=0x{weapons[0].Hash:X8}");
    }
}
