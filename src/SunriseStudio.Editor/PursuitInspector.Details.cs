using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;
using Sites = SunriseStudio.Editor.RewardSitesPage.RewardIndexTable;

namespace SunriseStudio.Editor;

internal sealed partial class PursuitInspector
{
    private const ushort Absent = ushort.MaxValue;
    private const string CompletionTip = InvestmentTableViews.CompletionTip;

    /** The completion controls. Live adds the game's saved completion check and chain value. */
    private static void DrawCompletion(Step step)
    {
        var pursuit = step.Pursuit!;
        string[] controls = [pursuit.RequireAll ? "Requires all objectives" : "Requires one objective",
            .. new[] { (pursuit.RequireNonOptional, "Skips optional objectives"), (pursuit.Automatic, "Completes automatically"),
                (pursuit.Suppress, "Completion suppressed") }.Where(flag => flag.Item1).Select(flag => flag.Item2)];
        string line = string.Join(Dot, controls), shown = StudioUi.Fit(line, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(shown);
        StudioUi.Tip(shown == line ? "" : line);
        DrawControls(step, pursuit);
        if (!step.Live) return;
        Block("Saved completion check", "What the game reports for the shown held copy.");
        DrawSavedCheck(step);
        Block("Actual chain value", "The chain value the game holds for the selected character.");
        if (pursuit.Chain is not { Steps.Count: > 0 } chain) ImGui.TextDisabled("Not in a chain");
        else DrawChainValue(step, chain);
    }

    /** The chain value the game holds, and which step's installed value it matches. */
    private static void DrawChainValue(Step step, InvestmentQuestSet chain)
    {
        if (chain.ValueSlot == Absent) { Muted("None", "This quest names no chain value."); return; }
        if (LiveUnlockSources.Of(step.Tables.Unlocks, new(InvestmentUnlocks.ValueOpcode, chain.ValueSlot, -1)) is not { } source)
        { Muted(NotReadable, NotReadableTip); return; }
        if (Current(step.Session, source, out var missing) is not { } value) { LiveGate.Line(missing!.Value); return; }
        if (!value.Known) { Muted(Unknown, UnreadTip); return; }
        var matches = chain.Steps.Select((row, index) => (row, index)).Where(match => match.row.Value == value.Value).ToArray();
        string match = matches.Any(row => row.row.ItemIndex == step.Item.Index) ? "Matches this step"
            : matches.Length != 0 ? $"Matches step {(matches[0].index + 1).ToString(CultureInfo.InvariantCulture)}" : "Matches no step";
        StudioUi.Facts("pursuit-chain", [("Saved", value.Value.ToString(CultureInfo.InvariantCulture) + Dot + match,
            "Each step's value identifies it; Studio matches the saved value against them.\n" + StudioUi.MayBeInaccurate)]);
    }

    /** A sub-block title inside a tab or section, with its explanation on hover. */
    private static void Block(string title, string tip)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(title);
        StudioUi.Tip(tip);
    }

    /**
     * The completion values and operations as name and value rows. An operation that names a reward site is a button that
     * opens the site on the Reward sites page.
     */
    private static void DrawControls(Step step, InvestmentPursuit pursuit)
    {
        string Site(InvestmentReference reference) => reference.Index == Absent ? "None" : $"Site {reference.Index}";
        (string Name, string Value, string Tip, InvestmentReference? Site)[] rows =
        [
            ("On completion", pursuit.CompletionValue.Label, "Unless suppressed, absence removes the step; a present value goes up by one.\n"
                + StudioUi.MayBeInaccurate, null),
            ("Context value", pursuit.ContextValue.Label, "", null),
            ("Primary operation", Site(pursuit.Primary), $"{pursuit.Primary.Label}\n{CompletionTip}", pursuit.Primary),
            ("Secondary operation", Site(pursuit.Secondary), $"{pursuit.Secondary.Label}\n{CompletionTip}", pursuit.Secondary)
        ];
        bool linked = step.Tables.Root is not null;
        float nameWidth = rows.Max(row => ImGui.CalcTextSize(row.Name).X);
        if (!ImGui.BeginTable("pursuit-completion", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthFixed, nameWidth);
        ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);
        foreach (var (name, value, tip, site) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(name);
            ImGui.TableSetColumnIndex(1);
            if (linked && site is { } reference && reference.Index != Absent)
            {
                ImGui.PushID(name);
                if (ImGui.SmallButton(value))
                {
                    step.Workspace.Open(step.Tables.Root!.Tag);
                    step.Context.ShowPage?.Invoke(RewardSitesPage.PageId, RewardSitesPage.RowOf(Sites.Sites, reference.Index));
                }
                StudioUi.Tip("Open in Reward sites\n" + tip);
                ImGui.PopID();
                continue;
            }
            ImGui.AlignTextToFramePadding();
            string shown = StudioUi.Fit(value, ImGui.GetContentRegionAvail().X);
            ImGui.TextUnformatted(shown);
            StudioUi.Tip(tip.Length != 0 ? tip : shown != value ? value : "");
        }
        ImGui.EndTable();
    }

