namespace Sylnar.Helpers;

/// <summary>
/// Temp folders this process created. The shutdown cleanup script deletes exactly
/// these instead of globbing %TEMP%\nf_* / drv_*, which also wiped folders that
/// belonged to other programs or to a second Sylnar instance still running.
/// </summary>
public static class OwnedTempDirs
{
    private static readonly object _lock = new();
    private static readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public static string Register(string dir)
    {
        lock (_lock) _dirs.Add(dir);
        return dir;
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (_lock) return _dirs.ToList();
    }
}
