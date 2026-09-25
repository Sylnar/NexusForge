using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Sylnar.Helpers;

namespace Sylnar.Services;

/// <summary>
/// Reads physical memory addresses through the FPGA via leechcore.
/// Uses the same VMM/leechcore stack as DmaTestService — works for arbitrary
/// physical addresses including the FPGA's own BAR (PCIe self-loopback through
/// the chipset, which AMD platforms generally allow).
///
/// Primary use case: reading the firmware's debug counters / ring buffer at the
/// AHCI controller's BAR (typically around 0xDCC00000 + offset on this system,
/// but the user can override).
/// </summary>
public sealed class BarProbeService
{
    private readonly LogService _log;
    private readonly DmaTestService _dmaTest;

    /// <summary>
    /// Polling output line: timestamp + index + raw hex of the entry.
    /// </summary>
    public sealed record ProbeSample(DateTime At, ulong Address, byte[] Data);

    public BarProbeService(LogService log, DmaTestService dmaTest)
    {
        _log = log;
        _dmaTest = dmaTest;
    }

    /// <summary>
    /// Quick connectivity check — reads 16 bytes at physical address 0x100000
    /// (the 1 MB mark, almost always real RAM on any x64 system). If this
    /// succeeds, the FTDI link + LeechCore + FPGA DMA path are all working.
    /// Independent of any specific BAR layout the user might enter.
    /// </summary>
    public byte[] TestConnection()
    {
        return ReadOnce(0x100000, 16);
    }

    /// <summary>
    /// Read the FPGA's own PCIe config space (4 KB extended) via PCILeech's
    /// command channel. Uses Type 0 config TLPs which the root complex always
    /// routes correctly, so this works even when memory-space self-loopback
    /// is blocked. Useful for inspecting the FPGA's identity, BAR table,
    /// capability list, DSN, etc.
    /// </summary>
    public byte[] ReadFpgaConfigSpace()
    {
        using var lease = _dmaTest.AcquireDevice("config-space read");
        IntPtr hVMM = ConnectVmm();
        try
        {
            IntPtr hLC = GetLcHandle(hVMM);
            if (!VmmNative.LcCommand(hLC, VmmNative.LC_CMD_FPGA_PCIECFGSPACE_RD, 0, IntPtr.Zero,
                                     out IntPtr pData, out uint cbData))
            {
                throw new IOException("LcCommand(FPGA_PCIECFGSPACE_RD) returned false");
            }
            if (pData == IntPtr.Zero || cbData == 0)
                throw new IOException("LcCommand returned no data");
            try
            {
                var buf = new byte[cbData];
                Marshal.Copy(pData, buf, 0, (int)cbData);
                return buf;
            }
            finally
            {
                VmmNative.LcMemFree(pData);
            }
        }
        finally
        {
            VmmNative.VMMDLL_Close(hVMM);
        }
    }

    /// <summary>
    /// One-shot read of <paramref name="length"/> bytes at <paramref name="physAddr"/>.
    /// Returns the raw bytes, or throws on init/read failure.
    /// </summary>
    public byte[] ReadOnce(ulong physAddr, uint length)
    {
        if (length == 0 || length > 4096)
            throw new ArgumentOutOfRangeException(nameof(length), "length must be 1..4096");

        using var lease = _dmaTest.AcquireDevice("BAR read");
        IntPtr hVMM = ConnectVmm();
        try
        {
            IntPtr hLC = GetLcHandle(hVMM);
            return ReadInternal(hLC, physAddr, length);
        }
        finally
        {
            VmmNative.VMMDLL_Close(hVMM);
        }
    }