    /** The game's completion check of the shown copy: what it knows, why it would or would not complete, and each objective. */
    private static void DrawSavedCheck(Step step)
    {
        var live = step.Session;
        if (LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) is { } missing) { LiveGate.Line(missing); return; }
        if (step.Held is not { } held) { ImGui.TextDisabled("This step is not held."); return; }
        string Known(LivePursuitFlags flag) => held.Flags.HasFlag(flag) ? "known" : "not known";
        string conditions = Known(LivePursuitFlags.ConditionsKnown)
            + (held.Flags.HasFlag(LivePursuitFlags.ConditionsKnown) ? Dot + (held.Flags.HasFlag(LivePursuitFlags.Eligible) ? "met" : "not met") : "");
        var rows = new List<(string, string, string)>
        {
            ("Objectives", Known(LivePursuitFlags.ObjectivesKnown), ""), ("Conditions", conditions, ""), ("Effects", Known(LivePursuitFlags.EffectsKnown), ""),
            ("Reason", InvestmentTableViews.PursuitReason(held.Reason), "Why the game would or would not complete it now.")
        };
        foreach (var objective in held.Objectives)
        {
            string name = step.Tables.Objectives.FirstOrDefault(row => row.Hash == objective.ObjectiveHash) is { } installed
                ? OneLine(InvestmentTableViews.ObjectiveName(step.Tables, installed).Text) : $"Objective 0x{objective.ObjectiveHash:X8}";
            string value = !objective.Flags.HasFlag(LivePursuitObjectiveFlags.Known) ? InvestmentTableViews.PursuitReason(held.Reason)
                : $"{objective.Value:N0} / {objective.Target:N0}{Dot}" + (objective.Flags.HasFlag(LivePursuitObjectiveFlags.Skipped) ? "skipped"
                    : objective.Flags.HasFlag(LivePursuitObjectiveFlags.Complete) ? "complete" : "incomplete");
            rows.Add((name, value, $"{name}\n{value}"));
        }
        StudioUi.Facts("pursuit-saved", rows);
    }

    /** The installed identity and links, the shown held copy's saved values in Live, then any pursuit notes. */
    private static void DrawData(Step step)
    {
        StudioUi.Facts("pursuit-data", Installed(step.Catalog, step.Tables, step.Item, step.Pursuit));
        if (step.Live) DrawSaved(step);
        if (step.Pursuit?.Diagnostics is { Count: > 0 } notes && ImGui.CollapsingHeader($"Pursuit notes ({notes.Count})###pursuit-notes"))
            StudioUi.Facts("pursuit-notes", notes);
    }

    /** The shown held copy's saved row. Draws nothing while the game reports no held copy. */
    private static void DrawSaved(Step step)
    {
        var live = step.Session;
        if (LiveGate.Missing(live, live.Pursuits, LiveNeed.Pursuits) is not null || step.Held is not { } held) return;
        Block("Saved values", "The shown held copy as the game saved it.");
        StudioUi.Facts("pursuit-raw", Saved(held));
    }

    /** The step's installed identity, chain and the slots its blocks name. */
    internal static IReadOnlyList<(string Name, string Value, string Tip)> Installed(InvestmentCatalog catalog, InvestmentTables tables, InvestmentItem item,
        InvestmentPursuit? pursuit)
    {
        var rows = new List<(string, string, string)> { ("Item", $"{item.Index}{Dot}0x{item.Hash:X8}", "") };
        if (pursuit is null) return rows;
        var unlocks = tables.Unlocks;
        string Joined(IEnumerable<string> labels) => string.Join(", ", labels) is { Length: > 0 } text ? text : "None";
        string Site(InvestmentReference reference) => reference.Index == Absent ? "None" : $"Site {reference.Index}";
        rows.Add(("Quest owner", pursuit.QuestOwner == Absent ? "None"
            : $"{InvestmentTableViews.ItemLabel(catalog, pursuit.QuestOwner)}{Dot}item {pursuit.QuestOwner}", ""));
        if (pursuit.Chain is { } chain)
            rows.Add(("Chain value", chain.ValueSlot == Absent ? "None" : unlocks.ValueLabel(chain.ValueSlot).Text,
                chain.ModeDecoded ? "Each step's value identifies it; it is not a progress count." : $"Mode {chain.Mode}. Only mode 1 is decoded."));
        rows.Add(("Primary", Site(pursuit.Primary), pursuit.Primary.Label));
        rows.Add(("Secondary", Site(pursuit.Secondary), pursuit.Secondary.Label));
        rows.Add(("Context value", pursuit.ContextValue.Label, ""));
        rows.Add(("Completion value", pursuit.CompletionValue.Label, ""));
        rows.Add(("Presence flags", Joined(pursuit.PresenceFlags.Select(slot => unlocks.FlagLabel(slot).Text)), ""));
        rows.Add(("Item values", Joined(pursuit.InstanceValues.Select(slot => unlocks.ValueLabel(slot).Text)), "Value slots the item keeps on its own instance."));
        return rows;
    }

    /** The held copy's saved row: identity, serial, quantity and item flags. */
    internal static IReadOnlyList<(string Name, string Value, string Tip)> Saved(LiveHeldPursuit held) =>
    [
        ("Instance", $"0x{held.Instance:X16}", ""), ("Serial", held.MutationSerial.ToString(CultureInfo.InvariantCulture), ""),
        ("Quantity", held.Quantity.ToString(CultureInfo.InvariantCulture), ""),
        ("Item flags", $"0x{held.ItemFlags:X8}" + (InvestmentTableViews.Tracked(held) ? Dot + "Tracked" : ""), "")
    ];
}
