using System.Numerics;
using ImGuiNET;
using SunriseStudio.Editor;
using SunriseStudio.Viewport;

/** The browser grid's column and cell arithmetic, its saved choice, and real frames of one grid with no GPU. */
internal static class BrowserGridChecks
{
    // More cells than any window shows, so the clipper must skip most of them.
    private const int CellCount = 500;
    private const float GridHeight = 300;

    public static void Run(string root)
    {
        CheckLayout();
        CheckSettings(root);
        CheckFrames();
        Console.WriteLine("browser_grid layout=pass settings=pass frames=pass");
    }

    private static void CheckLayout()
    {
        var cell = new Vector2(68, 100);
        Check(IconGrid.Columns(0, cell.X) == 1 && IconGrid.Columns(cell.X - 1, cell.X) == 1, "A grid narrower than one cell has no column.");
        Check(IconGrid.Columns(cell.X, cell.X) == 1 && IconGrid.Columns(3 * cell.X, cell.X) == 3
            && IconGrid.Columns(4 * cell.X - 1, cell.X) == 3, "Exact multiples of the cell width do not give whole columns.");
        // Three cells in a row 30 px wider than they need: 15 px between each pair, so the last cell ends at the right edge.
        float width = 3 * cell.X + 30;
        var spacing = IconGrid.Spacing(width, IconGrid.Columns(width, cell.X), cell, 6);
        Check(spacing == new Vector2(15, 6), $"The leftover width is spread as {spacing}.");
        Check(IconGrid.Spacing(cell.X / 2, 1, cell, 6).X == 0, "A single column is given spacing.");
        Check(IconGrid.CellOrigin(0, 3, cell, spacing) == Vector2.Zero, "The first cell does not start the grid.");
        var last = IconGrid.CellOrigin(2, 3, cell, spacing);
        Check(last == new Vector2(2 * (cell.X + 15), 0) && last.X + cell.X == width, $"The last cell of a row starts at {last}.");
        Check(IconGrid.CellOrigin(3, 3, cell, spacing) == new Vector2(0, cell.Y + 6), "The fourth cell does not wrap to the second row.");
        Check(IconGrid.CellOrigin(7, 3, cell, spacing) == new Vector2(cell.X + 15, 2 * (cell.Y + 6)), "A later wrapped cell is misplaced.");
    }

    private static void CheckSettings(string root)
    {
        string path = Path.Combine(root, "grid-settings.json");
        Check(!new StudioSettings().BrowserGrid, "Browser lists start as a grid.");
        new StudioSettings { BrowserGrid = true }.Save(path);
        Check(StudioSettings.Load(path).BrowserGrid, "The grid choice did not survive a save and load.");
        File.WriteAllText(path, "{\"BrowserWidth\":320}");
        Check(!StudioSettings.Load(path).BrowserGrid, "Older settings without the grid choice load as a grid.");
    }

    private static unsafe void CheckFrames()
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.NativePtr->IniFilename = null;
            io.DisplaySize = new(1200, 800); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _);
            ImGui.GetStyle().WindowPadding = Vector2.Zero;
            var grid = new IconGrid();
            var requested = new HashSet<int>();
            int chosen = -1, hovered = -1, menu = -1;
            IconCell Cell(int index)
            {
                requested.Add(index);
                return new($"Item {index}", 0xFFFFFFFF, index % 2 == 0 ? "" : "Entity / Prop", null, index % 2 == 0, StudioIcon.Cube,
                    index % 3 == 0 ? "x3" : "", 0xFFFFFFFF, index % 5 / 4f, index % 7 == 0, index % 11 == 0, index % 4 == 0, index == 1, index % 6 == 0);
            }
            void Frame(Vector2 mouse, bool? down = null, int button = 0)
            {
                io.AddMousePosEvent(mouse.X, mouse.Y);
                if (down is { } held) io.AddMouseButtonEvent(button, held);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("grid-check", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove);
                requested.Clear();
                int clicked = grid.Draw("grid", CellCount, Cell, 1, GridHeight, out hovered, out int context);
                if (clicked >= 0) chosen = clicked;
                if (context >= 0) menu = context;
                ImGui.End(); ImGui.Render();
            }
            var size = IconGrid.CellSize(1);
            Frame(new(-100)); Frame(new(-100));
            Check(requested.Count is > 0 and < CellCount / 4 && requested.Min() == 0,
                $"The grid asked for {requested.Count} cells of {CellCount} to fill {GridHeight} px.");
            float gap = ImGui.GetStyle().ItemSpacing.X;
            int columns = IconGrid.Columns(io.DisplaySize.X - ImGui.GetStyle().ScrollbarSize + gap, size.X + gap);
            var first = size / 2;
            var second = new Vector2(size.X / 2, size.Y + ImGui.GetStyle().ItemSpacing.Y + size.Y / 2);
            Frame(first); Frame(first, true); Frame(first, false);
            Check(chosen == 0 && hovered == 0, $"A click on the first cell chose {chosen} with {hovered} hovered.");
            Frame(second); Frame(second, true); Frame(second, false);
            Check(chosen == columns, $"A click on the second row chose {chosen}, not cell {columns}.");
            Frame(first, true, 1); Frame(first, false, 1);
            Check(menu == 0, $"A right click on the first cell opened the menu of {menu}.");
            grid.ScrollTo(5 * columns + 3);
            Frame(new(-100)); Frame(new(-100));
            Check(grid.FirstVisible == 5 * columns && requested.Contains(5 * columns + 3),
                $"Scrolling to a cell showed {grid.FirstVisible} first.");
            grid.ResetScroll();
            Frame(new(-100)); Frame(new(-100));
            Check(grid.FirstVisible == 0 && requested.Contains(0), "Reset did not scroll the grid to the top.");
            float line = ImGui.CalcTextSize("Red pen").X;
            Check(IconGrid.NameLines("Red", line) == ("Red", ""), "A short name was split.");
            Check(IconGrid.NameLines("Red pen lid", line) == ("Red pen", "lid"), "A name did not break between words.");
            var (top, bottom) = IconGrid.NameLines("Red pen lid and a long cord", line);
            Check(top == "Red pen" && bottom.EndsWith("...", StringComparison.Ordinal) && ImGui.CalcTextSize(bottom).X <= line,
                $"A long name ended as {top} / {bottom}.");
            var (word, rest) = IconGrid.NameLines("Overlongword tail", ImGui.CalcTextSize("Overlon...").X);
            Check(word == "Overlon..." && rest == "tail", $"A first word wider than a line ended as {word} / {rest}.");
        }
        finally { ImGui.DestroyContext(context); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
