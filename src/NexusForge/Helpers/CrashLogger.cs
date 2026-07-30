using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Sylnar.Helpers;

/// <summary>
/// Global unhandled-exception logger. Writes to %LOCALAPPDATA%\Sylnar\crash.log
/// so silent process deaths on Windows (especially Win11 25H2) leave a forensic trail
/// instead of a flash-and-bye. Also tracks normal start/exit so we can detect when a
/// previous run crashed (used by Program.cs to wipe the .NET single-file extraction
/// cache, which is the root cause of "opens once, then dies on every relaunch" on
/// Win11 25H2).
/// </summary>
internal static class CrashLogger
{
    private static readonly object _lock = new();
    private static string? _logPath;
    private static string? _markerPath;
    private static bool _installed;

    public static string LogDirectory { get; private set; } = string.Empty;
    public static string LogPath => _logPath ?? string.Empty;
    public static bool PreviousRunCrashed { get; private set; }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LogDirectory = Path.Combine(appData, "Sylnar");
            Directory.CreateDirectory(LogDirectory);

            _logPath = Path.Combine(LogDirectory, "crash.log");
            _markerPath = Path.Combine(LogDirectory, "running.marker");

            // If the marker exists, the previous run did not exit cleanly.
            PreviousRunCrashed = File.Exists(_markerPath);

            // Drop a fresh marker for THIS run; we delete it on clean exit.
            try { File.WriteAllText(_markerPath, $"{Environment.ProcessId} {DateTime.UtcNow:o}"); }
            catch { }

            // Trim oversized log (>1 MB) so it doesn't grow forever.
            try
            {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 1_048_576)
                {
                    var tail = File.ReadAllText(_logPath);
                    File.WriteAllText(_logPath, tail.Substring(tail.Length / 2));
                }
            }
            catch { }
        }
        catch
        {
            // If we can't write to LocalAppData, give up silently — we tried.
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => MarkCleanExit();

        WriteLine($"=== Sylnar launched (PID {Environment.ProcessId}, " +
                  $"v{Assembly.GetExecutingAssembly().GetName().Version}, " +
                  $"OS {Environment.OSVersion}, .NET {Environment.Version}) ===");
        if (PreviousRunCrashed)
            WriteLine("*** Previous run did not exit cleanly — single-file extract cache will be reset.");
    }

    public static void MarkCleanExit()
    {
        try
        {
            if (_markerPath != null && File.Exists(_markerPath))
                File.Delete(_markerPath);
        }
        catch { }
    }

    public static void WriteLine(string message)
    {
        if (_logPath == null) return;
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public static void LogException(string source, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== UNHANDLED EXCEPTION ({source}) ===");
        FormatException(sb, ex, 0);
        WriteLine(sb.ToString());
    }

    private static void FormatException(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}{ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            foreach (var line in ex.StackTrace.Split('\n'))
                sb.AppendLine($"{indent}  {line.TrimEnd()}");
        }
        if (ex.InnerException != null)
        {
            sb.AppendLine($"{indent}--- Inner ---");
            FormatException(sb, ex.InnerException, depth + 1);
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogException("AppDomain.UnhandledException", ex);
        else
            WriteLine($"=== UNHANDLED non-Exception object: {e.ExceptionObject} ===");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    [DebuggerNonUserCode]
    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        // Only log first-chance for native interop / loading failures — those are
        // the silent-death class. Skip everyday business exceptions to avoid spam.
        var t = e.Exception.GetType().FullName ?? "";
        if (t.Contains("DllNotFound") ||
            t.Contains("BadImageFormat") ||
            t.Contains("FileLoadException") ||
            t.Contains("TypeInitialization"))
        {
            WriteLine($"FIRST-CHANCE {t}: {e.Exception.Message}");
        }
    }
}
