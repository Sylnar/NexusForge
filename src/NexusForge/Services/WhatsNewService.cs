using Sylnar.Helpers;

namespace Sylnar.Services;

public static class WhatsNewService
{
    private const string NotesVersion = "1.1.39";
    private const string Notes =
        "• Finer 256 KB memory-map scanning and a 200k-page sample pool.\n" +
        "• Removed the Microsoft Symbol Store terms popup.\n" +
        "• Update notes now appear once after an update.";

    private static string StatePath => Path.Combine(
        string.IsNullOrEmpty(CrashLogger.LogDirectory) ? Path.GetTempPath() : CrashLogger.LogDirectory,
        "whats_new_version.txt");

    public static string? ConsumeUpdatedNotes()
    {
        var current = VersionInfo.Value;
        try
        {
            var previous = File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : null;
            if (previous == current) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, current);

            if (!Version.TryParse(previous, out var oldVersion) ||
                !Version.TryParse(current, out var currentVersion) ||
                currentVersion <= oldVersion ||
                NotesVersion != current)
                return null;

            return Notes;
        }
        catch
        {
            return null;
        }
    }
}
