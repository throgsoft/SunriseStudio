using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Editor.Creation;
using static SunriseStudio.Creation.Tests.Fixture;

namespace SunriseStudio.Creation.Tests;

/** New and Clone tooltips name the missing project before a type's own blocker. */
internal static class CreationRefusalChecks
{
    public static void Run(string root)
    {
        using var form = new StudioForm(new(Smoke: true, SettingsPath: Path.Combine(root, "creation-refusal-settings.json")));
        var refusal = typeof(StudioForm).GetMethod("CreationRefusal", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string? Refusal(CreationType type) => (string?)refusal.Invoke(form, [type]);
        var available = CreationTypes.For(CreationTypes.TextureKey, CreationMode.New);
        var blocked = CreationTypes.For("Unregistered creation key", CreationMode.New);
        Check(form.Workspace.Project is null && available.Available && !blocked.Available, "The refusal fixture changed.");
        Check(Refusal(available) == "Create or open a project first.", "An available type without a project was not refused for it.");
        Check(Refusal(blocked) == "Create or open a project first.\n" + blocked.Blocker,
            "A blocked type without a project did not name the project first.");
        Console.WriteLine("creation_refusal no_project=first");
    }
}
