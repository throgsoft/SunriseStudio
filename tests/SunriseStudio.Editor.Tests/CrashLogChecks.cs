using System.Runtime.CompilerServices;
using SunriseStudio.Editor;

/** The crash log keeps the whole exception, and two crashes in one millisecond keep two files. */
internal static class CrashLogChecks
{
    public static void Run(string root)
    {
        string folder = Path.Combine(root, "crash-logs");
        var time = new DateTime(2026, 10, 16, 12, 34, 56, 789);
        Exception error;
        try { Throw(); throw new InvalidOperationException("Unreachable."); }
        catch (InvalidOperationException caught) { error = caught; }
        string first = StudioCrashLog.Write(error, "Frame", folder, time) ?? throw new InvalidDataException("The crash log was not written.");
        string second = StudioCrashLog.Write(error, "Frame", folder, time) ?? throw new InvalidDataException("The second crash log was not written.");
        string text = File.ReadAllText(first);
        Check(Path.GetFileName(first) == "crash-20261016-123456-789.txt" && Path.GetFileName(second) == "crash-20261016-123456-789-2.txt",
            $"The crash logs were named {Path.GetFileName(first)} and {Path.GetFileName(second)}.");
        Check(text.Contains("Origin: Frame", StringComparison.Ordinal) && text.Contains(StudioVersion.Text, StringComparison.Ordinal)
            && text.Contains("System.InvalidOperationException: The index was outside the attached list.", StringComparison.Ordinal)
            && text.Contains("System.IndexOutOfRangeException: Index was outside the bounds of the array.", StringComparison.Ordinal)
            && text.Contains(nameof(Throw), StringComparison.Ordinal) && text.Contains(nameof(Index), StringComparison.Ordinal),
            "The crash log lost the type, message, stack or inner exception.");
        Check(StudioCrashLog.Write(error, "Frame", Path.Combine(first, "not-a-folder"), time) is null, "A crash log that cannot be written threw.");
        Check(StudioCrashLog.Message(error, first).EndsWith($"Details were saved to {first}.", StringComparison.Ordinal)
            && StudioCrashLog.Message(error, null) == error.Message, "The crash message does not name the log.");
        Console.WriteLine("crash_log content=full names=unique unwritable=null");
    }

    // Not inlined, so both frames stay in the stack the log keeps.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Throw()
    {
        try { Index([], 1); }
        catch (IndexOutOfRangeException inner) { throw new InvalidOperationException("The index was outside the attached list.", inner); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Index(int[] values, int at) => values[at];

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
