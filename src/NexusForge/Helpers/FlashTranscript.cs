using System.Diagnostics;
using System.Text;

namespace Sylnar.Helpers;

/// <summary>
/// A complete, timestamped record of one SPI flash: every line OpenOCD printed on
/// either stream, in the order the app read it, plus a header and a footer.
///
/// v1.1.32: added because the app kept only its own one-line summaries, and threw
/// OpenOCD's raw output away whenever a flash did not fail. That made the erase
/// problem impossible to see from outside: the lines that say whether the chip took
/// the whole-chip or the per-sector erase, and how long the erase really took, never
/// reached anyone.
///
/// Written to %LOCALAPPDATA%\Sylnar\flash\flash-yyyyMMdd-HHmmss.log with AutoFlush,
/// so a run that is interrupted, or a window that is closed mid-write, still leaves
/// every line up to that moment on disk; a missing footer means the run was cut off.
///
/// Nothing here may ever break a flash. Every I/O call is guarded and a failure
/// simply turns the transcript off for the rest of the run.
/// </summary>
internal sealed class FlashTranscript : IDisposable
{
    private const int KeepNewest = 20;

    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private StreamWriter? _writer;

    /// <summary>Full path of the transcript, or null if it could not be created.</summary>
    public string? FilePath { get; }

    /// <summary>The path written with %LOCALAPPDATA% in place of the user's profile, for the visible log.</summary>
    public string? DisplayPath { get; }

    public FlashTranscript()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "Sylnar", "flash");
            Directory.CreateDirectory(dir);

            var name = $"flash-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            var path = Path.Combine(dir, name);
            // Two flashes inside the same second: keep both.
            for (int n = 2; File.Exists(path) && n < 100; n++)
                path = Path.Combine(dir, $"flash-{DateTime.Now:yyyyMMdd-HHmmss}-{n}.log");

            _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            DisplayPath = Path.Combine("%LOCALAPPDATA%", "Sylnar", "flash", Path.GetFileName(path));

            Prune(dir);
        }
        catch
        {
            _writer = null;
            FilePath = null;
            DisplayPath = null;
        }
    }

    /// <summary>A line OpenOCD printed. <paramref name="stream"/> is "ERR" or "OUT".</summary>
    public void Line(string stream, string text) => Write($"{Stamp()} {stream}| {text}");

    /// <summary>Something the app itself observed or decided.</summary>
    public void Note(string text) => Write($"{Stamp()} APP| {text}");

    private string Stamp() => $"[+{_clock.Elapsed.TotalSeconds,9:F3}s]";

    private void Write(string line)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try { _writer.WriteLine(line); }
            catch { try { _writer.Dispose(); } catch { } _writer = null; }
        }
    }

    private static void Prune(string dir)
    {
        try
        {
            var old = new DirectoryInfo(dir)
                .GetFiles("flash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepNewest);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
