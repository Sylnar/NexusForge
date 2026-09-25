using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Sylnar.Helpers;
using Sylnar.Models;

namespace Sylnar.Services;

internal static class VmmNative
{
    private const string VmmDll = "vmm.dll";
    private const string LcDll = "leechcore.dll";

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr VMMDLL_Initialize(int argc, string[] argv);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void VMMDLL_Close(IntPtr hVMM);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void VMMDLL_MemFree(IntPtr pvMem);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool VMMDLL_ConfigGet(IntPtr hVMM, ulong fOption, out ulong pqwValue);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool VMMDLL_Map_GetPhysMem(IntPtr hVMM, out IntPtr ppPhysMemMap);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool VMMDLL_PidGetFromName(IntPtr hVMM, string szProcName, out uint pdwPID);

    [DllImport(LcDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool LcRead(IntPtr hLC, ulong pa, uint cb, IntPtr pb);

    // LcCommand: send a special command to LeechCore (e.g., read PCIe config space).
    // ppbDataOut buffer is allocated by LeechCore and must be freed via LcMemFree.
    [DllImport(LcDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool LcCommand(IntPtr hLC, ulong fOption, uint cbDataIn, IntPtr pbDataIn,
                                        out IntPtr ppbDataOut, out uint pcbDataOut);

    [DllImport(LcDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void LcMemFree(IntPtr pvMem);

    public const ulong OPT_CORE_LEECHCORE_HANDLE = 0x4000001000000000;

    // LeechCore command IDs (from leechcore.h).
    // FPGA_PCIECFGSPACE_RD reads the FPGA endpoint's own 4 KB PCIe config space
    // via Type 0 config TLPs - works regardless of P2P routing or memory map.
    public const ulong LC_CMD_FPGA_PCIECFGSPACE_RD = 0x0000010300000000;

    // LC_CMD_MEMMAP_SET_STRUCT: install an in-memory leechcore memory map as an
    // array of LC_MEMMAP_ENTRY { QWORD pa; QWORD cb; QWORD paRemap }. Used as an
    // ALLOW-LIST: leechcore will only issue TLPs for physical addresses that fall
    // inside one of the supplied ranges, so a read can never touch a VBS/IOMMU-
    // fenced page (which on AMD wedges the link). pbDataIn = entry array,
    // cbDataIn = count * 24 bytes.
    public const ulong LC_CMD_MEMMAP_SET_STRUCT = 0x4000050000000000;
}

public class DmaTestResult
{
    public bool Success { get; set; }
    public string TestType { get; set; } = "";
    public string ErrorMessage { get; set; } = "";

    public int LatencyRps { get; set; }
    public int LatencyMinUs { get; set; }
    public int LatencyMaxUs { get; set; }
    public int LatencyAvgUs { get; set; }
    public long LatencyTotalReads { get; set; }
    public long LatencyFailedReads { get; set; }
    public long LatencyRecoveredTransient { get; set; }
    public string LatencyRating { get; set; } = "-";

    public float ThroughputMBps { get; set; }
    public long ThroughputTotalReads { get; set; }
    public long ThroughputFailedReads { get; set; }
    public long ThroughputRecoveredTransient { get; set; }
    public string ThroughputRating { get; set; } = "-";

    public bool ProcessFound { get; set; }
    public string ProcessInfo { get; set; } = "";

    // Stress (lone-style long-duration mixed-size stability test)
    public TimeSpan StressDuration { get; set; }
    public long StressTotalReads { get; set; }
    public long StressFailedReads { get; set; }
    public long StressRecoveredTransient { get; set; }
    public long StressMaxConsecFails { get; set; }
    public float StressFailPct { get; set; }
    public string StressRating { get; set; } = "-";

    public string OverallRating { get; set; } = "-";
    public TimeSpan Duration { get; set; }
}

public readonly struct PhysMemPage
{
    public ulong PageBase { get; init; }
    public ulong RemainingBytes { get; init; }
}

public class MmapResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int RegionCount { get; set; }
    public double TotalRamGb { get; set; }
    public string Content { get; set; } = "";
}

public class DeployResult
{
    public int FoldersFound { get; set; }
    public int LeechcoreReplaced { get; set; }
    public int FtdiChainCompleted { get; set; }
    public int MmapWritten { get; set; }
    public List<string> Updated { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public List<string> Flagged { get; set; } = new();
    public string Summary { get; set; } = "";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
}

public class DmaTestService
{
    private readonly LogService _log;
    private string? _dmaDir;
    private bool _extracted;

    // One-time confirmed-readable page pool per connection. The probe scan is
    // expensive (tens of thousands of LcReads); Full/Stress tests call
    // BuildMemoryMap twice (different minContiguous), so we probe ONCE per hVMM
    // and reuse the confirmed pool + already-installed allow-list for later calls.
    private IntPtr _poolVmm = IntPtr.Zero;
    private PhysMemPage[]? _confirmedPool;

    private static string? _dmaDirStatic;
    private static bool _resolverRegistered;
    private static readonly object _resolverLock = new();

    // Canonical mmap.txt location - written on generate, read on every connect + test.
    public static string MmapCachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Sylnar", "mmap.txt");

    public static bool HasCachedMmap() => File.Exists(MmapCachePath);

    public static DateTime? GetMmapCacheAge() =>
        File.Exists(MmapCachePath) ? File.GetLastWriteTime(MmapCachePath) : null;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    private static readonly string[] EmbeddedDlls =
    {
        "vmm.dll", "leechcore.dll", "leechcore_driver.dll",
        "FTD3XX.dll", "FTD3XXWU.dll", "dbghelp.dll", "symsrv.dll",
        "tinylz4.dll", "vcruntime140.dll"
    };

    private static readonly string[] PreloadOrder =
    {
        "vcruntime140.dll",
        "FTD3XXWU.dll",
        "FTD3XX.dll",
        "tinylz4.dll",
        "dbghelp.dll",
        "symsrv.dll",
        "leechcore_driver.dll",
        "leechcore.dll",
        "vmm.dll"
    };

    public DmaTestService(LogService log) => _log = log;

    /// <summary>
    /// Public entry point so other services (e.g. BarProbeService) can ensure
    /// the embedded leechcore/vmm/FTDI DLLs are extracted and the PInvoke
    /// resolver is registered before they call into VmmNative themselves.
    /// Idempotent - safe to call repeatedly.
    /// </summary>
    public void EnsureLibraries() => EnsureDllsExtracted();

    private void EnsureDllsExtracted()
    {
        if (_extracted && _dmaDir != null && Directory.Exists(_dmaDir))
            return;

        // Suppress the Microsoft Internet Symbol Store EULA dialog that symsrv.dll
        // (bundled with MemProcFS 5.17.7) shows on first symbol-server use. Two
        // belt-and-suspenders mechanisms - either alone is enough on most PCs,
        // both together is defensive against future symsrv changes:
        //
        //   1. _NT_SYMBOL_PATH=""  Tells symsrv "no symbol path at all" - no
        //      downloads attempted, no EULA prompt path reached. VMMDLL still
        //      initialises (LeechCore reads don't need kernel symbols); only
        //      PidGetFromName-style introspection is degraded, which we only
        //      use in Full Test and can fall back gracefully.
        //
        //   2. HKCU\Software\Microsoft\Symbol Server\EULA = 1  The documented
        //      symsrv registry flag that records "user accepted EULA". Set
        //      proactively in case some future symsrv variant ignores the
        //      env var.
        SuppressSymbolStoreEula();

        _dmaDir = OwnedTempDirs.Register(Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_dmaDir);

        var assembly = Assembly.GetExecutingAssembly();
        int count = 0;

        foreach (var fileName in EmbeddedDlls)
        {
            var destPath = Path.Combine(_dmaDir, fileName);
            ResourceCrypto.ExtractResource(assembly, fileName, destPath);
            count++;
        }

        _dmaDirStatic = _dmaDir;

        int loaded = 0;
        foreach (var dll in PreloadOrder)
        {
            var fullPath = Path.Combine(_dmaDir, dll);
            if (File.Exists(fullPath))
            {
                var h = LoadLibraryW(fullPath);
                if (h != IntPtr.Zero) loaded++;
            }
        }

        lock (_resolverLock)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(VmmNative).Assembly, ResolveDmaLibrary);
                _resolverRegistered = true;
            }
        }

        _extracted = true;
        _log.Info($"DMA libraries extracted ({count} files, {loaded} preloaded)");
    }

    private static IntPtr ResolveDmaLibrary(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_dmaDirStatic == null) return IntPtr.Zero;

        var path = Path.Combine(_dmaDirStatic, libraryName);
        if (File.Exists(path))
        {
            try { return NativeLibrary.Load(path); } catch { }
        }

        path = Path.Combine(_dmaDirStatic, libraryName + ".dll");
        if (File.Exists(path))
        {
            try { return NativeLibrary.Load(path); } catch { }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Suppress the Microsoft Internet Symbol Store EULA prompt that symsrv.dll
    /// pops up when VMMDLL_Initialize tries to fetch kernel PDBs. Runs before
    /// any DLL load so the env var is in place when symsrv first reads it.
    /// </summary>
    private void SuppressSymbolStoreEula()
    {
        // (1) Primary: empty _NT_SYMBOL_PATH - symsrv has no server to query,
        // so the EULA-prompt code path is never reached. Process scope is
        // enough; child processes (none here) and other dbghelp consumers in
        // the process inherit it.
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("_NT_ALT_SYMBOL_PATH", "", EnvironmentVariableTarget.Process);
        }
        catch { /* env var write essentially can't fail */ }

        // (2) Backup: HKCU\Software\Microsoft\Symbol Server\EULA = 1 (DWORD),
        // the documented value the dialog's "Yes" button writes. Idempotent.
        // HKCU = no admin required.
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .CreateSubKey(@"Software\Microsoft\Symbol Server");
            if (key != null)
            {
                var existing = key.GetValue("EULA");
                if (!(existing is int v && v == 1))
                    key.SetValue("EULA", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }
        catch { /* best effort */ }
    }

    public void Cleanup()
    {
        if (_dmaDir != null)
        {
            try { Directory.Delete(_dmaDir, true); } catch { }
            _dmaDir = null;
            _extracted = false;
        }
    }

    public Task<DmaTestResult> RunLatencyTestAsync(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunLatencyTest(duration, progress, ct), ct);

    public Task<DmaTestResult> RunThroughputTestAsync(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunThroughputTest(duration, progress, ct), ct);

    public Task<DmaTestResult> RunFullTestAsync(
        IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunFullTest(progress, ct), ct);

    public Task<DmaTestResult> RunStressTestAsync(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunStressTest(duration, progress, ct), ct);

    private DmaTestResult RunLatencyTest(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Latency" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 10, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Mapping", 20, "Getting memory map..."));
            var pages = BuildMemoryMap(hVMM, progress: progress);
            if (pages.Length == 0) { result.ErrorMessage = "No valid physical memory pages found"; return result; }

            _log.Info($"Running Latency Test for {duration.TotalSeconds:0}s ({pages.Length} pages)...");
            progress?.Report(Prog("Testing", 30, $"Running latency test ({duration.TotalSeconds:0}s)..."));

            RunLatencyLoop(hLC, pages, duration, ct, result);
            result.Success = true;
            result.OverallRating = result.LatencyRating;

            _log.Info($"Latency Test: {result.LatencyRps:N0} RPS, {result.LatencyAvgUs:N0} us avg, {result.LatencyMinUs:N0} us min, {result.LatencyMaxUs:N0} us max");
            _log.Info($"  Reads: {result.LatencyTotalReads:N0} total, {result.LatencyFailedReads:N0} failed - {result.LatencyRating}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Latency test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success ? $"Latency: {result.LatencyRps:N0} RPS - {result.LatencyRating}" : result.ErrorMessage));
        }
        return result;
    }

    private DmaTestResult RunThroughputTest(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Throughput" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 10, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Mapping", 20, "Getting memory map..."));
            const uint readSize = 0x1000000;
            var pages = BuildMemoryMap(hVMM, pageCount: 1000, minContiguous: readSize, progress: progress);
            if (pages.Length == 0) { result.ErrorMessage = "No contiguous 16MB memory regions found"; return result; }

            _log.Info($"Running Throughput Test for {duration.TotalSeconds:0}s ({pages.Length} regions)...");
            progress?.Report(Prog("Testing", 30, $"Running throughput test ({duration.TotalSeconds:0}s)..."));

            RunThroughputLoop(hLC, pages, duration, ct, result);
            result.Success = true;
            result.OverallRating = result.ThroughputRating;

            _log.Info($"Throughput Test: {result.ThroughputMBps:F2} MB/s");
            _log.Info($"  Reads: {result.ThroughputTotalReads:N0} total, {result.ThroughputFailedReads:N0} failed - {result.ThroughputRating}");
            if (result.ThroughputMBps < 45f)
                _log.Warn("Low throughput indicates USB 2.0 connection. Check port/cable.");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Throughput test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success ? $"Throughput: {result.ThroughputMBps:F1} MB/s - {result.ThroughputRating}" : result.ErrorMessage));
        }
        return result;
    }

    private DmaTestResult RunFullTest(
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Full" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 5, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Process", 10, "Looking up system processes..."));
            RunProcessLookup(hVMM, result);

            progress?.Report(Prog("Mapping", 20, "Getting memory map..."));
            var allPages = BuildMemoryMap(hVMM, progress: progress);
            if (allPages.Length == 0) { result.ErrorMessage = "No valid physical memory pages found"; return result; }

            _log.Info("Running Latency Test (5s)...");
            progress?.Report(Prog("Latency", 30, "Running latency test (5s)..."));
            RunLatencyLoop(hLC, allPages, TimeSpan.FromSeconds(5), ct, result);
            _log.Info($"Latency: {result.LatencyRps:N0} RPS, {result.LatencyAvgUs:N0} us avg - {result.LatencyRating}");

            progress?.Report(Prog("Throughput", 60, "Running throughput test (5s)..."));
            _log.Info("Running Throughput Test (5s)...");
            const uint tputReadSize = 0x1000000;
            var tputPages = BuildMemoryMap(hVMM, pageCount: 1000, minContiguous: tputReadSize);

            if (tputPages.Length > 0)
            {
                RunThroughputLoop(hLC, tputPages, TimeSpan.FromSeconds(5), ct, result);
                _log.Info($"Throughput: {result.ThroughputMBps:F2} MB/s - {result.ThroughputRating}");
                if (result.ThroughputMBps < 45f)
                    _log.Warn("Low throughput indicates USB 2.0 connection.");
            }
            else
            {
                _log.Warn("No contiguous 16MB regions - skipping throughput test");
                result.ThroughputRating = "SKIP";
            }

            result.Success = true;
            if (result.LatencyRating == "FAIL" || result.ThroughputRating == "FAIL")
                result.OverallRating = "FAIL";
            else
            {
                int lp = GetRatingPriority(result.LatencyRating);
                int tp = GetRatingPriority(result.ThroughputRating);
                result.OverallRating = lp <= tp ? result.LatencyRating : result.ThroughputRating;
            }
            _log.Info($"Full Test Overall: {result.OverallRating}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Full test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success ? $"Overall: {result.OverallRating}" : result.ErrorMessage));
        }
        return result;
    }

    private DmaTestResult RunStressTest(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Stress" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 5, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Mapping", 10, "Getting memory map..."));
            // Pages usable for small 4K reads (anywhere)
            var smallPages = BuildMemoryMap(hVMM, pageCount: 100000, minContiguous: 0x1000, progress: progress);
            // Pages usable for 16M reads (contiguous region required)
            var bigPages   = BuildMemoryMap(hVMM, pageCount: 1000, minContiguous: 0x1000000, progress: progress);
            if (smallPages.Length == 0)
            {
                result.ErrorMessage = "No valid physical memory pages found";
                return result;
            }

            _log.Info($"Running Stress Test for {duration.TotalMinutes:F1} min ({smallPages.Length} small pages, {bigPages.Length} 16MB regions)...");
            progress?.Report(Prog("Stress", 15, $"Stress soak running ({duration.TotalMinutes:F1} min)..."));

            RunStressLoop(hLC, smallPages, bigPages, duration, progress, ct, result);
            result.Success = true;
            result.OverallRating = result.StressRating;

            _log.Info($"Stress Test: {result.StressTotalReads:N0} reads in {result.StressDuration.TotalSeconds:F0}s, " +
                      $"{result.StressFailedReads:N0} failed ({result.StressFailPct:F3}%), " +
                      $"{result.StressRecoveredTransient:N0} transient recovered, " +
                      $"max consecutive failures = {result.StressMaxConsecFails} - {result.StressRating}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Stress test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success
                    ? $"Stress: {result.StressFailedReads:N0} fails / {result.StressTotalReads:N0} reads - {result.StressRating}"
                    : result.ErrorMessage));
        }
        return result;
    }

    private static void RunStressLoop(
        IntPtr hLC, PhysMemPage[] smallPages, PhysMemPage[] bigPages,
        TimeSpan duration, IProgress<FlashProgress>? progress,
        CancellationToken ct, DmaTestResult result)
    {
        const uint smallSize = 0x1000;       // 4 KB
        const uint bigSize   = 0x1000000;    // 16 MB
        // Ratio: 256 small reads per 1 big read. Same workload mix as lone-DMA
        // stability soak: lots of metadata-sized reads with periodic bulk reads
        // to keep both code paths exercised.
        const int  bigEvery  = 256;

        IntPtr pbSmall = Marshal.AllocHGlobal((int)smallSize);
        IntPtr pbBig   = bigPages.Length > 0 ? Marshal.AllocHGlobal((int)bigSize) : IntPtr.Zero;
        try
        {
            long total = 0, fails = 0, recoveredTransient = 0;
            long maxConsec = 0, curConsec = 0;
            var sw = Stopwatch.StartNew();
            var lastProgress = Stopwatch.StartNew();
            int iter = 0;

            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                bool ok;
                ulong pa;
                uint size;
                if (pbBig != IntPtr.Zero && (iter % bigEvery) == 0 && bigPages.Length > 0)
                {
                    pa = bigPages[Random.Shared.Next(bigPages.Length)].PageBase;
                    size = bigSize;
                    ok = VmmNative.LcRead(hLC, pa, size, pbBig);
                }
                else
                {
                    pa = smallPages[Random.Shared.Next(smallPages.Length)].PageBase;
                    size = smallSize;
                    ok = VmmNative.LcRead(hLC, pa, size, pbSmall);
                }

                if (!ok)
                {
                    // Retry the same address once before counting a persistent fail.
                    IntPtr buf = size == bigSize ? pbBig : pbSmall;
                    bool retry = VmmNative.LcRead(hLC, pa, size, buf);
                    if (retry) { recoveredTransient++; ok = true; }
                }

                if (ok)
                {
                    curConsec = 0;
                }
                else
                {
                    fails++;
                    curConsec++;
                    if (curConsec > maxConsec) maxConsec = curConsec;
                }
                total++;   // every logical read counted exactly once
                iter++;

                // Report progress every 500ms with running stats
                if (lastProgress.ElapsedMilliseconds >= 500)
                {
                    int pct = (int)Math.Min(99, 15 + 84.0 * sw.Elapsed.TotalSeconds / duration.TotalSeconds);
                    float pctFail = total == 0 ? 0f : (fails / (float)total) * 100f;
                    progress?.Report(Prog(
                        "Stress",
                        pct,
                        $"{sw.Elapsed.TotalSeconds:F0}s / {duration.TotalSeconds:F0}s - " +
                        $"{total:N0} reads, {fails:N0} fail ({pctFail:F3}%), max-streak {maxConsec}"));
                    lastProgress.Restart();
                }
            }

            float failPct = total == 0 ? 0f : (fails / (float)total) * 100f;
            result.StressDuration      = sw.Elapsed;
            result.StressTotalReads    = total;
            result.StressFailedReads   = fails;
            result.StressRecoveredTransient = recoveredTransient;
            result.StressMaxConsecFails = maxConsec;
            result.StressFailPct       = failPct;

            // Honest rating: with the confirmed pool + allow-list, the soak should
            // see ZERO persistent failures on a healthy link. Recovered transients
            // do NOT fail the test (they were retried successfully). Any persistent
            // failure under sustained load = FAIL.
            if (fails == 0)
                result.StressRating = sw.Elapsed.TotalMinutes >= 5 ? "PERFECT"
                                    : sw.Elapsed.TotalMinutes >= 2 ? "EXCELLENT"
                                    :                                "GOOD";
            else
                result.StressRating = "FAIL";
        }
        finally
        {
            if (pbSmall != IntPtr.Zero) Marshal.FreeHGlobal(pbSmall);
            if (pbBig   != IntPtr.Zero) Marshal.FreeHGlobal(pbBig);
        }
    }

    private static void RunLatencyLoop(
        IntPtr hLC, PhysMemPage[] pages, TimeSpan duration,
        CancellationToken ct, DmaTestResult result)
    {
        const uint readSize = 0x1000;
        IntPtr pb = Marshal.AllocHGlobal((int)readSize);
        try
        {
            long totalCount = 0, failedCount = 0, recoveredTransient = 0;
            TimeSpan minRead = TimeSpan.MaxValue, maxRead = TimeSpan.MinValue;
            var readSw = new Stopwatch();
            var testSw = Stopwatch.StartNew();

            while (testSw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                ulong pa = pages[Random.Shared.Next(pages.Length)].PageBase;
                readSw.Restart();
                bool ok = VmmNative.LcRead(hLC, pa, readSize, pb);
                var elapsed = readSw.Elapsed;

                if (!ok)
                {
                    // Retry the same address once before counting a persistent fail.
                    bool retry = VmmNative.LcRead(hLC, pa, readSize, pb);
                    if (retry) { recoveredTransient++; ok = true; }
                }

                if (ok)
                {
                    if (elapsed < minRead) minRead = elapsed;
                    if (elapsed > maxRead) maxRead = elapsed;
                }
                else
                {
                    failedCount++;
                }
                totalCount++;   // every logical read counted exactly once
            }

            PopulateLatencyResult(result, totalCount, failedCount, recoveredTransient, testSw.Elapsed, minRead, maxRead);
        }
        finally { Marshal.FreeHGlobal(pb); }
    }

    private static void RunThroughputLoop(
        IntPtr hLC, PhysMemPage[] pages, TimeSpan duration,
        CancellationToken ct, DmaTestResult result)
    {
        const uint readSize = 0x1000000;
        IntPtr pb = Marshal.AllocHGlobal((int)readSize);
        try
        {
            long totalCount = 0, failedCount = 0, recoveredTransient = 0;
            var testSw = Stopwatch.StartNew();

            while (testSw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                ulong pa = pages[Random.Shared.Next(pages.Length)].PageBase;
                bool ok = VmmNative.LcRead(hLC, pa, readSize, pb);
                if (!ok)
                {
                    // Retry the same address once before counting a persistent fail.
                    bool retry = VmmNative.LcRead(hLC, pa, readSize, pb);
                    if (retry) { recoveredTransient++; ok = true; }
                }

                if (!ok) failedCount++;
                totalCount++;   // every logical read counted exactly once
            }

            PopulateThroughputResult(result, totalCount, failedCount, recoveredTransient, testSw.Elapsed);
        }
        finally { Marshal.FreeHGlobal(pb); }
    }

    private IntPtr ConnectDma(bool useCachedMmap = true)
    {
        EnsureDllsExtracted();

        // New connection → drop any confirmed-page pool from a previous connection
        // so we never reuse a pool/allow-list built against a stale hVMM.
        _confirmedPool = null;
        _poolVmm = IntPtr.Zero;

        _log.Info("Connecting to FPGA DMA device...");
        var argList = new List<string> { "-device", "fpga", "-norefresh", "-waitinitialize" };
        if (useCachedMmap && HasCachedMmap())
        {
            argList.Add("-memmap");
            argList.Add(MmapCachePath);
            _log.Info($"Using cached mmap ({Path.GetFileName(MmapCachePath)})");
        }
        else if (useCachedMmap)
        {
            _log.Warn("No mmap.txt cached - VBS-fenced pages will appear as read failures on IOMMU systems. Use 'Generate mmap' first.");
        }
        var args = argList.ToArray();
        var hVMM = VmmNative.VMMDLL_Initialize(args.Length, args);
        if (hVMM == IntPtr.Zero)
            throw new InvalidOperationException(
                "Could not open DMA device. Check: " +
                "1) Card in PCIe slot with power, " +
                "2) Firmware loaded (flash + cold reboot), " +
                "3) DATA USB 3.0 cable connected, " +
                "4) FTDI driver installed.");
        _log.Info("DMA device connected");
        return hVMM;
    }

    private IntPtr GetLeechCoreHandle(IntPtr hVMM)
    {
        if (!VmmNative.VMMDLL_ConfigGet(hVMM, VmmNative.OPT_CORE_LEECHCORE_HANDLE, out ulong hLC) || hLC == 0)
            throw new InvalidOperationException("Failed to get LeechCore handle from VMM");
        return (IntPtr)(long)hLC;
    }

    // Pool sizing for the confirmed-readable page builder.
    //   PoolTarget        - stop probing once this many readable pages are pooled
    //                       AND enough of them qualify for a 16MB-contiguous run.
    //   RunQualifyBytes   - a page "qualifies for throughput" if its coalesced run
    //                       has at least this many bytes remaining from the page.
    //   RunQualifyTarget  - how many such pages must exist before we may stop.
    private const int   PoolTarget       = 50_000;
    private const ulong RunQualifyBytes  = 0x1000000;   // 16 MB
    private const int   RunQualifyTarget = 1200;
    private const ulong PageSize         = 0x1000;       // 4 KB

    /// <summary>
    /// Builds a page pool consisting ONLY of physical 4 KB pages that have been
    /// PROBE-CONFIRMED readable over the live DMA link, then installs that pool as
    /// a leechcore allow-list (LC_CMD_MEMMAP_SET_STRUCT) so no subsequent read can
    /// ever touch a VBS/IOMMU-fenced page. This mirrors the Lone tool's winning
    /// mechanism and replaces the old "explode every E820 page + stale-mmap filter"
    /// approach which let fenced pages into the pool and forced a failure-skip.
    ///
    /// Candidate universe is STRICTLY the VMMDLL_Map_GetPhysMem ranges - we never
    /// probe an address outside them (probing outside RAM wedges the AMD link).
    /// Confirmed pages are coalesced into contiguous runs; RemainingBytes is the
    /// HONEST distance to the end of the run (a fenced hole ends the run), so the
    /// minContiguous filter for throughput reflects real contiguity.
    ///
    /// The signature is unchanged so all callers compile as-is: minContiguous
    /// selects the run-length needed (0x1000 = any page, 0x1000000 = inside a
    /// 16MB run), pageCount caps the returned sample.
    /// </summary>
    // If a probe-verified mmap.txt is cached, build the confirmed pool from it
    // directly (instant - no DMA probing needed). Falls back to ProbeConfirmedPool
    // only when no cache exists.
    private PhysMemPage[]? TryBuildPoolFromCachedMmap(IntPtr hVMM)
    {
        if (!HasCachedMmap()) return null;
        try
        {
            var lines = File.ReadAllLines(MmapCachePath);
            var ranges = new List<(ulong Pa, ulong Cb)>();
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                // Format: "NNNN AAAAAAAAAAAAAAAA - BBBBBBBBBBBBBBBB"
                var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                if (!ulong.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out ulong start)) continue;
                if (!ulong.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out ulong end)) continue;
                if (end <= start) continue;
                ranges.Add((start, end - start + 1));
            }
            if (ranges.Count == 0) return null;

            var hLC = GetLeechCoreHandle(hVMM);
            InstallAllowList(hLC, ranges);

            var confirmed = new List<PhysMemPage>();
            foreach (var (pa, cb) in ranges)
            {
                ulong regionEnd = pa + (cb & ~(PageSize - 1));
                for (ulong p = pa; p < regionEnd; p += PageSize)
                    confirmed.Add(new PhysMemPage { PageBase = p, RemainingBytes = regionEnd - p });
            }
            if (confirmed.Count == 0) return null;

            _log.Info($"Pool from cached mmap: {confirmed.Count:N0} pages, {ranges.Count} runs");
            var pool = confirmed.ToArray();
            Random.Shared.Shuffle(pool);
            return pool;
        }
        catch (Exception ex)
        {
            _log.Warn($"Cached mmap parse failed: {ex.Message} - falling back to live probe.");
            return null;
        }
    }

    private PhysMemPage[] BuildMemoryMap(
        IntPtr hVMM, int pageCount = 100000, uint minContiguous = 0x1000,
        IProgress<FlashProgress>? progress = null)
    {
        // Probe ONCE per connection; reuse the confirmed pool (and the already-
        // installed allow-list) for every subsequent call with a different filter.
        if (_confirmedPool == null || _poolVmm != hVMM)
        {
            _confirmedPool = TryBuildPoolFromCachedMmap(hVMM) ?? ProbeConfirmedPool(hVMM, progress);
            _poolVmm = hVMM;
        }

        var filtered = _confirmedPool
            .Where(p => p.RemainingBytes >= minContiguous)
            .Take(pageCount)
            .ToArray();
        return filtered;
    }

    // The expensive part: walk the OS phys-mem map, probe every candidate 4 KB
    // page for readability (retry-once), coalesce confirmed pages into contiguous
    // runs, install the leechcore allow-list from those runs, and return a
    // SHUFFLED array of confirmed pages with honest per-page RemainingBytes.
    private PhysMemPage[] ProbeConfirmedPool(IntPtr hVMM, IProgress<FlashProgress>? progress = null)
    {
        var hLC = GetLeechCoreHandle(hVMM);

        if (!VmmNative.VMMDLL_Map_GetPhysMem(hVMM, out IntPtr pMap) || pMap == IntPtr.Zero)
            throw new InvalidOperationException("Failed to retrieve physical memory map");

        // (pa, cb) candidate regions taken verbatim from the OS phys-mem map.
        var regions = new List<(ulong Pa, ulong Cb)>();
        ulong candidatePages = 0;
        try
        {
            uint cMap = (uint)Marshal.ReadInt32(pMap, 24);
            if (cMap == 0)
                throw new InvalidOperationException("Physical memory map is empty");

            const int headerSize = 32;
            const int entrySize  = 16;
            for (int i = 0; i < (int)cMap; i++)
            {
                IntPtr pEntry = pMap + headerSize + i * entrySize;
                ulong pa = (ulong)Marshal.ReadInt64(pEntry, 0);
                ulong cb = (ulong)Marshal.ReadInt64(pEntry, 8);
                if (cb < PageSize) continue;
                regions.Add((pa, cb));
                candidatePages += cb / PageSize;
            }
        }
        finally { VmmNative.VMMDLL_MemFree(pMap); }

        if (regions.Count == 0)
            throw new InvalidOperationException("Physical memory map is empty");

        // ── Probe every candidate page for readability, coalescing confirmed
        //    pages into contiguous runs as we go. A fenced page (fails twice)
        //    ends the current run.
        int initialCap = candidatePages > (ulong)(PoolTarget * 2) ? PoolTarget * 2 : (int)candidatePages;
        var confirmed = new List<PhysMemPage>(initialCap);
        var runs      = new List<(ulong Pa, ulong Cb)>();     // coalesced allow-list

        IntPtr pb = Marshal.AllocHGlobal((int)PageSize);
        ulong probed = 0, fenced = 0, qualifying = 0;
        try
        {
            foreach (var (regionPa, regionCb) in regions)
            {
                // Inclusive last page base of this region.
                ulong regionEnd = regionPa + (regionCb & ~(PageSize - 1));   // first page base past the region

                // Track the start index into `confirmed` for the current run so we
                // can back-fill RemainingBytes once the run terminates.
                ulong? runStart   = null;   // page base of current run start
                int    runFirstIx = -1;     // index in `confirmed` of run's first page

                for (ulong p = regionPa; p < regionEnd; p += PageSize)
                {
                    // Hard probe cap - always terminates even on a huge machine.
                    if (probed >= candidatePages) break;

                    bool ok = VmmNative.LcRead(hLC, p, (uint)PageSize, pb);
                    if (!ok) ok = VmmNative.LcRead(hLC, p, (uint)PageSize, pb); // retry once
                    probed++;
                    if (probed % 5000 == 0)
                        progress?.Report(Prog("Mapping", 20,
                            $"Probing physical memory... {probed:N0}/{candidatePages:N0} pages, {confirmed.Count:N0} confirmed"));

                    if (ok)
                    {
                        if (runStart == null) { runStart = p; runFirstIx = confirmed.Count; }
                        confirmed.Add(new PhysMemPage { PageBase = p, RemainingBytes = 0 });
                    }
                    else
                    {
                        fenced++;
                        CloseRun(confirmed, runs, ref runStart, ref runFirstIx, p, ref qualifying);
                    }
                }

                // Region boundary also closes any open run (next region may not be contiguous).
                CloseRun(confirmed, runs, ref runStart, ref runFirstIx, regionEnd, ref qualifying);

                // Stop once we have a big enough pool AND enough throughput-qualifying pages.
                if (confirmed.Count >= PoolTarget && qualifying >= (ulong)RunQualifyTarget)
                    break;
            }
        }
        finally { Marshal.FreeHGlobal(pb); }

        if (confirmed.Count == 0)
            throw new InvalidOperationException(
                "No physical memory pages were confirmed readable over the DMA link.");

        // ── Install the leechcore allow-list from the coalesced runs.
        InstallAllowList(hLC, runs);

        // ── Shuffle the confirmed pool so per-page random sampling in the test
        //    loops draws uniformly across physical memory, not lowest-address-first.
        var pool = confirmed.ToArray();
        Random.Shared.Shuffle(pool);

        _log.Info($"{confirmed.Count:N0} readable pages pooled, allow-list {runs.Count} runs " +
                  $"({probed:N0} probed, {fenced:N0} fenced, {qualifying:N0} qualify for 16MB)");
        return pool;
    }

    // Closes the current contiguous run (if any), back-filling RemainingBytes for
    // every page in the run as (runEnd - pageBase), and records the run in the
    // allow-list. `runEnd` is the first page base AFTER the run (a fenced page or a
    // region boundary).
    private static void CloseRun(
        List<PhysMemPage> confirmed, List<(ulong Pa, ulong Cb)> runs,
        ref ulong? runStart, ref int runFirstIx, ulong runEnd, ref ulong qualifying)
    {
        if (runStart == null) return;

        ulong start = runStart.Value;
        ulong cb    = runEnd - start;
        runs.Add((start, cb));

        for (int ix = runFirstIx; ix < confirmed.Count; ix++)
        {
            ulong rem = runEnd - confirmed[ix].PageBase;
            confirmed[ix] = new PhysMemPage { PageBase = confirmed[ix].PageBase, RemainingBytes = rem };
            if (rem >= RunQualifyBytes) qualifying++;
        }

        runStart = null;
        runFirstIx = -1;
    }

    // Marshals the coalesced runs into an LC_MEMMAP_ENTRY[] (three little-endian
    // QWORDs per entry: pa, cb, paRemap=pa - i.e. identity remap) and pushes it to
    // leechcore as an allow-list. Best-effort: the confirmed pool alone already
    // prevents single-page failures, so a failure here only logs a warning.
    private void InstallAllowList(IntPtr hLC, List<(ulong Pa, ulong Cb)> runs)
    {
        if (runs.Count == 0) return;
        const int entrySize = 24; // 3 * sizeof(ulong)
        int cb = runs.Count * entrySize;
        IntPtr pEntries = Marshal.AllocHGlobal(cb);
        try
        {
            for (int i = 0; i < runs.Count; i++)
            {
                long baseOff = (long)i * entrySize;
                Marshal.WriteInt64(pEntries, (int)baseOff + 0,  (long)runs[i].Pa);   // pa
                Marshal.WriteInt64(pEntries, (int)baseOff + 8,  (long)runs[i].Cb);   // cb
                Marshal.WriteInt64(pEntries, (int)baseOff + 16, (long)runs[i].Pa);   // paRemap = pa (identity)
            }

            bool ok = VmmNative.LcCommand(
                hLC, VmmNative.LC_CMD_MEMMAP_SET_STRUCT, (uint)cb, pEntries,
                out IntPtr ppbOut, out _);
            if (ppbOut != IntPtr.Zero) VmmNative.LcMemFree(ppbOut);

            if (!ok)
                _log.Warn("Could not install leechcore allow-list (MEMMAP_SET_STRUCT returned false); " +
                          "confirmed-readable pool still prevents fenced-page reads.");
            else
                _log.Info($"Leechcore allow-list installed: {runs.Count} runs.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not install leechcore allow-list: {SanitizeError(ex.Message)}; " +
                      "continuing with confirmed-readable pool only.");
        }
        finally { Marshal.FreeHGlobal(pEntries); }
    }

    private void RunProcessLookup(IntPtr hVMM, DmaTestResult result)
    {
        try
        {
            const string target = "smss.exe";
            if (VmmNative.VMMDLL_PidGetFromName(hVMM, target, out uint pid))
            {
                result.ProcessFound = true;
                result.ProcessInfo = $"{target} @ PID {pid}";
                _log.Info($"Process found: {target} (PID {pid})");
            }
            else
            {
                result.ProcessInfo = "smss.exe not found";
                _log.Warn("Could not locate smss.exe");
            }
        }
        catch (Exception ex)
        {
            result.ProcessInfo = "Process lookup failed";
            _log.Warn($"Process lookup error: {SanitizeError(ex.Message)}");
        }
    }

    private static void PopulateLatencyResult(
        DmaTestResult result, long totalCount, long failedCount, long recoveredTransient,
        TimeSpan testDuration, TimeSpan minRead, TimeSpan maxRead)
    {
        long ok = totalCount - failedCount;
        double avgSec = ok == 0 ? 0 : testDuration.TotalSeconds / ok;
        int rps   = avgSec <= 0 ? 0 : (int)Math.Round(1.0 / avgSec);
        int avgUs = ok == 0 ? 0 : (int)Math.Round(testDuration.TotalMicroseconds / ok);
        int minUs = minRead == TimeSpan.MaxValue ? 0 : (int)Math.Round(minRead.TotalMicroseconds);
        int maxUs = maxRead == TimeSpan.MinValue ? 0 : (int)Math.Round(maxRead.TotalMicroseconds);
        double failPct = totalCount == 0 ? 0 : (failedCount / (double)totalCount) * 100.0;

        result.LatencyRps        = rps;
        result.LatencyMinUs      = minUs;
        result.LatencyMaxUs      = maxUs;
        result.LatencyAvgUs      = avgUs;
        result.LatencyTotalReads  = totalCount;
        result.LatencyFailedReads = failedCount;
        result.LatencyRecoveredTransient = recoveredTransient;

        // Honest grade: persistent fails >= 1% => FAIL; otherwise graded purely by
        // speed. Recovered transients are not failures and do not affect the grade.
        result.LatencyRating = failPct >= 1.0 ? "FAIL" : rps switch
        {
            >= 18000 => "PERFECT",
            >= 6000  => "EXCELLENT",
            >= 4000  => "GOOD",
            >= 3000  => "ACCEPTABLE",
            _        => "FAIL"
        };
    }

    private static void PopulateThroughputResult(
        DmaTestResult result, long totalCount, long failedCount, long recoveredTransient, TimeSpan testDuration)
    {
        long ok = totalCount - failedCount;
        ulong bytesRead = (ulong)ok * 0x1000000;
        float mbps = testDuration.TotalSeconds <= 0
            ? 0 : (float)(bytesRead / 1024.0 / 1024.0 / testDuration.TotalSeconds);
        float failPct = totalCount == 0 ? 0f : (failedCount / (float)totalCount) * 100f;

        result.ThroughputMBps         = mbps;
        result.ThroughputTotalReads   = totalCount;
        result.ThroughputFailedReads  = failedCount;
        result.ThroughputRecoveredTransient = recoveredTransient;

        // Honest zero-tolerance: ANY persistent fail => FAIL (with the confirmed
        // pool + allow-list it is genuinely zero on a healthy link). Recovered
        // transients are not failures. Otherwise graded by throughput.
        result.ThroughputRating = failPct > 0 ? "FAIL" : mbps switch
        {
            >= 600f => "PERFECT",
            >= 200f => "EXCELLENT",
            >= 150f => "GOOD",
            >= 100f => "ACCEPTABLE",
            _       => "FAIL"
        };
    }

    private static int GetRatingPriority(string r) => r switch
    {
        "PERFECT" => 5, "EXCELLENT" => 4, "GOOD" => 3,
        "ACCEPTABLE" => 2, "FAIL" => 1, _ => 0
    };

    private static string SanitizeError(string msg)
    {
        var s = Regex.Replace(msg, @"[A-Za-z]:\\[^\s""']+", "[path]");
        return Regex.Replace(s, @"/tmp/[^\s""']+", "[path]");
    }

    private static FlashProgress Prog(string stage, int pct, string msg) =>
        new() { Stage = stage, Percentage = pct, Message = msg };

    /// <summary>
    /// Connects to the FPGA, reads the physical memory map of the connected PC,
    /// and returns the mmap content as a string ready to save.
    /// The caller is responsible for writing the file wherever needed.
    /// </summary>
    public Task<MmapResult> GenerateMmapAsync(
        IProgress<FlashProgress>? progress,
        CancellationToken ct) =>
        Task.Run(() => GenerateMmap(progress, ct), ct);

    private MmapResult GenerateMmap(
        IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        var result = new MmapResult();
        IntPtr hVMM = IntPtr.Zero;
        IntPtr pMap = IntPtr.Zero;

        try
        {
            // Connect WITHOUT cached mmap so we can probe the real physical layout.
            progress?.Report(Prog("Connecting", 5, "Connecting to DMA device..."));
            hVMM = ConnectDma(useCachedMmap: false);
            var hLC = GetLeechCoreHandle(hVMM);

            // Get E820 physical map from the OS - this includes VBS-fenced pages.
            progress?.Report(Prog("Reading", 10, "Reading physical memory layout..."));
            if (!VmmNative.VMMDLL_Map_GetPhysMem(hVMM, out pMap) || pMap == IntPtr.Zero)
                throw new InvalidOperationException("Failed to retrieve physical memory map.");

            const int headerSize = 32;
            const int entrySize  = 16;
            uint cMap = (uint)Marshal.ReadInt32(pMap, 24);
            if (cMap == 0) throw new InvalidOperationException("Physical memory map is empty.");

            var e820 = new List<(ulong Start, ulong End)>();
            for (int i = 0; i < (int)cMap; i++)
            {
                IntPtr pEntry = pMap + headerSize + i * entrySize;
                ulong pa = (ulong)Marshal.ReadInt64(pEntry, 0);
                ulong cb = (ulong)Marshal.ReadInt64(pEntry, 8);
                if (cb > 0) e820.Add((pa, pa + cb - 1));
            }
            VmmNative.VMMDLL_MemFree(pMap);
            pMap = IntPtr.Zero;

            // Probe at 2MB granularity to discover which blocks are accessible.
            // VBS/HVCI reserves large contiguous pages; a 2MB block probe correctly
            // identifies them since the secure kernel uses 2MB large-page mappings.
            const ulong BLOCK = 0x200000UL; // 2MB
            long totalBlocks = e820.Sum(r => (long)((r.End - r.Start + BLOCK) / BLOCK));
            long probed = 0;

            var verified = new List<(ulong Start, ulong End)>();
            var probeBuf = Marshal.AllocHGlobal(0x1000);
            try
            {
                foreach (var (regionStart, regionEnd) in e820)
                {
                    if (ct.IsCancellationRequested) break;

                    // Align block iteration to 2MB boundary
                    ulong blockBase = regionStart & ~(BLOCK - 1);
                    ulong? runStart = null;

                    for (; blockBase <= regionEnd; blockBase += BLOCK)
                    {
                        if (ct.IsCancellationRequested) break;

                        // Probe the first accessible page in this 2MB block
                        ulong probeAddr = Math.Max(blockBase, regionStart);
                        bool ok = VmmNative.LcRead(hLC, probeAddr, 0x1000, probeBuf);
                        probed++;

                        int pct = 15 + (int)(75.0 * probed / Math.Max(totalBlocks, 1));
                        if (probed % 64 == 0)
                            progress?.Report(Prog("Probing", pct,
                                $"Probing physical memory... {probed}/{totalBlocks} blocks, {verified.Count} regions found"));

                        if (ok)
                        {
                            if (runStart == null) runStart = probeAddr;
                        }
                        else
                        {
                            if (runStart != null)
                            {
                                verified.Add((runStart.Value, Math.Min(blockBase - 1, regionEnd)));
                                runStart = null;
                            }
                        }
                    }

                    if (runStart != null)
                        verified.Add((runStart.Value, regionEnd));
                }
            }
            finally { Marshal.FreeHGlobal(probeBuf); }

            if (verified.Count == 0)
                throw new InvalidOperationException("No accessible physical memory found after probing.");

            // Build mmap.txt content
            var lines = new List<string>
            {
                "# Physical memory map generated by Sylnar (probe-verified).",
                "# VBS/HVCI-protected pages have been excluded by 2MB-block probing.",
                "# Save as mmap.txt in your radar/DMA tool folder.",
                "# Re-generate if you change RAM, BIOS, or move to a different PC.",
                ""
            };

            for (int i = 0; i < verified.Count; i++)
                lines.Add($"{i:D4} {verified[i].Start:X016} - {verified[i].End:X016}");

            result.RegionCount = verified.Count;
            result.TotalRamGb  = verified.Sum(r => (double)(r.End - r.Start + 1)) / (1024.0 * 1024 * 1024);
            result.Content     = string.Join("\n", lines);
            result.Success     = true;

            // Auto-save to canonical cache
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MmapCachePath)!);
                File.WriteAllText(MmapCachePath, result.Content);
                _log.Info($"Probe-verified mmap saved: {result.RegionCount} regions, {result.TotalRamGb:F1} GB");
            }
            catch (Exception ex) { _log.Warn($"Could not auto-save mmap cache: {ex.Message}"); }

            progress?.Report(Prog("Ready", 100,
                $"Ready - {result.RegionCount} regions, {result.TotalRamGb:F1} GB (VBS-excluded). Choose where to save."));
            _log.Info($"mmap generation complete: {probed} blocks probed, {result.RegionCount} verified regions.");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"mmap generation failed: {result.ErrorMessage}");
        }
        finally
        {
            if (pMap != IntPtr.Zero) VmmNative.VMMDLL_MemFree(pMap);
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            if (!result.Success)
                progress?.Report(Prog("Failed", 0, result.ErrorMessage));
        }
        return result;
    }

    // ───────────────────────── Deploy to DMA Tools ─────────────────────────
    //
    // Scans sane, fast, safe roots on PC2 for radar/DMA tool folders (every
    // folder that contains a leechcore.dll), and delivers the custom patched
    // leechcore.dll + the full FTDI chain + the freshly generated mmap.txt so
    // the end user does not have to manually patch anything. SAFE + reversible:
    //   - .orig backup taken before replacing leechcore.dll or mmap.txt.
    //   - never replaces a leechcore that already matches the currently-bundled build.
    //   - never replaces a leechcore from a different ABI minor (e.g. 2.19).
    //   - never clobbers an existing FTDI dll - only fills gaps.
    //   - never throws out of the per-folder loop; overall try/catch -> Error.

    // The bundled leechcore is identified by its byte length AND/OR its FileVersion.
    // v1.1.25: both are AUTO-DERIVED from the freshly-extracted embedded copy at
    // DeployToTools time (see the assignments right after ResourceCrypto extract),
    // so bumping the bundled leechcore never requires editing these constants
    // again. Prevents the class of bug where a stale value here silently defeats
    // the whole point of a leechcore bump (v1.1.24 -> v1.1.25 hit exactly this).
    // ABI minor stays as a compile-time compat statement - a new minor is a
    // manual decision, not automatic.
    private static long CustomLeechcoreSize;
    private static string CustomLeechcoreVersion = "";
    private const string CustomLeechcoreAbiMinor = "2.22";

    // Depth cap is measured from a DRIVE ROOT (e.g. C:\), not a profile sub-folder,
    // so it must reach typical install depths: C:\Users\<u>\Desktop\bin\DmaTool\pcileech
    // is 6 below C:\; allow a couple more levels of headroom.
    private const int DeployDepthCap = 9;

    // Heartbeat cadence: report a "scanned N folders" tick every this-many directories
    // so a whole-drive walk shows live motion instead of a frozen status line.
    private const int ScanHeartbeatEvery = 400;

    // A folder is a DMA tool folder iff it contains leechcore.dll. This is the only
    // truly DMA-exclusive filename (pcileech/MemProcFS DMA library) - no legitimate
    // non-DMA software ships a file by that name, so it is false-positive-proof. We
    // deliberately do NOT anchor on vmm.dll: that name is shared by VMware/SCVMM and
    // other hypervisor components, so a vmm.dll anchor would flag foreign folders and
    // (since they hold no leechcore.dll) cause DeployToFolder to FABRICATE a pcileech
    // leechcore.dll + FTDI chain there - silent pollution of unrelated directories.
    // Anchoring on leechcore.dll means we only ever OVERWRITE an existing DMA library
    // (always with a .orig backup), never create one where none belongs. A MemProcFS
    // folder that extracts leechcore.dll to %TEMP% (no on-disk copy) is intentionally
    // skipped here and surfaced by the transient-extractor flag in DeployToFolder.
    private static readonly string[] DmaAnchorFiles = { "leechcore.dll" };

    private static readonly string[] DeployFtdiChain =
    {
        "FTD3XX.dll", "FTD3XXWU.dll", "leechcore_driver.dll"
    };

    // Mutable counters threaded through the recursive scan for live progress.
    private sealed class ScanState
    {
        public int DirsScanned;
        public int ToolsFound;
        public string CurrentRoot = "";
    }

    public Task<DeployResult> DeployToToolsAsync(
        string mmapContent, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => DeployToTools(mmapContent, progress, ct), ct);

    private DeployResult DeployToTools(
        string mmapContent, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DeployResult();
        string? staging = null;
        try
        {
            progress?.Report(Prog("Extracting", 5, "Extracting custom DMA libraries..."));

            // 1. Extract the custom DLLs to a temp staging dir via ResourceCrypto.
            //    We only deploy leechcore + the FTDI chain - vmm.dll is NOT required.
            staging = Path.Combine(Path.GetTempPath(), $"nf_deploy_{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);

            var assembly = Assembly.GetExecutingAssembly();
            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dll in new[] { "leechcore.dll", "FTD3XX.dll", "FTD3XXWU.dll", "leechcore_driver.dll" })
            {
                var dest = Path.Combine(staging, dll);
                if (ResourceCrypto.ExtractResource(assembly, dll, dest) && File.Exists(dest))
                    staged[dll] = dest;
            }

            if (!staged.TryGetValue("leechcore.dll", out var stagedLeechcore))
            {
                result.Error = "Could not extract the bundled custom leechcore.dll.";
                _log.Error($"Deploy aborted: {result.Error}");
                progress?.Report(Prog("Failed", 0, result.Error));
                return result;
            }

            var customLeechcoreBytes = File.ReadAllBytes(stagedLeechcore);

            // v1.1.25: auto-derive the size + version identity from the freshly-
            // extracted embedded copy. This guarantees the "already current"
            // fast-path in DeployToFolder compares against what we are actually
            // shipping right now, not against a hardcoded value that goes stale
            // whenever the bundled leechcore is bumped.
            CustomLeechcoreSize = customLeechcoreBytes.LongLength;
            CustomLeechcoreVersion = TryGetFileVersion(stagedLeechcore) ?? "";
            _log.Info($"Deploy: bundled leechcore identity resolved (size={CustomLeechcoreSize} bytes, version={(string.IsNullOrEmpty(CustomLeechcoreVersion) ? "unknown" : CustomLeechcoreVersion)}).");

            // 2. Determine scan roots: every ready FIXED drive root. This covers tools
            //    wherever they live (C:\pcileech, D:\dma\..., a non-default user profile),
            //    not just the current user's Desktop/Downloads/AppData. ShouldSkipDir
            //    prunes Windows/ProgramFiles/ProgramData/Temp/caches so the walk stays fast.
            progress?.Report(Prog("Scanning", 15, "Scanning fixed drives for DMA tool folders..."));
            var roots = new List<string>();
            void AddRoot(string? p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p!); }
            try
            {
                foreach (var drv in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drv.DriveType == DriveType.Fixed && drv.IsReady)
                            AddRoot(drv.RootDirectory.FullName);
                    }
                    catch { /* a drive can throw on IsReady (BitLocker-locked); skip it */ }
                }
            }
            catch { /* GetDrives itself failed; fall back to profile roots below */ }

            // Fallback if no fixed drive enumerated (rare): scan the current profile tree.
            if (roots.Count == 0)
            {
                AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }

            // Find every DMA tool folder under the roots (depth-capped, skip system dirs),
            // emitting live progress as the walk proceeds.
            var folders = new List<string>();
            var scan = new ScanState();
            foreach (var root in roots)
            {
                if (ct.IsCancellationRequested) break;
                scan.CurrentRoot = root;
                progress?.Report(Prog("Scanning", 15,
                    $"Scanning {root}  ({scan.ToolsFound} tool folder(s) so far)..."));
                FindDmaToolFolders(root, 0, folders, scan, progress, ct);
            }

            // De-dup (defensive; a folder can't be reached from two distinct drive roots).
            folders = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            result.FoldersFound = folders.Count;
            progress?.Report(Prog("Scanning", 18,
                $"Scan complete: {folders.Count} DMA tool folder(s) found ({scan.DirsScanned:N0} folders checked)."));
            _log.Info($"Deploy: {folders.Count} DMA tool folder(s) found across {roots.Count} drive root(s), {scan.DirsScanned} dirs scanned.");

            // 3. Operate on each folder.
            int idx = 0;
            foreach (var folder in folders)
            {
                if (ct.IsCancellationRequested) break;
                idx++;
                int pct = 20 + (int)(70.0 * idx / Math.Max(folders.Count, 1));
                progress?.Report(Prog("Deploying", pct, $"Processing {idx}/{folders.Count}: {Path.GetFileName(folder)}"));
                try
                {
                    DeployToFolder(folder, customLeechcoreBytes, staged, mmapContent, result);
                }
                catch (Exception ex)
                {
                    result.Skipped.Add($"{folder} - error: {SanitizeError(ex.Message)}");
                    _log.Warn($"Deploy: folder error '{folder}': {SanitizeError(ex.Message)}");
                }
            }

            // 5. Build the Summary string.
            result.Summary =
                $"{result.FoldersFound} folder(s) found, " +
                $"{result.LeechcoreReplaced} leechcore replaced, " +
                $"{result.FtdiChainCompleted} FTDI chain(s) completed, " +
                $"{result.MmapWritten} mmap written, " +
                $"{result.Skipped.Count} skipped, " +
                $"{result.Flagged.Count} flagged.";
            result.Success = true;

            _log.Info($"Deploy complete: {result.Summary}");
            foreach (var u in result.Updated) _log.Info($"  Updated: {u}");
            foreach (var s in result.Skipped) _log.Warn($"  Skipped: {s}");
            foreach (var f in result.Flagged) _log.Warn($"  Flagged: {f}");

            progress?.Report(Prog("Complete", 100, result.Summary));
        }
        catch (Exception ex)
        {
            result.Error = SanitizeError(ex.Message);
            result.Success = false;
            _log.Error($"Deploy failed: {result.Error}");
            progress?.Report(Prog("Failed", 0, result.Error));
        }
        finally
        {
            if (staging != null)
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
        return result;
    }

    private void DeployToFolder(
        string folder, byte[] customLeechcoreBytes,
        Dictionary<string, string> staged, string mmapContent, DeployResult result)
    {
        var leechcorePath = Path.Combine(folder, "leechcore.dll");
        var changes = new List<string>();

        // 3a. VERSION / ABI CHECK on the existing leechcore.dll.
        long existingLen = -1;
        try { existingLen = new FileInfo(leechcorePath).Length; } catch { }

        bool replace = false;
        if (existingLen == CustomLeechcoreSize)
        {
            // Already our custom build - leave it.
            result.Flagged.Add($"{folder} - leechcore already current ({CustomLeechcoreSize} bytes)");
            _log.Info($"Deploy: '{folder}' leechcore already current - skipping replacement.");
        }
        else
        {
            var existingVer = TryGetFileVersion(leechcorePath);
            if (existingVer != null &&
                !string.IsNullOrEmpty(CustomLeechcoreVersion) &&
                string.Equals(existingVer, CustomLeechcoreVersion, StringComparison.OrdinalIgnoreCase))
            {
                // Same version string but different size - treat as already ours.
                result.Flagged.Add($"{folder} - leechcore already current (v{CustomLeechcoreVersion})");
                _log.Info($"Deploy: '{folder}' leechcore version matches custom - skipping replacement.");
            }
            else
            {
                var abiMinor = MajorMinor(existingVer);
                if (existingVer == null)
                {
                    // No version resource - fall back to replacing (size already != custom).
                    replace = true;
                }
                else if (abiMinor == CustomLeechcoreAbiMinor)
                {
                    // Same major.minor - drop-in safe.
                    replace = true;
                }
                else
                {
                    // Different minor - ABI mismatch, do not touch.
                    result.Flagged.Add($"{folder} - leechcore ABI mismatch {abiMinor} - left as-is");
                    _log.Warn($"Deploy: '{folder}' leechcore ABI {abiMinor} != {CustomLeechcoreAbiMinor} - left as-is.");
                }
            }
        }

        // 3b. REPLACE (backup-first).
        if (replace)
        {
            try
            {
                var backup = leechcorePath + ".orig";
                if (!File.Exists(backup) && File.Exists(leechcorePath))
                    File.Copy(leechcorePath, backup);
                File.WriteAllBytes(leechcorePath, customLeechcoreBytes);
                result.LeechcoreReplaced++;
                changes.Add("leechcore replaced (+.orig backup)");
            }
            catch (IOException)
            {
                // File locked - tool is running.
                result.Skipped.Add($"{folder} - leechcore.dll in use - close the tool and re-run");
                _log.Warn($"Deploy: '{folder}' leechcore.dll locked (tool running) - skipped.");
                // Still attempt FTDI gap-fill + mmap below (those files aren't locked by an open leechcore).
            }
            catch (UnauthorizedAccessException)
            {
                result.Skipped.Add($"{folder} - leechcore.dll access denied");
                _log.Warn($"Deploy: '{folder}' leechcore.dll access denied - skipped.");
            }
        }

        // 3c. FTDI CHAIN - fill gaps only; never clobber an existing working dll.
        int chainFilled = 0;
        foreach (var dll in DeployFtdiChain)
        {
            if (!staged.TryGetValue(dll, out var src)) continue;
            var dest = Path.Combine(folder, dll);
            if (File.Exists(dest)) continue; // present - leave as-is
            try
            {
                File.Copy(src, dest);
                chainFilled++;
            }
            catch (Exception ex)
            {
                _log.Warn($"Deploy: '{folder}' could not copy {dll}: {SanitizeError(ex.Message)}");
            }
        }
        if (chainFilled > 0)
        {
            result.FtdiChainCompleted++;
            changes.Add($"FTDI chain filled (+{chainFilled})");
        }

        // 3d. MMAP - deliver the fresh map next to each tool (backup existing once).
        if (!string.IsNullOrEmpty(mmapContent))
        {
            var mmapPath = Path.Combine(folder, "mmap.txt");
            try
            {
                var backup = mmapPath + ".orig";
                if (File.Exists(mmapPath) && !File.Exists(backup))
                    File.Copy(mmapPath, backup);
                File.WriteAllText(mmapPath, mmapContent);
                result.MmapWritten++;
                changes.Add("mmap.txt written");
            }
            catch (Exception ex)
            {
                _log.Warn($"Deploy: '{folder}' could not write mmap.txt: {SanitizeError(ex.Message)}");
            }
        }

        // 4. FLAG transient-extractor tools (re-extract stock leechcore to %TEMP%).
        bool transientHeuristic = folder.IndexOf("DMATool", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!transientHeuristic)
        {
            try
            {
                transientHeuristic = Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly).Any();
            }
            catch { }
        }
        if (transientHeuristic)
            result.Flagged.Add($"{folder} - this tool may re-extract stock leechcore to %TEMP% on launch; on-disk replace may not persist.");

        // 3e. Record what changed.
        if (changes.Count > 0)
            result.Updated.Add($"{folder} - {string.Join(", ", changes)}");
    }

    // Recursively find every DMA tool folder (contains leechcore.dll OR vmm.dll) under
    // root, depth-capped, skipping system/transient/store dirs. Emits live progress via
    // `progress`: a heartbeat every ScanHeartbeatEvery dirs, and a line per folder found.
    // Per-dir try/catch for UnauthorizedAccess so a single locked dir never aborts the walk.
    private static void FindDmaToolFolders(
        string dir, int depth, List<string> found, ScanState st,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || depth > DeployDepthCap) return;
        if (ShouldSkipDir(dir)) return;

        st.DirsScanned++;
        if (progress != null && st.DirsScanned % ScanHeartbeatEvery == 0)
        {
            progress.Report(Prog("Scanning", 16,
                $"Scanning {st.CurrentRoot}  ({st.DirsScanned:N0} folders checked, {st.ToolsFound} tool(s) found)..."));
        }

        // Anchor check: does THIS folder hold any DMA-exclusive library file?
        try
        {
            bool isToolFolder = false;
            foreach (var anchor in DmaAnchorFiles)
            {
                if (File.Exists(Path.Combine(dir, anchor))) { isToolFolder = true; break; }
            }
            if (isToolFolder)
            {
                found.Add(dir);
                st.ToolsFound++;
                progress?.Report(Prog("Scanning", 17,
                    $"Found DMA tool folder #{st.ToolsFound}: {dir}"));
            }
        }
        catch (UnauthorizedAccessException) { return; }
        catch (Exception) { /* ignore this dir's anchor check, still try subdirs */ }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (ct.IsCancellationRequested) return;
                FindDmaToolFolders(sub, depth + 1, found, st, progress, ct);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception) { }
    }

    // Directory NAMES (exact, case-insensitive) that never hold a DMA tool and are
    // either huge (caches) or recursion hazards. Pruning these keeps a whole-drive walk
    // to a second or two. None of these are where pcileech/MemProcFS/radars install.
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset",
        "Recovery", "Config.Msi", "Windows.old", "OneDriveTemp", "PerfLogs",
        "node_modules", ".git", ".svn", ".hg", ".vs", ".nuget", ".gradle",
        ".cargo", ".dotnet", "__pycache__", "AppData\\Local\\Packages",
    };

    private static bool ShouldSkipDir(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (SkipDirNames.Contains(name)) return true;

        // Reparse points (junctions/symlinks) are the classic whole-drive recursion trap:
        // C:\Users\<u>\AppData\Local\Application Data and "My Documents\My Music" etc. loop
        // back on themselves. DMA tools are never behind a junction, so skip them outright.
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true;
        }
        catch { /* attribute read can fail on locked dirs; let the caller's try/catch handle */ }

        if (dir.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // UWP package store + the bulkiest Local caches - never a tool, always large.
        if (dir.IndexOf(@"AppData\Local\Packages", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (dir.IndexOf(@"AppData\Local\Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) return true;

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windir) && dir.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) return true;

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf) && dir.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return true;
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86) && dir.StartsWith(pf86, StringComparison.OrdinalIgnoreCase)) return true;

        // ProgramData is a large machine-wide cache tree; no DMA tool installs there.
        var progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(progData) && dir.StartsWith(progData, StringComparison.OrdinalIgnoreCase)) return true;

        // AppData\Local\Temp is transient - skip (also covers our own nf_*/nf_deploy_* staging).
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(temp) && dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string? TryGetFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrEmpty(fvi.FileVersion) ? null : fvi.FileVersion.Trim();
        }
        catch { return null; }
    }

    // "2.22.9.95" -> "2.22"; null/garbage -> null.
    private static string? MajorMinor(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        var parts = version.Split('.');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _)) return null;
        return $"{parts[0]}.{parts[1]}";
    }
}