    /// <summary>
    /// Polling loop: every <paramref name="periodMs"/> ms, read <paramref name="length"/>
    /// bytes at <paramref name="physAddr"/>, append to <paramref name="logFilePath"/>
    /// as a line "ISO8601_timestamp HEXBYTES". Stops when <paramref name="ct"/> is cancelled
    /// or a read fails N times in a row (FPGA died — typical sign the target PC has frozen).
    /// </summary>
    public async Task PollAsync(
        ulong physAddr,
        uint length,
        int periodMs,
        string logFilePath,
        CancellationToken ct,
        int failuresBeforeStop = 5)
    {
        if (periodMs < 10) periodMs = 10;
        if (periodMs > 60_000) periodMs = 60_000;

        using var lease = _dmaTest.AcquireDevice("BAR poll");
        IntPtr hVMM = ConnectVmm();
        try
        {
            IntPtr hLC = GetLcHandle(hVMM);

            using var f = new StreamWriter(logFilePath, append: true);
            await f.WriteLineAsync(
                $"# BarProbe poll start  addr=0x{physAddr:X16} len={length} period={periodMs}ms  at {DateTime.UtcNow:O}");
            await f.FlushAsync();

            int consecutiveFailures = 0;
            long sampleIdx = 0;
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                long tickStart = sw.ElapsedMilliseconds;
                byte[]? data = null;
                try
                {
                    data = ReadInternal(hLC, physAddr, length);
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    await f.WriteLineAsync(
                        $"{DateTime.UtcNow:O} #{sampleIdx} READ_ERROR ({consecutiveFailures}/{failuresBeforeStop}): {ex.Message}");
                    if (consecutiveFailures >= failuresBeforeStop)
                    {
                        await f.WriteLineAsync(
                            $"# BarProbe poll stop  reason=consecutive_read_failures  at {DateTime.UtcNow:O}");
                        await f.FlushAsync();
                        _log.Warn($"BarProbe stopped: {failuresBeforeStop} consecutive read failures (FPGA likely lost)");
                        return;
                    }
                }

                if (data is not null)
                {
                    var hex = ToHex(data);
                    await f.WriteLineAsync($"{DateTime.UtcNow:O} #{sampleIdx} {hex}");
                }

                sampleIdx++;
                if ((sampleIdx & 0xFF) == 0)
                    await f.FlushAsync();

                long elapsed = sw.ElapsedMilliseconds - tickStart;
                int sleep = periodMs - (int)elapsed;
                if (sleep > 0)
                    await Task.Delay(sleep, ct).ContinueWith(_ => { });
            }

            await f.WriteLineAsync(
                $"# BarProbe poll stop  reason=cancelled  samples={sampleIdx}  at {DateTime.UtcNow:O}");
            await f.FlushAsync();
            _log.Info($"BarProbe stopped after {sampleIdx} samples (cancelled)");
        }
        finally
        {
            VmmNative.VMMDLL_Close(hVMM);
        }
    }

    // ---- internals ---------------------------------------------------------

    private IntPtr ConnectVmm()
    {
        // DmaTestService owns the embedded DLL extraction + PInvoke resolver.
        // Make sure it's been initialized before we call into VmmNative ourselves.
        // (Idempotent — safe to call every time.) We re-open VMM for every probe
        // session so we don't share leechcore handles with a running speed test.
        _dmaTest.EnsureLibraries();

        var argList = new List<string> { "-device", "fpga", "-norefresh", "-waitinitialize" };
        if (DmaTestService.HasCachedMmap())
        {
            argList.Add("-memmap");
            argList.Add(DmaTestService.MmapCachePath);
        }
        var args = argList.ToArray();
        var hVMM = VmmNative.VMMDLL_Initialize(args.Length, args);
        if (hVMM == IntPtr.Zero)
            throw new InvalidOperationException(
                "Could not open DMA device. Verify: card seated in PCIe slot, firmware loaded, " +
                "USB-C DATA cable connected, FTDI driver installed.");
        return hVMM;
    }

    private static IntPtr GetLcHandle(IntPtr hVMM)
    {
        if (!VmmNative.VMMDLL_ConfigGet(hVMM, VmmNative.OPT_CORE_LEECHCORE_HANDLE, out ulong hLC) || hLC == 0)
            throw new InvalidOperationException("Failed to get LeechCore handle from VMM");
        return (IntPtr)(long)hLC;
    }

    private static byte[] ReadInternal(IntPtr hLC, ulong pa, uint cb)
    {
        var buf = new byte[cb];
        var pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            bool ok = VmmNative.LcRead(hLC, pa, cb, pinned.AddrOfPinnedObject());
            if (!ok)
                throw new IOException($"LcRead returned false at 0x{pa:X16} cb={cb}");
            return buf;
        }
        finally
        {
            pinned.Free();
        }
    }

    private static string ToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("X2"));
            if (i + 1 < data.Length) sb.Append(' ');
        }
        return sb.ToString();
    }
}
