using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/**
 * One flag or value an objective reads. ViaShared is the shared expression it is read through, or -1. Binding is the saved
 * row it binds to, or null, and Uses counts the use lines that row lists. Live is the row the game can read, or null when
 * Studio cannot bind it. Class is the character class a class flag names, or -1. Proven is true when the label is proven.
 */
internal sealed record SourceRow(string Label, string Tip, int ViaShared, UnlockBinding? Binding, uint Slot, int Uses,
    LiveUnlockSource? Live = null, int Class = -1, bool Proven = false);

/** One objective's flags and values. Rows is empty when the objective keeps its progress on the item or is not in the table. */
internal sealed record ObjectiveSources(int Ordinal, IReadOnlyList<SourceRow> Rows);

internal sealed partial class PursuitInspector
{
    /** Uses a source row lists before its list scrolls. */
    private const int ShownUses = 8;
    private const string UnboundTip = "Studio reads this row's label from the installed slot tables; no mapping confirms it.";
    private const string NotReadable = "Not readable", NotReadableTip = "Studio cannot bind this source to a saved row.";
    private const string Unknown = "Unknown", UnreadTip = "The game could not read this row.";
    private const string ClassTip = "Set when the selected character is this class; not saved data.";
    private (InvestmentTables? Tables, InvestmentPursuit? Pursuit) sourcesKey;
    private IReadOnlyList<ObjectiveSources> sources = [];
    /** The sources of the pursuit last drawn. */
    internal IReadOnlyList<ObjectiveSources> ListedSources => sources;

    /** What a source row's uses button does: lists every use of its saved row under it, or hides them. */
    internal void ToggleUses(InvestmentCatalog catalog, InvestmentTables tables, int ordinal, int index)
    {
        if (expanded.Remove((ordinal, index))) return;
        var row = sources.First(objective => objective.Ordinal == ordinal).Rows[index];
        if (row.Binding is { } binding) expanded[(ordinal, index)] = UseLines(catalog, tables, binding, row.Slot);
    }
    // Source rows whose uses are expanded, by objective ordinal and row, with their lines built on first expansion.
    private readonly Dictionary<(int Objective, int Row), IReadOnlyList<RewardLine>> expanded = [];

    /** Every objective's sources, rebuilt when the tables or the shown pursuit change. */
    private IReadOnlyList<ObjectiveSources> SourcesOf(Step step)
    {
        if (!ReferenceEquals(sourcesKey.Tables, step.Tables) || !ReferenceEquals(sourcesKey.Pursuit, step.Pursuit))
        { sourcesKey = (step.Tables, step.Pursuit); sources = Sources(step.Catalog, step.Tables, step.Pursuit!); expanded.Clear(); }
        return sources;
    }

    /** The flags and values one objective's source reads, each with its value in Live and its other uses. */
    private void DrawSourceRows(Step step, int ordinal)
    {
        if (SourcesOf(step).FirstOrDefault(row => row.Ordinal == ordinal) is not { } objective) return;
        if (objective.Rows.Count == 0) { ImGui.TextDisabled("No flags or values."); return; }
        // Live's Now column is sized from installed data alone, so connecting never moves a column. A longer number is shortened.
        string? widest = !step.Live ? null : objective.Rows.Any(source => source.Live is null && source.Class < 0) ? NotReadable : Unknown;
        // Used by fits this objective's widest count, so the source column keeps the rest of the width.
        float usedBy = objective.Rows.Max(source => source.Uses == 0 || source.Binding is null ? ImGui.CalcTextSize("None").X
            : ImGui.CalcTextSize(InvestmentTableViews.Count(source.Uses, "use")).X + ImGui.GetStyle().FramePadding.X * 2);
        // Expanded uses draw between table segments that share one id, so every segment keeps the same column widths.
        int row = 0;
        while (row < objective.Rows.Count)
        {
            if (!BeginSourceTable(objective.Ordinal, widest, usedBy)) return;
            if (row == 0) ImGui.TableHeadersRow();
            int first = row;
            for (; row < objective.Rows.Count; row++)
            {
                if (row != first && expanded.ContainsKey((objective.Ordinal, row - 1))) break;
                DrawSourceRow(step, objective, row);
            }
            ImGui.EndTable();
            if (expanded.TryGetValue((objective.Ordinal, row - 1), out var uses)) DrawUses(step, uses, row - 1);
        }
    }

