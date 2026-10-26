using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Scene;

/**
 * Back and Forward return to the previous document after an open that changes the page tabs, and to Live opened before any
 * document.
 */
internal static class NavigationChecks
{
    private const uint Helmet = 0x8131934E, Weapon = 0x8132017D;
    private const string ComponentsPage = "sunrise.components";

    public static void Run(string install, string output)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Frames(install, output); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    private static void Frames(string install, string output)
    {
        Directory.CreateDirectory(output);
        string settings = Path.Combine(output, "settings.json");
        new StudioSettings().Save(settings);
        using var form = new StudioForm(new(Game: install, Warp: true, Smoke: true, Frames: 40000, SettingsPath: settings));
        var shownPage = typeof(StudioForm).GetField("shownPage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var script = Script(form, () => (string?)shownPage.GetValue(form));
        int wait = 5, settled = 0;
        bool done = false;
        form.FrameRendered += () =>
        {
            var workspace = form.Workspace;
            if (!form.EditorReady || workspace.IsBusy || workspace.Catalog is null
                || form.AssetPreviewViewport?.Animation?.IsLoading == true) { settled = 0; return; }
            Check(workspace.Error.Length == 0, workspace.Error);
            // A new document's tab bar draws nothing on its first frame, so each step waits a few frames.
            if (++settled < wait) return;
            settled = 0;
            if (script.MoveNext()) { wait = script.Current; return; }
            done = true; form.Close();
        };
        Application.Run(form);
        if (form.Failure is { } error) throw new InvalidDataException(error);
        Check(done, "The navigation check stopped early.");
        Console.WriteLine("editor_navigation live_first=pass guardian_item=pass guardian_weapon=pass enemy_component=pass open_as_asset=pass deferred_open_as_asset=pass deferred_pages=kept_model");
    }

    /** Each step acts, then yields the settled frames to wait before the next step checks it. */
    private static IEnumerator<int> Script(StudioForm form, Func<string?> shownPage)
    {
        var workspace = form.Workspace;
        for (int tries = 0; tries < 200 && workspace.CategoryRowsOf(StudioCategories.EnemiesCategory).Count == 0; tries++)
            yield return 5;
        Check(workspace.CategoryRowsOf(StudioCategories.EnemiesCategory).Count != 0, "The Enemies category is empty.");

        // Live needs no document, so it is a place of its own before the first asset opens.
        Check(workspace.Document is null, "A document opened before the check.");
        form.ShowAssetPage("Live"); yield return 5;
        Check(shownPage() == "live:world" && !workspace.CanNavigateBack, $"Live showed {shownPage()} or left a place to go back to.");
        workspace.Open(GuardianClasses.Titan); yield return 5;
        form.ShowAssetPage(StudioCategories.GuardianPageId); yield return 5;
        Navigate(back: true); yield return 5;
        Check(shownPage() == "live:world" && workspace.Document?.Entry.TagHash == GuardianClasses.Titan,
            $"Back showed {shownPage()} instead of Live, or closed the document.");
        Navigate(back: false); yield return 5;
        Expect(GuardianClasses.Titan, "asset:" + StudioCategories.GuardianPageId);

        // The Guardian page opens an item with Select, as its Open item button does.
        workspace.Open(GuardianClasses.Titan); yield return 5;
        form.ShowAssetPage(StudioCategories.GuardianPageId); yield return 5;
        Expect(GuardianClasses.Titan, "asset:" + StudioCategories.GuardianPageId);
        workspace.Select(Helmet); yield return 5;
        Expect(Helmet);
        Navigate(back: true); yield return 5;
        Expect(GuardianClasses.Titan, "asset:" + StudioCategories.GuardianPageId);
        Check(workspace.Document!.OpenContext?.Guardian == GuardianLoadout.Default, "Back lost the guardian loadout.");
        Navigate(back: false); yield return 5;
        Expect(Helmet);

        Navigate(back: true); yield return 5;
        workspace.Select(Weapon); yield return 5;
        Expect(Weapon);
        // A plain open reads the item alone in the weapon view; Load model reads the model and its animation owner.
        var weaponView = workspace.Document!.OpenContext;
        Check(workspace.Document is { ModelDeferred: true, Scene: null } && weaponView is { WeaponItemTag: Weapon, EntityTag: 0 },
            "The weapon read its model on a plain open.");
        Navigate(back: true); yield return 5;
        Expect(GuardianClasses.Titan, "asset:" + StudioCategories.GuardianPageId);
        Navigate(back: false); yield return 5;
        Expect(Weapon);
        Check(workspace.Document!.OpenContext is { } forward && forward with { EntityTag = 0 } == weaponView, "Forward lost the weapon view.");
        // Open as asset is another view, so it reads the deferred weapon plainly with its preview; Back returns to the deferred
        // weapon view, which history kept.
        workspace.Open(Weapon, StudioWorkspace.PlainOpen); yield return 5;
        Expect(Weapon);
        Check(workspace.Document is { Kind: "", ModelDeferred: false, OpenContext: var plainWeapon } && plainWeapon == StudioWorkspace.PlainOpen,
            "Open as asset kept the deferred weapon deferred or in the weapon view.");
        Navigate(back: true); yield return 5;
        Expect(Weapon);
        Check(workspace.Document is { ModelDeferred: true, OpenContext: { } returned } && returned with { EntityTag = 0 } == weaponView,
            "Back from Open as asset did not return to the deferred weapon view.");
        workspace.LoadModel(); yield return 5;
        Expect(Weapon);
        Check(workspace.Document is { ModelDeferred: false, Scene: not null, OpenContext: { WeaponItemTag: Weapon, EntityTag: not 0 } },
            "Load model did not read the weapon with its animation owner.");

        // Two pages of the deferred weapon are two places. Load model on the second keeps the model for Back and Forward between
        // them, so neither rereads the weapon on its data alone.
        workspace.Select(Helmet); yield return 5;
        workspace.Select(Weapon); yield return 5;
        form.ShowAssetPage(InvestmentPage.PageId); yield return 5;
        form.ShowAssetPage(ItemRewardsPage.PageId); yield return 5;
        Expect(Weapon, "asset:" + ItemRewardsPage.PageId);
        Check(workspace.Document is { ModelDeferred: true }, "The weapon read its model on a plain open.");
        workspace.LoadModel(); yield return 5;
        var loaded = workspace.Document;
        Check(loaded is { ModelDeferred: false, Scene: not null }, "Load model on the second page did not read the model.");
        Navigate(back: true); yield return 5;
        Expect(Weapon, "asset:" + InvestmentPage.PageId);
        Check(ReferenceEquals(workspace.Document, loaded), "Back to the weapon's first page reread it and dropped its model.");
        Navigate(back: false); yield return 5;
        Expect(Weapon, "asset:" + ItemRewardsPage.PageId);
        Check(ReferenceEquals(workspace.Document, loaded), "Forward to the weapon's second page reread it.");

        // The Components page opens a config with Select.
        uint enemy = workspace.CategoryRowsOf(StudioCategories.EnemiesCategory)[0].Entry.TagHash;
        workspace.Open(enemy); yield return 5;
        form.ShowAssetPage(ComponentsPage);
        for (int tries = 0; tries < 60 && workspace.LoadedComponents is not { Sections.Count: > 0 }; tries++) yield return 5;
        Check(workspace.LoadedComponents is { Sections.Count: > 0 }, $"Enemy {enemy:X8} lists no configs.");
        uint component = workspace.LoadedComponents!.Sections[0].Config;
        workspace.Select(component); yield return 5;
        Expect(component);
        Navigate(back: true); yield return 5;
        Expect(enemy, "asset:" + ComponentsPage);
        Navigate(back: false); yield return 5;
        Expect(component);

        // Open as asset swaps the category tabs for the flat ones.
        workspace.Open(Helmet); yield return 5;
        var item = workspace.Document!;
        workspace.Open(Helmet, StudioWorkspace.PlainOpen); yield return 5;
        Check(workspace.Document is { Kind: "", OpenContext: var plain } && plain == StudioWorkspace.PlainOpen,
            "Open as asset did not open the plain view.");
        Navigate(back: true); yield return 5;
        Expect(Helmet);
        Check(workspace.Document!.Kind == item.Kind && workspace.Document.OpenContext == item.OpenContext,
            "Back did not return to the category view of the item.");

        void Navigate(bool back)
        {
            Check(back ? workspace.CanNavigateBack : workspace.CanNavigateForward, $"{(back ? "Back" : "Forward")} is disabled.");
            if (back) workspace.NavigateBack();
            else workspace.NavigateForward();
        }

        void Expect(uint tag, string? page = null)
        {
            var document = workspace.Document;
            Check(document?.Entry.TagHash == tag, $"Expected {tag:X8} open, found {document?.Entry.TagHash:X8}.");
            Check(page is null || shownPage() == page, $"Expected page {page} on {tag:X8}, found {shownPage()}.");
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
}
