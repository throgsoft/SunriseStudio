using System.Globalization;

namespace SunriseStudio.Editor;

/** Writes an unhandled exception's full text to %LOCALAPPDATA%\Sunrise Studio\logs, so a crash leaves its stack behind. */
public static class StudioCrashLog
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sunrise Studio", "logs");

    /**
     * Writes crash-<time>.txt for the exception, with origin naming where it was caught.
     * @return The file, or null when it could not be written.
     */
    public static string? Write(Exception error, string origin) => Write(error, origin, Folder, DateTime.Now);

    internal static string? Write(Exception error, string origin, string folder, DateTime time)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string name = "crash-" + time.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(folder, name + ".txt");
            for (int copy = 2; File.Exists(path); copy++) path = Path.Combine(folder, $"{name}-{copy}.txt");
            string text = $"Time: {time.ToString("O", CultureInfo.InvariantCulture)}\nSunrise Studio {StudioVersion.Text}\n"
                + $"OS: {Environment.OSVersion}\nOrigin: {origin}\n\n{error}\n";
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file)) writer.Write(text);
            return path;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
    }

    /** The message a crash shows, naming the log when one was written. */
    public static string Message(Exception error, string? path) =>
        path is null ? error.Message : $"{error.Message}\n\nDetails were saved to {path}.";
}