    /** Begins one segment of the sources table: Source, then in Live a Now column as wide as widest, then Used by. */
    private static bool BeginSourceTable(int ordinal, string? widest, float usedBy)
    {
        if (!ImGui.BeginTable($"##sources-{ordinal}", widest is null ? 2 : 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return false;
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
        if (widest is not null) ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize(widest).X);
        ImGui.TableSetupColumn("Used by", ImGuiTableColumnFlags.WidthFixed, Math.Max(ImGui.CalcTextSize("Used by").X, usedBy));
        return true;
    }

    private void DrawSourceRow(Step step, ObjectiveSources objective, int index)
    {
        var row = objective.Rows[index];
        // Reads inside one shared expression sit under its name, indented, so the narrow column keeps each read's own label.
        if (row.ViaShared >= 0 && (index == 0 || objective.Rows[index - 1].ViaShared != row.ViaShared))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextDisabled($"Via shared {row.ViaShared}");
            StudioUi.Tip($"Read through shared expression {row.ViaShared}, which Sunrise inlines where it is referenced.");
        }
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        if (row.ViaShared >= 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetStyle().IndentSpacing);
        string label = row.Label;
        string shown = row.Proven ? StudioUi.FitTail(label, ImGui.GetContentRegionAvail().X) : StudioUi.Fit(label, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(shown);
        StudioUi.Tip(shown == label ? row.Tip : $"{label}\n{row.Tip}".TrimEnd());
        if (step.Live)
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            DrawNow(step.Session, row);
        }
        ImGui.TableNextColumn();
        if (row.Uses == 0 || row.Binding is not { } binding) { ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("None"); return; }
        ImGui.PushID(index);
        bool open = expanded.ContainsKey((objective.Ordinal, index));
        if (ImGui.SmallButton(InvestmentTableViews.Count(row.Uses, "use"))) ToggleUses(step.Catalog, step.Tables, objective.Ordinal, index);
        StudioUi.Tip(open ? "Hide the other uses of this saved row." : "List every use of this saved row in the installed tables.");
        ImGui.PopID();
    }

