using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar;

/// <summary>
/// Headless CLI runner for the DMA test, used for SSH / automation verification
/// without bringing up the Avalonia GUI. Mirrors Lone's "lone-dma-test.exe full".
///
///   Sylnar.exe --dmatest full | latency | throughput | stress
///
/// Sylnar is built as OutputType=WinExe (no console subsystem), so when launched
/// over SSH / from a redirected pipe there is no console to write to. We attach to the
/// parent console if one exists (AttachConsole(-1)), otherwise allocate a fresh one
/// (AllocConsole), then reopen Console.Out onto it. As a belt-and-suspenders fallback
/// (console attach can be imperfect under some SSH/service contexts) the full result
/// summary is ALSO written to dmatest_result.txt next to the exe, so an automation
/// harness can always capture the output by reading that file.
///
/// This path is entirely additive: it never touches the GUI, the DMA test engine,
/// DeployToTools, or mmap features. It is reached only when args[0] == "--dmatest".
/// </summary>
internal static class HeadlessDmaTest
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    /// <summary>
    /// Entry point for the headless DMA test. Attaches a console, runs the requested
    /// test synchronously, prints + writes the result, then Environment.Exit()s.
    /// Never returns to the caller (always exits the process).
    /// </summary>
    public static void Run(string[] args)
    {
        EnsureConsole();

        // mode := args[1] if present, else "full". (args[0] is "--dmatest".)
        var mode = (args.Length >= 2 ? args[1] : "full").Trim().ToLowerInvariant();
        var sb = new StringBuilder();

        try
        {
            // Minimal services: LogService needs only an ILogger<LogService>, which we
            // satisfy with NullLogger (no UI sink required for the headless result).
            var log = new LogService(NullLogger<LogService>.Instance);
            var dma = new DmaTestService(log);

            // Extract the embedded leechcore/vmm/FTDI DLLs to temp. Same path the GUI
            // uses; idempotent. If it throws, the catch below reports [FAIL] and exits 1.
            dma.EnsureLibraries();

            // Console-writing progress so a watcher sees liveness; the result file does
            // not depend on it.
            var progress = new Progress<FlashProgress>(p =>
            {
                try { Console.WriteLine($"  [{p.Percentage,3}%] {p.Stage}: {p.Message}"); }
                catch { /* console may be detached under some SSH contexts */ }
            });

            Console.WriteLine($"Sylnar headless DMA test - mode: {mode}");
            Console.WriteLine("Connecting and running... (this may take from seconds to minutes)");
            Console.Out.Flush();

            DmaTestResult result = mode switch
            {
                "full"       => dma.RunFullTestAsync(progress, CancellationToken.None).GetAwaiter().GetResult(),
                "latency"    => dma.RunLatencyTestAsync(TimeSpan.FromSeconds(30), progress, default).GetAwaiter().GetResult(),
                "throughput" => dma.RunThroughputTestAsync(TimeSpan.FromSeconds(15), progress, default).GetAwaiter().GetResult(),
                "stress"     => dma.RunStressTestAsync(TimeSpan.FromMinutes(1), progress, default).GetAwaiter().GetResult(),
                _            => throw new ArgumentException(
                                    $"Unknown DMA test mode '{mode}'. Valid: full | latency | throughput | stress"),
            };

            FormatResult(sb, result);
            Emit(sb.ToString());
            Environment.Exit(result.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            sb.Clear();
            sb.AppendLine($"[FAIL] {ex.Message}");
            Emit(sb.ToString());
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Headless deploy: generate a fresh probe-verified mmap, then run DeployToTools to
    /// push the custom leechcore + FTDI chain + fresh mmap into every DMA tool folder on
    /// this PC. Prints + writes the DeployResult, then Environment.Exit()s.
    ///   Sylnar.exe --deploy
    /// </summary>
    public static void RunDeploy(string[] args)
    {
        EnsureConsole();
        var sb = new StringBuilder();
        try
        {
            var log = new LogService(NullLogger<LogService>.Instance);
            var dma = new DmaTestService(log);
            dma.EnsureLibraries();

            var progress = new Progress<FlashProgress>(p =>
            {
                try { Console.WriteLine($"  [{p.Percentage,3}%] {p.Stage}: {p.Message}"); }
                catch { }
            });

            Console.WriteLine("Sylnar headless deploy - generating mmap, then deploying to tool folders...");
            Console.Out.Flush();

            // Fresh probe-verified mmap so each tool folder gets a current map.
            var mmap = dma.GenerateMmapAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
            string mmapContent = mmap.Success ? mmap.Content : "";
            if (!mmap.Success)
                Console.WriteLine($"  [warn] mmap generation failed ({mmap.ErrorMessage}); deploying DLLs without a fresh mmap.");

            var dr = dma.DeployToToolsAsync(mmapContent, progress, CancellationToken.None).GetAwaiter().GetResult();

            sb.AppendLine("=== Sylnar Deploy-to-Tools Result ===");
            sb.AppendLine($"Success            : {dr.Success}");
            sb.AppendLine($"Folders found      : {dr.FoldersFound}");
            sb.AppendLine($"Leechcore replaced : {dr.LeechcoreReplaced}");
            sb.AppendLine($"FTDI chain filled  : {dr.FtdiChainCompleted}");
            sb.AppendLine($"Mmap written       : {dr.MmapWritten}");
            if (dr.Updated.Count > 0)  { sb.AppendLine("Updated:");  foreach (var u in dr.Updated)  sb.AppendLine($"  + {u}"); }
            if (dr.Skipped.Count > 0)  { sb.AppendLine("Skipped:");  foreach (var s in dr.Skipped)  sb.AppendLine($"  - {s}"); }
            if (dr.Flagged.Count > 0)  { sb.AppendLine("Flagged:");  foreach (var f in dr.Flagged)  sb.AppendLine($"  ! {f}"); }
            if (!string.IsNullOrWhiteSpace(dr.Summary)) sb.AppendLine($"Summary: {dr.Summary}");
            if (!string.IsNullOrWhiteSpace(dr.Error))   sb.AppendLine($"Error: {dr.Error}");

            Emit(sb.ToString());
            Environment.Exit(dr.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            sb.Clear();
            sb.AppendLine($"[FAIL] {ex.Message}");
            Emit(sb.ToString());
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Attach to the parent console (works for SSH / cmd / redirected pipe). If there is
    /// no parent console, allocate a new one. Then rebind Console.Out so WriteLine reaches
    /// the attached console rather than the (absent) WinExe console.
    /// </summary>
    private static void EnsureConsole()
    {
        try
        {
            bool attached = AttachConsole(ATTACH_PARENT_PROCESS);
            if (!attached)
                AllocConsole();

            // Rebind the standard output stream onto the console handle. Without this,
            // Console.Out on a WinExe with a freshly attached/allocated console still
            // points at the original (null) stream.
            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(writer);

            try
            {
                var stderr = Console.OpenStandardError();
                var ewriter = new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetError(ewriter);
            }
            catch { /* stderr rebind is best-effort */ }
        }
        catch
        {
            // If console wiring fails entirely we still write dmatest_result.txt, so
            // automation capture works regardless.
        }
    }

    /// <summary>Write text to the console (best-effort) and always to dmatest_result.txt.</summary>
    private static void Emit(string text)
    {
        try
        {
            Console.WriteLine();
            Console.Write(text);
            Console.Out.Flush();
        }
        catch { /* console may be unavailable; the file fallback below still works */ }

        try
        {
            var dir = AppContext.BaseDirectory;
            var path = Path.Combine(dir, "dmatest_result.txt");
            File.WriteAllText(path, text);
        }
        catch { /* if even the file write fails there is nothing more we can do */ }
    }

    /// <summary>
    /// Build a plain-text summary. Includes the always-present fields (TestType,
    /// OverallRating, Duration, ErrorMessage) plus whichever per-test fields the chosen
    /// test populated (latency RPS/avg, throughput MB/s, stress soak stats), and the
    /// failed-read / recovered-transient counters where present.
    /// </summary>
    private static void FormatResult(StringBuilder sb, DmaTestResult r)
    {
        sb.AppendLine("=== Sylnar DMA Test Result ===");
        sb.AppendLine($"TestType      : {r.TestType}");
        sb.AppendLine($"Result        : {(r.Success ? "PASS" : "FAIL")}");
        sb.AppendLine($"OverallRating : {r.OverallRating}");
        sb.AppendLine($"Duration      : {r.Duration.TotalSeconds:F1} s");

        var type = r.TestType?.ToLowerInvariant() ?? "";

        if (type == "latency" || type == "full")
        {
            sb.AppendLine($"Latency RPS   : {r.LatencyRps:N0}");
            sb.AppendLine($"Latency avg   : {r.LatencyAvgUs:N0} us  (min {r.LatencyMinUs:N0} / max {r.LatencyMaxUs:N0})");
            sb.AppendLine($"Latency rating: {r.LatencyRating}");
            sb.AppendLine($"Latency reads : {r.LatencyTotalReads:N0} total, {r.LatencyFailedReads:N0} failed, {r.LatencyRecoveredTransient:N0} recovered transient");
        }

        if (type == "throughput" || type == "full")
        {
            sb.AppendLine($"Throughput    : {r.ThroughputMBps:F2} MB/s");
            sb.AppendLine($"Tput rating   : {r.ThroughputRating}");
            sb.AppendLine($"Tput reads    : {r.ThroughputTotalReads:N0} total, {r.ThroughputFailedReads:N0} failed, {r.ThroughputRecoveredTransient:N0} recovered transient");
        }

        if (type == "stress")
        {
            sb.AppendLine($"Stress dur    : {r.StressDuration.TotalSeconds:F0} s");
            sb.AppendLine($"Stress reads  : {r.StressTotalReads:N0} total");
            sb.AppendLine($"Stress failed : {r.StressFailedReads:N0} ({r.StressFailPct:F3}%), max consecutive {r.StressMaxConsecFails:N0}");
            sb.AppendLine($"Stress recov  : {r.StressRecoveredTransient:N0} recovered transient");
            sb.AppendLine($"Stress rating : {r.StressRating}");
        }

        if (r.ProcessFound && !string.IsNullOrWhiteSpace(r.ProcessInfo))
            sb.AppendLine($"Process       : {r.ProcessInfo}");

        if (!string.IsNullOrWhiteSpace(r.ErrorMessage))
            sb.AppendLine($"ErrorMessage  : {r.ErrorMessage}");
    }
}