    /** One row's uses, each with a link to where it is. The list scrolls past ShownUses rows. */
    private static unsafe void DrawUses(Step step, IReadOnlyList<RewardLine> uses, int row)
    {
        float line = ImGui.GetFrameHeightWithSpacing();
        ImGui.Indent();
        ImGui.BeginChild($"uses-{row}", new(0, Math.Min(uses.Count, ShownUses) * line + ImGui.GetStyle().WindowPadding.Y), ImGuiChildFlags.None);
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(uses.Count, line);
        while (clipper.Step())
            for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                ImGui.PushID(i);
                DrawUse(step, uses[i]);
                ImGui.PopID();
            }
        clipper.End(); clipper.Destroy();
        ImGui.EndChild();
        ImGui.Unindent();
    }

    /** A use's text, giving way to its link at the right edge. */
    private static void DrawUse(Step step, RewardLine use)
    {
        var style = ImGui.GetStyle();
        float start = ImGui.GetCursorPosX(), width = ImGui.GetContentRegionAvail().X;
        string link = ItemRewardsPage.LinkLabel(use.Link);
        float linkWidth = link.Length == 0 ? 0 : ImGui.CalcTextSize(link).X + style.FramePadding.X * 2;
        ImGui.AlignTextToFramePadding();
        string text = StudioUi.Fit(use.Text, width - (link.Length == 0 ? 0 : linkWidth + style.ItemSpacing.X));
        if (use.Item >= 0 && use.Item < step.Catalog.Items.Count) ImGui.TextColored(StudioTheme.Rarity(step.Catalog.Items[use.Item].Tier), text);
        else ImGui.TextUnformatted(text);
        StudioUi.Tip(text == use.Text ? use.Tip : $"{use.Text}\n{use.Tip}".TrimEnd());
        if (link.Length == 0) return;
        ImGui.SameLine(start + width - linkWidth);
        if (!ImGui.SmallButton(link)) return;
        ItemRewardsPage.Follow(step.Context, step.Catalog, step.Tables, use);
        // Follow opens an item without choosing a page, so Live also asks for the Investment page.
        if (step.Live && use.Link == RewardLinkKind.Item) step.Context.ShowPage?.Invoke(InvestmentPage.PageId, null);
    }

    /** A source's current value: Set or Clear for a flag, the number for a value, else why the game gives none. */
    private static void DrawNow(LiveInvestmentSession live, SourceRow row)
    {
        if (row.Class >= 0) { DrawClassNow(live, row.Class); return; }
        if (row.Live is not { } source) { Muted(NotReadable, NotReadableTip); return; }
        if (Current(live, source, out var missing) is not { } value) { LiveGate.Cell(null, missing); return; }
        if (!value.Known) { Muted(Unknown, UnreadTip); return; }
        string text = source.Kind == LiveUnlockKind.Flag ? value.Value != 0 ? "Set" : "Clear" : value.Value.ToString("N0", CultureInfo.CurrentCulture);
        string shown = StudioUi.Fit(text, ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(shown);
        StudioUi.Tip(shown == text ? "" : text);
    }

    /** A class flag is not a saved row. It is set when the selected character is that class. */
    private static void DrawClassNow(LiveInvestmentSession live, int characterClass)
    {
        if (!live.Connected) { LiveGate.Cell(null, LiveGate.NotLinked(live, LiveNeed.Unlocks.See)); return; }
        if (live.Character is not { } character) { LiveGate.Cell(null, (LiveInvestmentProtocol.Describe("selected_character"), "")); return; }
        ImGui.TextUnformatted(character.CharacterClass == characterClass ? "Set" : "Clear");
        StudioUi.Tip(ClassTip);
    }

    /**
     * The game's value of one saved row. Each call while the row is drawn keeps it wanted.
     * @return The value, or null with the line to show instead until the game has given one.
     */
    private static LiveUnlockValue? Current(LiveInvestmentSession live, LiveUnlockSource source, out (string Text, string Tip)? missing)
    {
        if (live.Connected && live.Supports(LiveNeed.Unlocks.Capability)) live.WantUnlock(source);
        var value = live.Unlock(source);
        missing = LiveGate.Missing(live, LiveNeed.Unlocks, value is not null, live.UnlockError);
        return missing is null ? value : null;
    }

    private static void Muted(string text, string tip)
    {
        ImGui.TextDisabled(text);
        StudioUi.Tip(tip);
    }

    /** Every objective's sources, with the number of uses of each saved row they read. */
    internal static IReadOnlyList<ObjectiveSources> Sources(InvestmentCatalog catalog, InvestmentTables tables, InvestmentPursuit pursuit)
    {
        var unlocks = tables.Unlocks;
        var result = new List<ObjectiveSources>();
        for (int ordinal = 0; ordinal < pursuit.Objectives.Count; ordinal++)
        {
            ushort index = pursuit.Objectives[ordinal];
            if (index >= tables.Objectives.Count) { result.Add(new(ordinal, [])); continue; }
            var objective = tables.Objectives[index];
            var rows = tables.ProgressLane(objective, pursuit) >= 0 ? [] : unlocks.Reads(objective.Source).Select(read =>
            {
                var label = unlocks.Label(read.Instruction);
                var binding = unlocks.Binding(read.Instruction);
                // An unproven label names only the slot's kind, so the slot number goes first to tell two such reads apart in a
                // narrow column. A proven label ends with its row, which the column keeps by shortening the label from the front.
                return new SourceRow(label.Proven ? label.Text : $"Slot {read.Instruction.Operand}{Dot}{label.Text}",
                    label.Proven ? label.Detail : $"{label.Detail}\n{UnboundTip}\n{StudioUi.MayBeInaccurate}".TrimStart(),
                    read.ViaShared, binding, read.Instruction.Operand, binding is { } bound ? UseCount(tables, bound) : 0,
                    LiveUnlockSources.Of(unlocks, read.Instruction), unlocks.ClassOf(read.Instruction), label.Proven);
            }).ToArray();
            result.Add(new(ordinal, rows));
        }
        return result;
    }

    /** The number of lines UseLines gives, without building them. An objective use is one line per item that lists it. */
    private static int UseCount(InvestmentTables tables, UnlockBinding binding) => Distinct(tables.References.Users(binding))
        .Sum(use => use.Kind is UnlockUseKind.ObjectiveSource or UnlockUseKind.ObjectiveTarget ? tables.ObjectiveUsers(use.Owner).Count : 1);

    /** One use per kind, owner, element and slot, so a read reached through several shared expressions counts once. */
    private static IEnumerable<UnlockUse> Distinct(IReadOnlyList<UnlockUse> uses) => uses.DistinctBy(use => (use.Kind, use.Owner, use.Element, use.Slot));

    /** Every use of one saved row as a line with its link. A use that names a different slot says which. */
    internal static IReadOnlyList<RewardLine> UseLines(InvestmentCatalog catalog, InvestmentTables tables, UnlockBinding binding, uint slot)
    {
        bool pools = InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Pools));
        bool pass = InvestmentTableViews.Usable(tables.TableTag(InvestmentRootSlots.Progressions));
        var lines = new List<RewardLine>();
        foreach (var use in Distinct(tables.References.Users(binding)))
        {
            string other = use.Slot != slot ? $" (slot {use.Slot.ToString(CultureInfo.InvariantCulture)})" : "";
            string via = use.ViaShared >= 0 ? $"Through shared expression {use.ViaShared}." : "";
            RewardLine Item(int item, string text, string tip) =>
                new(text + other, "", $"{tip}\n{via}".Trim(), item < catalog.Items.Count ? item : -1,
                    item < catalog.Items.Count ? RewardLinkKind.Item : RewardLinkKind.None, item);
            switch (use.Kind)
            {
                case UnlockUseKind.ObjectiveSource or UnlockUseKind.ObjectiveTarget:
                    string description = use.Owner < tables.Objectives.Count
                        ? OneLine(InvestmentTableViews.ObjectiveName(tables, tables.Objectives[use.Owner]).Text) : $"Objective {use.Owner}";
                    string what = use.Kind == UnlockUseKind.ObjectiveSource ? "Objective source" : "Objective target adjustment";
                    foreach (int item in tables.ObjectiveUsers(use.Owner))
                        lines.Add(Item(item, $"{InvestmentTableViews.ItemLabel(catalog, item)}: {description}"
                            + (use.Kind == UnlockUseKind.ObjectiveTarget ? " (target)" : ""), $"{what}, objective {use.Owner}."));
                    break;
                case UnlockUseKind.WrapperAcquired:
                    lines.Add(Item(use.Owner, $"Acquisition flag of {InvestmentTableViews.ItemLabel(catalog, use.Owner)}", "Set when the item is acquired."));
                    break;
                case UnlockUseKind.PassClaim or UnlockUseKind.PassCondition:
                    var row = tables.Progressions[use.Owner].Rewards.First(reward => reward.Index == use.Element);
                    lines.Add(new($"Season pass rank {row.Rank.ToString(CultureInfo.InvariantCulture)}{Dot}row {use.Element} "
                        + (use.Kind == UnlockUseKind.PassClaim ? "claim" : "condition") + other, "", $"Progression {use.Owner}\n{via}".Trim(),
                        Link: pass ? RewardLinkKind.PassRow : RewardLinkKind.None, Target: use.Owner, Element: use.Element));
                    break;
                case UnlockUseKind.PoolCondition or UnlockUseKind.PoolWeightRule:
                    lines.Add(new($"Pool {use.Owner}, entry {use.Element} " + (use.Kind == UnlockUseKind.PoolCondition ? "condition" : "weight rule") + other,
                        "", via, Link: pools ? RewardLinkKind.Pool : RewardLinkKind.None, Target: use.Owner, Element: use.Element));
                    break;
                case UnlockUseKind.CompletionValue or UnlockUseKind.ContextValue or UnlockUseKind.ChainValue:
                    string value = use.Kind switch
                    {
                        UnlockUseKind.CompletionValue => "completion value", UnlockUseKind.ContextValue => "context value", _ => "quest chain value"
                    };
                    lines.Add(Item(use.Owner, $"{InvestmentTableViews.ItemLabel(catalog, use.Owner)} {value}", ""));
                    break;
                case UnlockUseKind.PreviewCondition:
                    lines.Add(Item(use.Owner, $"{InvestmentTableViews.ItemLabel(catalog, use.Owner)} preview {use.Element}", InvestmentTableViews.PreviewTip));
                    break;
            }
        }
        return lines;
    }
}
