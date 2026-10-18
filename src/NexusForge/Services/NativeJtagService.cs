using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Sylnar.Helpers;
using Sylnar.Models;

namespace Sylnar.Services;

public class NativeJtagService : IDisposable
{
    /// <summary>Markers the SPI status-register reads are echoed behind.</summary>
    private const string StatusReg1Marker = "SylnarSR1=";
    private const string StatusReg2Marker = "SylnarSR2=";

    private readonly LogService _logService;
    private readonly AppSettings _settings;
    private string? _toolDir;
    private bool _extracted;

    public NativeJtagService(LogService logService, AppSettings settings)
    {
        _logService = logService;
        _settings = settings;
    }

    private void EnsureToolsExtracted()
    {
        if (_extracted && _toolDir != null && Directory.Exists(_toolDir))
            return;

        _toolDir = OwnedTempDirs.Register(Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(_toolDir);

        var assembly = Assembly.GetExecutingAssembly();
        var resources = new[]
        {
            "openocd.exe", "libhidapi-0.dll", "libusb-1.0.dll",
            "ch347.cfg", "xilinx-xc7.cfg", "xilinx-dna.cfg", "jtagspi.cfg",
            "bscan_spi_xc7a35t.bit", "bscan_spi_xc7a50t.bit",
            "bscan_spi_xc7a75t.bit", "bscan_spi_xc7a100t.bit",
            "bscan_spi_xc7a200t.bit",
        };

        foreach (var fileName in resources)
        {
            var destPath = Path.Combine(_toolDir, fileName);
            // A missing resource used to be ignored here and surfaced much later
            // as a confusing OpenOCD "file not found" / flash failure.
            if (!ResourceCrypto.ExtractResource(assembly, fileName, destPath))
                throw new FileNotFoundException($"Embedded JTAG tool is missing from this build: {fileName}", fileName);
        }

        _extracted = true;
    }

    private (bool success, string output, string error) RunOpenOcd(
        string commands, int timeoutMs = 15000, string[]? extraCfgFiles = null)
    {
        EnsureToolsExtracted();

        var openocdPath = Path.Combine(_toolDir!, "openocd.exe");
        var ch347Cfg = Path.Combine(_toolDir!, "ch347.cfg");
        var xc7Cfg = Path.Combine(_toolDir!, "xilinx-xc7.cfg");

        var cfgArgs = $"-f \"{ch347Cfg}\" -f \"{xc7Cfg}\"";
        if (extraCfgFiles != null)
        {
            foreach (var cfg in extraCfgFiles)
                cfgArgs += $" -f \"{Path.Combine(_toolDir!, cfg)}\"";
        }

        var args = $"-s \"{_toolDir}\" {cfgArgs} -c \"{commands}; exit\"";

        var psi = new ProcessStartInfo
        {
            FileName = openocdPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _toolDir!,
        };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        using var process = new Process();
        process.StartInfo = psi;

        try { process.Start(); }
        catch (Exception ex)
        {
            _logService.Error($"JTAG engine failed to start: {ex.Message}");
            return (false, "", ex.Message);
        }

        var outputTask = Task.Run(() =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = process.StandardOutput.ReadLine();
                if (line != null) outputBuilder.AppendLine(line);
            }
        });

        var errorTask = Task.Run(() =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = process.StandardError.ReadLine();
                if (line != null) errorBuilder.AppendLine(line);
            }
        });

        bool exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(true); } catch { }
            _logService.Error("JTAG operation timed out.");
            return (false, outputBuilder.ToString(), "Timed out");
        }

        Task.WaitAll(outputTask, errorTask);
        return (process.ExitCode == 0, outputBuilder.ToString(), errorBuilder.ToString());
    }

    public BoardInfo DetectBoard()
    {
        var boardInfo = new BoardInfo();

        _logService.Info("Detecting FPGA board via JTAG...");

        var (success, output, error) = RunOpenOcd("init; scan_chain");
        var combined = output + "\n" + error;

        if (!success && combined.Contains("no device found"))
        {
            _logService.Error("CH347 adapter not found.");
            _logService.Warn("Connect the JTAG USB cable and ensure the WCH driver is installed.");
            return boardInfo;
        }

        var idcodeMatch = Regex.Match(combined, @"found:\s*0x([0-9a-fA-F]{8})", RegexOptions.IgnoreCase);
        if (!idcodeMatch.Success)
            idcodeMatch = Regex.Match(combined, @"0x([0-9a-fA-F]{8})", RegexOptions.IgnoreCase);

        if (!idcodeMatch.Success)
        {
            if (combined.Contains("CH347 Open Succ", StringComparison.OrdinalIgnoreCase))
            {
                _logService.Error("JTAG adapter connected but FPGA not responding.");
                _logService.Warn("Replug the USB cable and ensure the board has power.");
            }
            else
            {
                _logService.Error("Could not detect FPGA on JTAG chain.");
                _logService.Warn("Check USB cable connection and WCH driver status.");
            }
            return boardInfo;
        }

        string idcodeHex = "0x" + idcodeMatch.Groups[1].Value.ToLowerInvariant();
        uint idcode = Convert.ToUInt32(idcodeMatch.Groups[1].Value, 16);
        uint idcodeMasked = idcode & 0x0FFFFFFF;

        // Validate that the read IDCODE is actually a Xilinx part. Per IEEE 1149.1
        // the IDCODE register's bit 0 is always 1; if the JTAG TDO line is stuck
        // (cable plugged in but no FPGA / unpowered / wrong voltage), the read
        // returns exactly 0x00000001. The all-ones pattern 0x0FFFFFFF after masking
        // means TDO stuck high. Either way, no chip is on the chain — refuse to
        // claim "Connected ✓ Unknown Xilinx" since that misleads the user.
        // Manufacturer ID lives in bits 11:1 and must equal 0x049 (Xilinx).
        uint mfgId = (idcodeMasked >> 1) & 0x7FF;
        bool idcodeValid = (idcodeMasked != 0x00000001) && (idcodeMasked != 0x0FFFFFFF) && (mfgId == 0x049);
        if (!idcodeValid)
        {
            _logService.Error($"JTAG chain returned IDCODE {idcodeHex} — no FPGA detected.");
            _logService.Warn("Most common cause: the FPGA board is not powered, the JTAG cable is loose,");
            _logService.Warn("or the JTAG TDO line is stuck (board off / not connected to the host PC).");
            _logService.Warn("Verify the board has power and the USB-JTAG cable is firmly seated, then retry.");
            boardInfo.IsDetected = false;
            boardInfo.JtagCable = "WCH CH347 (USB JTAG)";
            return boardInfo;
        }

        string deviceName = idcodeMasked switch
        {
            0x0362E093 => "XC7A15T (Artix-7)",
            0x0362D093 => "XC7A35T (Artix-7)",
            0x0362C093 => "XC7A50T (Artix-7)",
            0x03632093 => "XC7A75T (Artix-7)",
            0x03631093 => "XC7A100T (Artix-7)",
            0x03636093 => "XC7A200T (Artix-7)",
            0x03647093 => "XC7K325T (Kintex-7)",
            0x0364C093 => "XC7K355T (Kintex-7)",
            0x03651093 => "XC7K410T (Kintex-7)",
            _ => $"Unknown Xilinx ({idcodeHex})"
        };

        _logService.Info($"FPGA detected: {deviceName}");
        _logService.Info($"IDCODE: {idcodeHex}");

        boardInfo.IsDetected = true;
        boardInfo.DeviceName = deviceName;
        boardInfo.IdCode = idcodeHex;
        // The package is not readable over JTAG. Only show the configured part
        // when the detected die matches it, instead of labelling every board
        // (e.g. a 35T) with the 75T package.
        boardInfo.Package = _settings.FpgaPart.StartsWith(
            deviceName.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? _settings.FpgaPart : "—";
        boardInfo.JtagCable = "WCH CH347 (USB JTAG)";
        boardInfo.Bridge = Path.GetFileNameWithoutExtension(GetBscanProxy(idcodeMasked) ?? string.Empty);
        boardInfo.DetectedAt = DateTime.Now;

        _logService.Info("Reading Device DNA...");
        var (_, dnaOutput, dnaError) = RunOpenOcd(
            "init; " +
            "irscan xc7.tap 0x10; runtest 64; " +
            "irscan xc7.tap 0x17; " +
            "set raw [drscan xc7.tap 64 0]; " +
            "echo RAW_DNA=$raw; " +
            "runtest 64; " +
            "irscan xc7.tap 0x16; runtest 64");

        var dnaCombined = dnaOutput + "\n" + dnaError;
        var rawDnaMatch = Regex.Match(dnaCombined, @"RAW_DNA=([0-9a-fA-F]+)", RegexOptions.IgnoreCase);

        if (rawDnaMatch.Success)
        {
            string rawHex = rawDnaMatch.Groups[1].Value.PadLeft(16, '0');
            ulong rawVal = Convert.ToUInt64(rawHex, 16);
            uint word0 = (uint)(rawVal & 0xFFFFFFFF);
            uint word1 = (uint)(rawVal >> 32);
            ulong rawResult = ((ulong)ReverseBits32(word0) << 32) | ReverseBits32(word1);
            ulong dna = rawResult >> 7;

            string dnaHex = dna.ToString("x16");
            boardInfo.Dna = dnaHex;
            boardInfo.DnaFormatted = dnaHex;
            _logService.Info($"DNA: {dnaHex}");
        }
        else
        {
            _logService.Warn("Could not read Device DNA.");
        }

        return boardInfo;
    }

    private string? GetBscanProxy(uint idcodeMasked)
    {
        return idcodeMasked switch
        {
            0x0362D093 => "bscan_spi_xc7a35t.bit",
            0x0362C093 => "bscan_spi_xc7a50t.bit",
            0x03632093 => "bscan_spi_xc7a75t.bit",
            0x03631093 => "bscan_spi_xc7a100t.bit",
            0x03636093 => "bscan_spi_xc7a200t.bit",
            0x0362E093 => "bscan_spi_xc7a35t.bit",
            _ => null
        };
    }

    public FlashResult FlashFirmware(
        string firmwarePath,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var fileName = Path.GetFileName(firmwarePath);

        _logService.Info($"Flashing firmware: {fileName}");
        progress?.Report(new FlashProgress { Stage = "Initializing", Percentage = 5, Message = "Connecting to JTAG..." });

        EnsureToolsExtracted();

        string ext = Path.GetExtension(firmwarePath).ToLowerInvariant();
        bool isBin = ext == ".bin";
        bool isBit = ext == ".bit";

        if (!isBin && !isBit)
        {
            _logService.Error($"Unsupported file type: {ext}. Use .bit or .bin files.");
            return new FlashResult { Success = false, ErrorMessage = $"Unsupported: {ext}" };
        }

        // The path is embedded in a Tcl word ({...}) inside a double-quoted -c
        // argument; these characters would break out of either and corrupt the
        // OpenOCD command line.
        if (firmwarePath.IndexOfAny(new[] { '{', '}', '"' }) >= 0)
        {
            const string msg = "Firmware path contains { } or \" characters. Rename the file or move it to a simpler folder (e.g. C:\\Firmware).";
            _logService.Error(msg);
            return new FlashResult { Success = false, ErrorMessage = msg };
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Only now that the file has passed its checks and this flash is really
        // going to touch the adapter.
        StopStrayOpenOcd();

        string firmwareOcd = firmwarePath.Replace("\\", "/");

        if (isBit)
        {
            _logService.Info("Mode: Direct JTAG programming (.bit → FPGA fabric)");
            _logService.Info("Note: This is volatile — firmware is lost on power cycle.");
            progress?.Report(new FlashProgress { Stage = "Programming", Percentage = 20, Message = $"Sending {fileName} to FPGA..." });

            string commands = $"init; xc7_program xc7.tap; pld load 0 {{{firmwareOcd}}}";
            var (success, output, error) = RunOpenOcd(commands, _settings.FlashTimeoutSeconds * 1000);
            sw.Stop();

            var combined = output + "\n" + error;
            bool ok = success ||
                      combined.Contains("loaded file", StringComparison.OrdinalIgnoreCase);

            if (ok)
            {
                _logService.Info($"Flash complete in {sw.Elapsed.TotalSeconds:F1}s");
                progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "FPGA programmed!" });
                return new FlashResult { Success = true, Duration = sw.Elapsed };
            }

            return ReportFlashError(combined, sw.Elapsed, progress);
        }
        else
        {
            return FlashSpiWithProgress(firmwarePath, firmwareOcd, fileName, sw, progress, cancellationToken);
        }
    }

    public async Task<bool> HotResetFpgaAsync(IProgress<FlashProgress>? progress)
    {
        _logService.Info("Hot Reload: PCIe bus reset (no JTAG, no reboot)...");
        progress?.Report(new FlashProgress { Stage = "Finding", Percentage = 10, Message = "Finding DMA PCIe device..." });

        const string findDeviceScript = @"
$pciDevs = Get-PnpDevice -Class 'System','SCSIAdapter','HDC','Net','Display','Unknown','Multifunction' `
           -ErrorAction SilentlyContinue |
           Where-Object { $_.InstanceId -match '^PCI\\' -and $_.Status -eq 'OK' }

# Also check for any PCI device with error state (freshly flashed DMA might be in error)
$errDevs = Get-PnpDevice -ErrorAction SilentlyContinue |
           Where-Object { $_.InstanceId -match '^PCI\\' -and $_.Status -ne 'OK' -and $_.Class -ne 'System' }

# Find the PCIe root port or bridge that is a parent of our DMA device
# For now, output all PCI devices so user can see what's on the bus
$allPci = Get-PnpDevice -ErrorAction SilentlyContinue |
          Where-Object { $_.InstanceId -match '^PCI\\' }

foreach ($d in $allPci) {
    $parent = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
               -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    Write-Output ('{0}|{1}|{2}|{3}' -f $d.InstanceId, $d.FriendlyName, $d.Status, $parent)
}
";
        var (_, devOut, _) = await RunPowerShellAsync(findDeviceScript);

        string? dmaInstanceId = null;
        string? dmaName = null;

        foreach (var line in devOut.Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 4) continue;

            var instId = parts[0].Trim();
            var name = parts[1].Trim();
            var status = parts[2].Trim();

            if (name.Contains("Root Port", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Host Bridge", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PCI Express", StringComparison.OrdinalIgnoreCase) && name.Contains("Root", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ISA Bridge", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("RAM ", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("LPC ", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(name))
                continue;

            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("USB ", StringComparison.OrdinalIgnoreCase) && name.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                continue;

            dmaInstanceId = instId;
            dmaName = name;
        }

        if (dmaInstanceId == null)
        {
            _logService.Warn("Could not auto-detect the DMA PCIe device.");
            _logService.Info("Falling back to full PCIe bus rescan...");
            progress?.Report(new FlashProgress { Stage = "Rescan", Percentage = 30, Message = "Rescanning PCIe bus..." });

            await RunPnpUtil("/scan-devices");
            await Task.Delay(3000);

            _logService.Info("PCIe bus rescan complete.");
            progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "Bus rescan complete" });
            return true;
        }

        _logService.Info($"Found DMA device: {dmaName}");
        progress?.Report(new FlashProgress { Stage = "Disabling", Percentage = 25, Message = $"Disabling: {dmaName}..." });

        string disableScript = $@"
$dev = Get-PnpDevice | Where-Object {{ $_.InstanceId -eq '{dmaInstanceId.Replace("'", "''")}' }}
if ($dev) {{
    Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output 'DISABLED'
}}
";
        var (_, disOut, _) = await RunPowerShellAsync(disableScript);
        bool disabled = disOut.Contains("DISABLED");

        if (disabled)
            _logService.Info("PCIe device disabled. Link is down.");
        else
            _logService.Warn("Could not disable device — trying direct bus rescan instead.");

        progress?.Report(new FlashProgress { Stage = "Waiting", Percentage = 45, Message = "Waiting for link to settle..." });
        await Task.Delay(2000);

        if (disabled)
        {
            _logService.Info("Re-enabling PCIe device...");
            progress?.Report(new FlashProgress { Stage = "Enabling", Percentage = 60, Message = "Re-enabling PCIe device..." });

            string enableScript = $@"
$dev = Get-PnpDevice | Where-Object {{ $_.InstanceId -eq '{dmaInstanceId.Replace("'", "''")}' }}
if ($dev) {{
    Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output 'ENABLED'
}}
";
            var (_, enOut, _) = await RunPowerShellAsync(enableScript);
            if (enOut.Contains("ENABLED"))
                _logService.Info("PCIe device re-enabled. Link retraining...");
        }

        progress?.Report(new FlashProgress { Stage = "Training", Percentage = 75, Message = "PCIe link training..." });
        await Task.Delay(3000);

        _logService.Info("Rescanning PCIe bus...");
        progress?.Report(new FlashProgress { Stage = "Rescan", Percentage = 85, Message = "Rescanning bus..." });
        await RunPnpUtil("/scan-devices");
        await Task.Delay(2000);

        progress?.Report(new FlashProgress { Stage = "Stabilizing", Percentage = 95, Message = "Stabilizing..." });
        await Task.Delay(1000);

        _logService.Info("Hot Reload complete.");
        _logService.Info("DMA device has been reset via PCIe bus (no JTAG, no reboot).");
        _logService.Info("The firmware loaded from SPI flash should now be active.");
        progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "Hot Reload complete!" });
        return true;
    }

    private async Task RunPnpUtil(string args)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    private static async Task<(int exitCode, string output, string error)> RunPowerShellAsync(
        string script, int timeoutMs = 15000)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptFile, script);

        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = new Process { StartInfo = psi };
        var sb  = new System.Text.StringBuilder();
        var sbe = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sbe.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exited = await Task.Run(() => proc.WaitForExit(timeoutMs));
        if (!exited)
        {
            try { proc.Kill(true); proc.WaitForExit(5000); } catch { }
        }

        try { File.Delete(scriptFile); } catch { }
        int exitCode = -1;
        try { if (proc.HasExited) exitCode = proc.ExitCode; } catch { }
        return (exitCode, sb.ToString(), sbe.ToString());
    }

    private FlashResult FlashSpiWithProgress(
        string firmwarePath, string firmwareOcd, string fileName,
        Stopwatch sw, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        _logService.Info("Mode: SPI flash programming (.bin → SPI flash)");
        progress?.Report(new FlashProgress { Stage = "Detecting", Percentage = 5, Message = "Identifying FPGA..." });

        var (_, dOut, dErr) = RunOpenOcd("init; scan_chain");
        var idMatch = Regex.Match(dOut + "\n" + dErr, @"found:\s*0x([0-9a-fA-F]{8})", RegexOptions.IgnoreCase);
        if (!idMatch.Success)
        {
            _logService.Error("Could not detect FPGA.");
            progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = "FPGA not detected" });
            return new FlashResult { Success = false, ErrorMessage = "FPGA not detected", Duration = sw.Elapsed };
        }

        uint idcode = Convert.ToUInt32(idMatch.Groups[1].Value, 16) & 0x0FFFFFFF;
        string? bscanFile = GetBscanProxy(idcode);
        if (bscanFile == null)
        {
            _logService.Error("No BSCAN proxy for this FPGA.");
            progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = "Unsupported FPGA" });
            return new FlashResult { Success = false, ErrorMessage = "Unsupported FPGA", Duration = sw.Elapsed };
        }

        if (idcode == 0x0362E093)
        {
            // No dedicated 15T bridge is bundled; the 35T one is used. If the
            // bitstream's IDCODE check rejects it, the bridge load fails below.
            _logService.Warn("XC7A15T: using the XC7A35T SPI bridge (no 15T bridge bundled). If the bridge fails to load, this board needs a 15T-specific bridge bitstream.");
        }

        long firmwareSize = 0;
        try { firmwareSize = new FileInfo(firmwarePath).Length; } catch { }
        int totalSectorsEstimate = Math.Max(1, (int)((firmwareSize + 65535) / 65536));

        _logService.Info("FPGA identified. Loading BSCAN SPI bridge...");
        progress?.Report(new FlashProgress { Stage = "Bridge", Percentage = 10, Message = "Loading BSCAN SPI bridge..." });

        EnsureToolsExtracted();

        var openocdPath = Path.Combine(_toolDir!, "openocd.exe");
        var ch347Cfg = Path.Combine(_toolDir!, "ch347.cfg");
        var xc7Cfg = Path.Combine(_toolDir!, "xilinx-xc7.cfg");
        var spiCfg = Path.Combine(_toolDir!, "jtagspi.cfg");

        // jtagspi_program (defined in jtagspi.cfg) already runs
        // "flash write_image erase" + "flash verify_bank" internally. Adding an
        // explicit verify command here is redundant AND does not work on this
        // bundled OpenOCD 0.11.0+dev CH347/bscan setup: after jtagspi_init the
        // command chain closes right after flash probe, so any standalone read/
        // verify command (verify_image, verify_bank, flash banks) never executes.
        // Empirically confirmed by running the bundled openocd directly against
        // the board. v1.1.13's verify_image even errored ("checksum_memory").
        // So we use exactly the long-proven write flow: write happens in
        // jtagspi_program; we report "written" honestly and never fake a verify.
        // v1.1.33: read the flash chip's two status registers between the probe and
        // the erase, and show what they say.
        //
        // A v1.1.32 transcript from a failing run settled what is wrong, and it is
        // not what v1.1.32 guessed. At 3.75 MHz (the log confirms "clock speed
        // 3750 kHz") all 45 sector erases still reported 2-3 ms, which is far below
        // any real 64 KB erase and far too consistent to be misread status bits. So
        // the slower clock is gone again: it fixed nothing and only made writing
        // slower.
        //
        // What the same transcript proves instead: jtagspi_write_enable (0x4840b0)
        // sends 0x06, reads the status register and logs "Cannot enable write to
        // flash. Status=0x%02x" at LOG_ERROR if the write-enable latch is not set.
        // That line never appeared, so the chip is reachable, it accepted the
        // command and it reported the latch set. The erase that follows then ends
        // on the first status read with the busy bit clear. A chip that sets the
        // latch, takes the erase command, never goes busy and never errors is
        // describing block protection: it is refusing the erase and clearing the
        // latch again.
        //
        // So ask it directly. 0x05 reads status register 1, which carries the block
        // protect bits, and 0x35 reads status register 2, which carries the
        // complement bit that inverts the protected range. Both are reads and
        // change nothing. "jtagspi cmd" hands its answer back as the command's
        // result rather than logging it, so echo is what makes it visible; catch
        // keeps a failure here from stopping the flash.
        string commands =
            $"init; " +
            $"jtagspi_init 0 {{{bscanFile}}}; " +
            $"catch {{echo {StatusReg1Marker}[join [jtagspi cmd 0 1 0x05] _]}}; " +
            $"catch {{echo {StatusReg2Marker}[join [jtagspi cmd 0 1 0x35] _]}}; " +
            $"jtagspi_program {{{firmwareOcd}}} 0x0; " +
            $"xc7_program xc7.tap";

        var args = $"-s \"{_toolDir}\" -f \"{ch347Cfg}\" -f \"{xc7Cfg}\" -f \"{spiCfg}\" -c \"{commands}; exit\"";

        var psi = new ProcessStartInfo
        {
            FileName = openocdPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _toolDir!,
        };

        var allOutput = new System.Text.StringBuilder();
        bool wrote = false, verifyFailed = false;
        int sectorsTotal = totalSectorsEstimate;
        int sectorsErased = 0;
        int eraseFirstSector = 0;   // sector the erase range starts at; the per-sector lines count from it
        bool erasePhase = false;
        long totalPages = firmwareSize / 256;
        long pagesWritten = 0;
        DateTime lastWriteUpdate = DateTime.MinValue;

        // Erase evidence, gathered from OpenOCD's own lines (all level 2, so they
        // always reach us). Times are on the flash stopwatch, in ms. See
        // ReportEraseEvidence for what they are used for.
        long? probeAtMs = null;        // "Found flash device" read
        long? firstPageAtMs = null;    // first "0x%08x." page line read
        int sectorLines = 0;           // "sector N took M ms" lines
        int fastSectors = 0;           // ...of which reported under 10 ms
        long sectorMsMin = long.MaxValue, sectorMsMax = 0, sectorMsSum = 0;
        long? bulkTookMs = null;       // bare "took N ms" from the whole-chip erase
        bool bulkFallback = false;     // "Bulk flash erase failed. Falling back..."
        int? statusReg1 = null, statusReg2 = null;
        bool chipWriteProtected = false;

        // Last safe point to cancel: once the programmer starts erasing, killing
        // it leaves the board with a half-written (unbootable) flash.
        ct.ThrowIfCancellationRequested();

        using var transcript = new FlashTranscript();
        transcript.Note($"Sylnar {VersionInfo.WithV}, SPI flash");
        transcript.Note($"Firmware: {fileName}, {firmwareSize:N0} bytes ({firmwareSize / 1048576.0:F2} MB), " +
                        $"~{totalSectorsEstimate} x 64 KB sectors, {totalPages:N0} pages");
        transcript.Note($"FPGA IDCODE 0x{idcode:X8}, bridge {bscanFile}");
        transcript.Note($"Command: {SanitizePath(commands)}");
        if (transcript.DisplayPath != null)
            _logService.Info($"Flash transcript: {transcript.DisplayPath}");

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex)
        {
            transcript.Note($"Failed to start OpenOCD: {ex.Message}");
            _logService.Error($"Failed to start JTAG engine: {ex.Message}");
            return new FlashResult { Success = false, ErrorMessage = "Engine start failed", Duration = sw.Elapsed };
        }
        // Declared after `process`, so it is disposed first: the registration is
        // cleared before the Process object goes away, on every return path.
        using var activeRegistration = new ActiveOpenOcdRegistration(this, process);
        transcript.Note($"OpenOCD started (process {process.Id})");

        using var cancelNotice = ct.Register(() => _logService.Warn(
            "Cancel ignored: the SPI flash is being erased/written. Interrupting now would leave the board unbootable, so the write will finish."));

        var lineLock = new object();
        int pageLinesSeen = 0;
        void ProcessLine(string line, string stream)
        {
            lock (lineLock)
            {
                // Recorded under the same lock as the parse, so the transcript's
                // order is exactly the order the parser saw the two pipes in.
                // Every line is kept except the write's per-page progress, one
                // "0x%08x." line per 256 bytes (tens of thousands per flash) that
                // carries nothing but an address: the first 64 and every 512th
                // after that are enough to place the write in time against the
                // erase, and keep a transcript to kilobytes.
                var tt = line.Trim();
                bool page = tt.Length == 11 && tt.StartsWith("0x", StringComparison.Ordinal) && tt.EndsWith('.');
                if (!page || pageLinesSeen++ < 64 || pageLinesSeen % 512 == 0)
                    transcript.Line(stream, line);
                ProcessLineCore(line);
            }
        }

        // Both are idempotent: more than one marker can announce the same
        // transition and whichever arrives first should report it once. They are
        // also the gate `erasePhase` was declared for and never used.
        void EnterErasePhase()
        {
            if (erasePhase) return;
            erasePhase = true;
            // A whole-chip erase says nothing at all until it is over, so this
            // may be the only erase news for a minute or more. The elapsed clock
            // on the panel is what shows it is still working.
            progress?.Report(new FlashProgress { Stage = "Erasing", Percentage = 20, Message = "Erasing flash (can take a minute)..." });
        }

        void EndErasePhase(long elapsedMs = 0)
        {
            if (!erasePhase) return;
            erasePhase = false;
            string detail = sectorsErased > 0 ? $" ({sectorsErased} sectors)"
                          : elapsedMs > 0 ? $" in {elapsedMs / 1000.0:F1}s"
                          : "";
            _logService.Info($"Erase complete{detail}.");
            progress?.Report(new FlashProgress { Stage = "Erased", Percentage = 45, Message = "Erase complete. Writing..." });
        }

        // Says in plain words what the chip's status registers report, and sets
        // chipWriteProtected so the erase summary can name the cause. Runs on
        // whichever read arrives; the second call reports the complete picture.
        //
        // Bit layout is the one every Winbond/ISSI/Macronix 25-series part shares.
        // Status register 1: 0 busy, 1 write-enable latch, 2-4 block protect,
        // 5 top/bottom, 6 sector-or-block, 7 status-register protect. Status
        // register 2: 1 quad enable, 6 complement - and complement is the trap,
        // because with no block-protect bits set it protects the WHOLE chip rather
        // than none of it.
        bool protectionReported = false;
        void ReportProtection()
        {
            if (statusReg1 == null) return;

            int sr1 = statusReg1.Value;
            int bp = (sr1 >> 2) & 0x07;
            bool complement = statusReg2 != null && (statusReg2.Value & 0x40) != 0;
            chipWriteProtected = bp != 0 || complement;

            // Wait for the second read before the final word, unless it never comes.
            if (protectionReported) return;
            if (statusReg2 == null && !chipWriteProtected) return;
            protectionReported = true;

            string regs = statusReg2 != null
                ? $"status registers 0x{sr1:X2} and 0x{statusReg2.Value:X2}"
                : $"status register 0x{sr1:X2}";

            if (chipWriteProtected)
            {
                string which = complement && bp == 0
                    ? "the whole chip is protected by its complement bit"
                    : complement
                        ? $"block-protect bits are set (level {bp}) with the complement bit on"
                        : $"block-protect bits are set (level {bp})";
                _logService.Warn($"The flash chip is refusing writes: {which}, from {regs}. " +
                                 "An erase on a protected chip does nothing and reports no error, " +
                                 "which is why flashing appears to skip it.");
            }
            else
            {
                _logService.Info($"Flash chip write protection: none ({regs}).");
            }
            transcript.Note($"Protection: {(chipWriteProtected ? "YES" : "no")}, {regs}");
        }

        // What the erase actually did, in OpenOCD's own numbers. Called at the first
        // written page, with what is known by then, early enough to be read before
        // anyone closes the window mid-write; and again when OpenOCD has exited, into
        // the transcript only, with everything both pipes delivered (the whole-chip
        // "took" line is on stderr and can be read just after the first page, which
        // is on stdout).
        void ReportEraseEvidence(bool final)
        {
            string gap = probeAtMs != null && firstPageAtMs != null
                ? $"{(firstPageAtMs.Value - probeAtMs.Value) / 1000.0:F1}s"
                : "an unknown time";

            string what;
            if (sectorLines > 0)
                what = $"{sectorLines} sector erases, {sectorMsSum / 1000.0:F1}s in total, each {sectorMsMin}-{sectorMsMax} ms" +
                       (bulkFallback ? ", after the chip refused the whole-chip erase" : "");
            else if (bulkTookMs != null)
                what = $"whole-chip erase, {bulkTookMs.Value / 1000.0:F1}s";
            else
                what = "no erase timing was reported";

            string summary = $"Erase: {what}; writing began {gap} after the flash was detected.";

            // These floors sit far below any real SPI NOR erase. A 64 KB block erase
            // takes tens to hundreds of milliseconds on every part these boards use,
            // and a whole-chip erase takes tens of seconds. jtagspi_wait declares an
            // erase finished on the first status read that shows the chip not busy,
            // and one read over this bridge takes about a millisecond, so an erase
            // command the chip never acted on "completes" almost at once, with no
            // error. This only warns; it changes nothing that is sent to the board.
            // Any single sector under the floor counts, not just the slowest: a
            // partly skipped erase (some sectors ignored, some real) would otherwise
            // hide behind one real 150 ms sector.
            string? suspicion = null;
            if (fastSectors > 0)
                suspicion = fastSectors == sectorLines
                    ? "every sector erase reported under 10 ms"
                    : $"{fastSectors} of {sectorLines} sector erases reported under 10 ms";
            else if (sectorLines == 0 && bulkTookMs != null && bulkTookMs.Value < 2000)
                suspicion = $"the whole-chip erase reported {bulkTookMs.Value} ms";
            else if (sectorLines == 0 && bulkTookMs == null && probeAtMs != null && firstPageAtMs != null
                     && firstPageAtMs.Value - probeAtMs.Value < 3000)
                suspicion = $"no erase was reported and writing began {gap} after detection";

            if (!final)
            {
                _logService.Info(summary);
                // The file is named inside the warning itself: the Warnings filter
                // and its Copy show WARN lines only, which hides the INFO line that
                // named it at the start of the flash.
                if (suspicion != null)
                {
                    _logService.Warn($"This erase finished faster than a real flash erase can ({suspicion}). " +
                                     "The flash was almost certainly NOT erased, so what is being written now " +
                                     "will be wrong and the board will not boot from it.");
                    _logService.Warn(chipWriteProtected
                        ? "The cause is the write protection reported above. The protection bits have to be " +
                          "cleared before this board can be flashed."
                        : "The chip did not report write protection, so the cause is still open. Please send " +
                          "the flash record " + (transcript.DisplayPath ?? @"from %LOCALAPPDATA%\Sylnar\flash") + ".");
                }
            }
            transcript.Note((final ? "FINAL " : "") + summary + (suspicion != null ? $" SUSPECT: {suspicion}." : ""));
        }

        void ProcessLineCore(string line)
        {
            allOutput.AppendLine(line);
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) return;

            if (t.Contains("loaded file", StringComparison.OrdinalIgnoreCase) ||
                (t.Contains("loaded", StringComparison.OrdinalIgnoreCase) && t.Contains("pld", StringComparison.OrdinalIgnoreCase)))
            {
                _logService.Info("BSCAN SPI bridge loaded.");
                progress?.Report(new FlashProgress { Stage = "Probing", Percentage = 12, Message = "Probing SPI flash chip..." });
            }

            if (t.Contains("Found flash device", StringComparison.OrdinalIgnoreCase))
            {
                probeAtMs ??= sw.ElapsedMilliseconds;
                var fm = Regex.Match(t, @"Found flash device '([^']+)'", RegexOptions.IgnoreCase);
                string flashName = fm.Success ? fm.Groups[1].Value : "SPI flash";
                _logService.Info($"SPI flash detected: {flashName}");
                progress?.Report(new FlashProgress { Stage = "Detected", Percentage = 15, Message = $"Flash: {flashName}" });
                // The probe is the LAST thing jtagspi_init does, and jtagspi_program
                // starts erasing immediately after it, so this line is where the
                // erase begins as far as anything observable goes. See the block
                // below for why there is nothing better to key on.
                EnterErasePhase();
            }

            // The two status-register reads echoed between the probe and the erase.
            // "jtagspi cmd" answers with the bytes it sent, then "-> ", then the
            // bytes it read, space separated ("05 -> 02 "). The command chain joins
            // that with underscores so echo sees one word, so the value wanted here
            // is the LAST hex token, not the first - the first is the opcode we sent.
            var srMatch = Regex.Match(t,
                $@"(?:{StatusReg1Marker}|{StatusReg2Marker})(\S+)",
                RegexOptions.IgnoreCase);
            int srValue = -1;
            if (srMatch.Success)
            {
                foreach (var part in srMatch.Groups[1].Value.Split('_'))
                {
                    if (Regex.IsMatch(part, @"^[0-9a-fA-F]{1,2}$") &&
                        int.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                                     System.Globalization.CultureInfo.InvariantCulture, out int parsed))
                        srValue = parsed;
                }
            }
            if (srValue >= 0)
            {
                if (t.Contains(StatusReg1Marker, StringComparison.OrdinalIgnoreCase))
                    statusReg1 = srValue;
                else
                    statusReg2 = srValue;

                // Both arrive before the erase; report once the pair is in, or on
                // whichever one arrives if the other never does.
                ReportProtection();
                return;
            }

            // ── erase ────────────────────────────────────────────────────────
            // Almost nothing about the erase is observable, which is the whole
            // bug. Two separate reasons, both read out of the bundled openocd.exe
            // rather than assumed:
            //
            // 1. OpenOCD 0.11's command_print does NOT log. It appends to the
            //    command's Jim result object (_command_print calls
            //    Jim_AppendString and never touches the logger), and this app
            //    runs one "-c init; ...; exit" chain, so that object is never
            //    printed. Everything emitted that way is invisible here:
            //    "auto erase enabled", the pld "loaded file ..." line, and even
            //    "wrote N bytes from file ..." - which is exactly why the success
            //    gate further down cannot rely on `wrote` and counts pages too.
            //    Only log_printf_lf at level <= 2 (the default; no -d is passed)
            //    and OpenOCD's raw printf reach this parser.
            //
            // 2. In the jtagspi driver the erase has two paths, and the quiet one
            //    is the normal one:
            //      bulk    one whole-chip command. "Trying bulk erase." is
            //              level 3 behind an explicit debug_level > 2 guard, so
            //              the erase itself says nothing; only a trailing
            //              "took <n> ms" at level 2 marks the end of it.
            //      sector  "sector <n> took <m> ms" per sector at level 2,
            //              reached directly or after "Bulk flash erase failed"
            //              (level 1, always visible).
            //    The old code matched ONLY the per-sector line, so a bulk erase
            //    showed no Erase step at all and the bar sat on Bridge until the
            //    write began - the reported "it goes straight to writing". The
            //    two other erase parsers were dead outright: "done in" occurs
            //    nowhere in this binary, and "erasing sectors N through M" is
            //    at91sam4l's string, never jtagspi's.
            //
            // So the phase is bracketed by the fixed command chain in jtagspi.cfg
            // (jtagspi_init: pld load, reset halt, flash probe -- then
            // jtagspi_program: flash write_image erase, flash verify_bank): it
            // opens at the probe line above and closes at the first page of the
            // write, with whatever the driver does say used to fill it in.

            // Level 3, so normally absent; parsed anyway because it is the only
            // line that states the real sector count.
            var eraseRangeMatch = Regex.Match(t, @"erase\s+from\s+sector\s+(\d+)\s+to\s+sector\s+(\d+)", RegexOptions.IgnoreCase);
            if (eraseRangeMatch.Success)
            {
                int first = int.Parse(eraseRangeMatch.Groups[1].Value);
                int last = int.Parse(eraseRangeMatch.Groups[2].Value);
                if (last >= first)
                {
                    eraseFirstSector = first;
                    sectorsTotal = last - first + 1;
                }
                EnterErasePhase();
                _logService.Info($"Erasing sectors {first}–{last} ({sectorsTotal} total)...");
            }

            // LOG_WARNING, so always visible: the chip refused the bulk erase and
            // the driver is falling back to erasing sector by sector. Worth
            // saying out loud - it is also why some runs take much longer.
            if (t.Contains("Bulk flash erase failed", StringComparison.OrdinalIgnoreCase))
            {
                bulkFallback = true;
                EnterErasePhase();
                _logService.Warn("Bulk erase refused by the chip; falling back to sector erase (slower).");
                progress?.Report(new FlashProgress { Stage = "Erasing", Percentage = 20, Message = "Erasing sector by sector..." });
            }

            var sectorMatch = Regex.Match(t, @"sector\s+(\d+)\s+took\s+(\d+)\s*ms", RegexOptions.IgnoreCase);
            if (sectorMatch.Success)
            {
                EnterErasePhase();
                if (long.TryParse(sectorMatch.Groups[2].Value, out long sectorMs))
                {
                    sectorLines++;
                    if (sectorMs < 10) fastSectors++;
                    sectorMsSum += sectorMs;
                    if (sectorMs < sectorMsMin) sectorMsMin = sectorMs;
                    if (sectorMs > sectorMsMax) sectorMsMax = sectorMs;
                }
                int sectorNum = int.Parse(sectorMatch.Groups[1].Value);
                // The driver logs the absolute sector number; count from the
                // start of the erase range so a range that does not begin at 0
                // does not read as more sectors erased than there are.
                sectorsErased = Math.Max(1, sectorNum - eraseFirstSector + 1);

                int total = sectorsTotal > 0 ? sectorsTotal : totalSectorsEstimate;
                if (sectorsErased > total) total = sectorsErased;

                // Floored at the 20 the phase opened on, so the first sector of a
                // large erase does not read as going backwards from 20% to 16%.
                int pct = 15 + (int)(40.0 * sectorsErased / total);
                pct = Math.Clamp(pct, 20, 54);

                progress?.Report(new FlashProgress
                {
                    Stage = "Erasing",
                    Percentage = pct,
                    Message = $"Erasing sector {sectorsErased} / {total}"
                });
            }

            // NOT a completion marker, though it looks like one: a whole-chip
            // erase logs a bare "took <n> ms" (level 2) from jtagspi_bulk_erase,
            // but it does so UNCONDITIONALLY, before the caller inspects the
            // result - the return value is stashed, the line is logged, then the
            // value is returned. On a refused bulk erase the real order is
            //     Info : took 32000 ms
            //     Warn : Bulk flash erase failed. Falling back to sector erase.
            //     Info : sector 0 took 120 ms   ...
            // so treating it as "erase finished" would close the phase in the
            // middle of the erase and report the rest of it as writing. The
            // first page of the write is the only honest end, and it is handled
            // below for both paths.
            //
            // It IS evidence, though: it is the whole-chip erase's own measure of
            // how long it took, so it is recorded (and only recorded) here.
            var bulkTookMatch = Regex.Match(t, @"^(?:[A-Za-z]+\s*:\s*)?took\s+(\d+)\s*ms$", RegexOptions.IgnoreCase);
            if (bulkTookMatch.Success && long.TryParse(bulkTookMatch.Groups[1].Value, out long tookMs))
                bulkTookMs = tookMs;

            var pageMatch = Regex.Match(t, @"^0x([0-9a-fA-F]{8})\.$");
            if (pageMatch.Success)
            {
                if (firstPageAtMs == null)
                {
                    firstPageAtMs = sw.ElapsedMilliseconds;
                    ReportEraseEvidence(final: false);
                }
                // The first page of the write is the end of the erase. This
                // replaces a check for a "done in" line, which no OpenOCD in this
                // build ever prints - the erase was never reported as finished
                // and the "Erase complete" message was dead code.
                EndErasePhase();
                pagesWritten++;

                if (totalPages > 0)
                {
                    int pct = 55 + (int)(33.0 * pagesWritten / totalPages);
                    pct = Math.Min(pct, 88);

                    var now = DateTime.UtcNow;
                    if ((now - lastWriteUpdate).TotalMilliseconds > 200 || pagesWritten == totalPages)
                    {
                        lastWriteUpdate = now;
                        long kbWritten = (pagesWritten * 256) / 1024;
                        long kbTotal = (totalPages * 256) / 1024;
                        progress?.Report(new FlashProgress
                        {
                            Stage = "Writing",
                            Percentage = pct,
                            Message = $"Writing {kbWritten:N0} / {kbTotal:N0} KB"
                        });
                    }
                }
                return;
            }

            // v1.1.25: dropped the "Close" substring hack that previously
            // marked a run as "wrote=true" any time OpenOCD emitted a "Close"
            // log line after >=1 page report. That masked partial-write failures
            // by faking success when the write had actually died mid-page.
            // Now we ONLY trust the explicit "wrote N bytes" pattern from
            // OpenOCD, cross-checked with the success gate below (which also
            // requires ExitCode==0 or full page count reached).
            if (t.Contains("wrote", StringComparison.OrdinalIgnoreCase) && t.Contains("bytes", StringComparison.OrdinalIgnoreCase))
            {
                // Belt and braces only: this line is command_print too, so it
                // does not actually reach us on this build (which is why the
                // success gate below cannot lean on `wrote` and counts pages).
                // Left in so the phase would still close if a future OpenOCD
                // logged it.
                EndErasePhase();
                wrote = true;
                long kbWritten = (pagesWritten * 256) / 1024;
                _logService.Info($"Write complete: {kbWritten} KB ({pagesWritten} pages).");
                progress?.Report(new FlashProgress { Stage = "Written", Percentage = 90, Message = "Write complete!" });
            }

            // Real read-back mismatch from "flash verify_bank" (host-side memcmp).
            // This means the bytes on the flash do NOT match the firmware file:
            // genuine corruption, not the old checksum_memory false alarm. Must
            // surface as a failure, not be masked by the write having completed.
            if (t.Contains("contents differ", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("verify failed", StringComparison.OrdinalIgnoreCase))
            {
                verifyFailed = true;
            }

            if (t.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                _logService.Error(SanitizePath(t));
            }
        }

        var errTask = Task.Run(() =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = process.StandardError.ReadLine();
                if (line != null) ProcessLine(line, "ERR");
            }
        });

        var outTask = Task.Run(() =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = process.StandardOutput.ReadLine();
                if (line != null) ProcessLine(line, "OUT");
            }
        });

        // v1.1.25: scale the timeout with firmware size so a legitimate long
        // write on a marginal cable is not misreported as a hang. Baseline of
        // FlashTimeoutSeconds is the floor; effective is max(baseline, size/25000).
        // For a 16 MB firmware, that is ~671s. For a 4 MB firmware, ~168s so the
        // baseline (default 300s) still applies. Change reported to the user
        // in the timeout error message so they can distinguish "cable was slow"
        // from "cable was broken".
        int baselineMs = _settings.FlashTimeoutSeconds * 1000;
        int sizeScaledMs = (int)Math.Min(int.MaxValue, (long)(firmwareSize / 25000L) * 1000L);
        int effectiveTimeoutMs = Math.Max(baselineMs, sizeScaledMs);

        bool exited = process.WaitForExit(effectiveTimeoutMs);
        if (!exited)
        {
            try { process.Kill(true); } catch { }
            int effSec = effectiveTimeoutMs / 1000;
            long sizeKb = firmwareSize / 1024;
            transcript.Note($"Timed out after {effSec}s; OpenOCD killed. {pagesWritten:N0} of {totalPages:N0} pages seen.");
            ReportEraseEvidence(final: true);
            _logService.Error($"SPI flash programming timed out after {effSec}s (firmware {sizeKb} KB).");
            progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = $"Timed out after {effSec}s" });
            return new FlashResult { Success = false, ErrorMessage = $"Timed out after {effSec}s ({sizeKb} KB firmware)", Duration = sw.Elapsed };
        }

        Task.WaitAll(errTask, outTask);
        sw.Stop();

        bool success = process.ExitCode == 0;
        transcript.Note($"OpenOCD exited with code {process.ExitCode} after {sw.Elapsed.TotalSeconds:F1}s; " +
                        $"{pagesWritten:N0} of {totalPages:N0} pages seen");
        ReportEraseEvidence(final: true);

        // v1.1.25: tightened success gate. Previously any of (ExitCode==0, wrote
        // seen, "Close" substring) counted; that let partial-write failures pass
        // as success. Now: OpenOCD MUST exit with code 0, AND we must either
        // have seen an explicit "wrote N bytes" or observed all expected pages,
        // AND no read-back mismatch fired. Anything else routes to
        // ReportFlashError with actionable guidance.
        bool pagesReached = totalPages > 0 && pagesWritten >= totalPages;
        if (success && (wrote || pagesReached) && !verifyFailed)
        {
            long kbTotal = (pagesWritten * 256) / 1024;
            _logService.Info($"SPI flash complete in {sw.Elapsed.TotalSeconds:F1}s");
            _logService.Info($"  {kbTotal} KB written to SPI flash.");
            // Honest: this JTAG bridge does not return a read-back verify
            // result, so we do not claim verification. The write itself is
            // the reliable operation; power-cycle and confirm enumeration.
            _logService.Info("  Write reported complete by the flash programmer.");
            _logService.Info("Power cycle the target PC to load the new firmware.");
            progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "SPI flash programmed!" });
            return new FlashResult { Success = true, Duration = sw.Elapsed };
        }

        return ReportFlashError(allOutput.ToString(), sw.Elapsed, progress);
    }

    private FlashResult ReportFlashError(string combined, TimeSpan duration, IProgress<FlashProgress>? progress)
    {
        string errorMsg;
        string[] hints;

        // v1.1.31: "Bulk flash erase failed. Falling back to sector erase." is the
        // jtagspi driver announcing that the chip declined the whole-chip erase
        // command and is taking the slower per-sector path instead. It is not a
        // failure - the sector erase that follows normally succeeds, and that run is
        // exactly the one the Erase step reports on. The binary carries a
        // "sector-by-sector" variant of the same sentence; both open with this phrase
        // and both contain "erase failed", so the erase branch below used to read a
        // perfectly healthy chip as locked and tell the user to buy an external
        // programmer and a SOIC-8 clip - on any run that took the fallback and then
        // failed for some entirely unrelated reason. Drop the phrase before looking
        // for a genuine erase failure. Real ones still match: OpenOCD writes those as
        // "Flash sector_erase failed on sector %u" and the like, where the two words
        // sit together on their own.
        var eraseScan = combined.Replace("Bulk flash erase failed", " ",
                                         StringComparison.OrdinalIgnoreCase);

        // Order matters: specific patterns before generic ones.
        if (combined.Contains("Unknown flash device", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "SPI flash chip is not responding.";
            hints = new[]
            {
                "JTAG reached the FPGA fine, but the SPI flash chip behind it did not return a valid manufacturer ID.",
                "Causes: status-register write-protection bits got latched on the flash chip, cold or cracked solder on the flash IC, or hardware failure.",
                "This is NOT recoverable through JTAG flashing. Recovery requires an external SPI programmer such as a CH341A with a SOIC-8 chip clip directly on the flash IC.",
                "If the board is new, contact your DMA card supplier about warranty replacement."
            };
        }
        else if (combined.Contains("auto_probe failed", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("jtagspi: device not found", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "SPI flash probe failed.";
            hints = new[]
            {
                "The BSCAN bridge loaded but the flash chip did not respond to the probe.",
                "Try: reseat the DMA card in its PCIe slot, then fully power-cycle the host PC (shutdown, not restart) and retry.",
                "If it still fails, the flash chip needs external recovery via CH341A + SOIC-8 chip clip."
            };
        }
        else if (combined.Contains("contents differ", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("verify failed", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("verify mismatch", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "Verification failed after write.";
            hints = new[]
            {
                "Data was written to flash but the read-back does not match the firmware file.",
                "Causes: marginal flash chip, bad USB cable, or signal integrity issues at the JTAG connector.",
                "Try: swap the JTAG USB cable for a shorter or known-good one, then retry the flash."
            };
        }
        else if (eraseScan.Contains("flash erase failed", StringComparison.OrdinalIgnoreCase) ||
                 eraseScan.Contains("erase failed", StringComparison.OrdinalIgnoreCase) ||
                 (combined.Contains("protected", StringComparison.OrdinalIgnoreCase) &&
                  combined.Contains("sector", StringComparison.OrdinalIgnoreCase)))
        {
            errorMsg = "Flash erase blocked. Chip is write-protected.";
            hints = new[]
            {
                "The SPI flash chip has block-protection bits set in its status register.",
                "Those bits cannot be cleared through JTAG. Use an external CH341A programmer with a SOIC-8 clip to clear the protection bits, then retry from Sylnar."
            };
        }
        else if (combined.Contains("no device found", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("CH347 Open Fail", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("LIBUSB_ERROR", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "CH347 JTAG adapter not detected.";
            hints = new[]
            {
                "The USB-JTAG cable is not visible to the OS.",
                "Check: USB cable firmly plugged in at both ends, and the WCH CH347 driver is installed (Device Manager should show 'WCH CH347' under USB devices).",
                "If the driver shows a yellow warning, reinstall it via the WCH driver included with Sylnar."
            };
        }
        else if (combined.Contains("invalid JTAG IDCODE", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("IDCODE 0x00000001", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("IDCODE 0x0fffffff", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "JTAG connected but FPGA is not responding.";
            hints = new[]
            {
                "The CH347 is detected but the FPGA is not returning a valid IDCODE.",
                "Causes: DMA card not powered (PCIe slot has no power), JTAG ribbon loose on the FPGA card, or wrong JTAG voltage.",
                "Check: the DMA card is fully seated in the PCIe slot, the host PC is on, and the JTAG ribbon is firmly seated on the FPGA's JTAG header. Power-cycle and retry."
            };
        }
        else if (combined.Contains("xc7_program", StringComparison.OrdinalIgnoreCase) &&
                 (combined.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                  combined.Contains("Error", StringComparison.OrdinalIgnoreCase)))
        {
            errorMsg = "BSCAN bridge load failed.";
            hints = new[]
            {
                "Could not load the SPI flash bridge bitstream into the FPGA fabric.",
                "Causes: JTAG signal integrity (long or cheap USB cable), insufficient PCIe slot power, or FPGA fabric refusing the bitstream.",
                "Try: shorter USB cable, different PCIe slot, fully power-cycle the host PC, then retry."
            };
        }
        else if (combined.Contains("file not found", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("couldn't open", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "Firmware file not accessible.";
            hints = new[]
            {
                "The .bin or .bit file path does not exist or is locked.",
                "Check: the file was not deleted or moved, and is not currently open in another program."
            };
        }
        else if (combined.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("Timed out", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "Programming timed out.";
            hints = new[]
            {
                "The flash operation took longer than the configured timeout.",
                "Try: retry once. If it times out again, swap the JTAG USB cable for a shorter or known-good one."
            };
        }
        else if (combined.Contains("can't open", StringComparison.OrdinalIgnoreCase))
        {
            errorMsg = "Could not access JTAG adapter.";
            hints = new[]
            {
                "Another program may be holding the CH347 device open.",
                "Close other JTAG tools (Vivado, UrJTAG, openFPGALoader, etc.) and retry. If still failing, unplug and replug the USB cable."
            };
        }
        else
        {
            errorMsg = "Flash failed.";
            hints = new[]
            {
                "An unrecognized error occurred during flashing.",
                "Try: retry once, then fully power-cycle the host PC if it fails again.",
                "If the error persists, copy the error lines from the log above and share them for diagnosis."
            };
        }

        // Dump the raw error lines from OpenOCD output for diagnostics.
        foreach (var line in combined.Split('\n'))
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("can't", StringComparison.OrdinalIgnoreCase))
            {
                var sanitized = SanitizePath(t);
                _logService.Error($"  {sanitized}");
            }
        }

        _logService.Error($"Flash failed: {errorMsg}");
        foreach (var hint in hints)
            _logService.Warn(hint);

        progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = errorMsg });

        return new FlashResult { Success = false, ErrorMessage = errorMsg, Duration = duration };
    }

    // ── The SPI flash programmer process ─────────────────────────────────────
    // v1.1.32. Before this, closing the window during a flash never stopped
    // OpenOCD and never said so: Cleanup only deleted the tools folder (which
    // silently fails while openocd.exe is running out of it), and nothing else
    // held the process. So a flash the user abandoned by closing Sylnar - exactly
    // what they do when the erase is skipped - carried on erasing and writing
    // unseen, and the next attempt from a new window then drove the same JTAG
    // adapter at the same time. Two programmers interleaving commands on one BSCAN
    // bridge corrupt each other's status reads, erase and write.
    //
    // Now: MainWindow asks before closing during a flash (StopFlashWindow) and
    // stops the programmer only if the user says so; and a programmer left behind
    // by any earlier window is stopped before a new flash touches the adapter.

    private Process? _activeOpenOcd;
    private readonly object _activeOpenOcdLock = new();

    private sealed class ActiveOpenOcdRegistration : IDisposable
    {
        private readonly NativeJtagService _owner;
        private readonly Process _process;

        public ActiveOpenOcdRegistration(NativeJtagService owner, Process process)
        {
            _owner = owner;
            _process = process;
            lock (owner._activeOpenOcdLock) owner._activeOpenOcd = process;
        }

        public void Dispose()
        {
            lock (_owner._activeOpenOcdLock)
            {
                if (ReferenceEquals(_owner._activeOpenOcd, _process))
                    _owner._activeOpenOcd = null;
            }
        }
    }

    /// <summary>True while this window's SPI flash programmer is running.</summary>
    public bool IsFlashProgrammerRunning
    {
        get
        {
            Process? p;
            lock (_activeOpenOcdLock) p = _activeOpenOcd;
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Stops this window's SPI flash programmer. Only ever on the user's explicit
    /// choice: MainWindow asks first when the window is closed during a flash.
    /// It is deliberately NOT called on other exits (ProcessExit, the auto-updater's
    /// Environment.Exit): a healthy flash then still runs to completion on its own,
    /// as it always has, and StopStrayOpenOcd keeps it from colliding with the next
    /// flash if one is started before it ends.
    /// </summary>
    public void StopFlashProgrammer()
    {
        Process? p;
        lock (_activeOpenOcdLock) p = _activeOpenOcd;
        if (p == null) return;
        try
        {
            if (p.HasExited) return;
            _logService.Warn("Flash stopped at your request. The board needs a complete flash before it will boot.");
            p.Kill(true);
            p.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>
    /// Stops any openocd.exe still running out of another Sylnar tools folder
    /// (%TEMP%\nf_*): a programmer an earlier window left behind. Run before this
    /// window touches the adapter. Killing one mid-write is safe at that point,
    /// because the flash about to start erases and rewrites everything anyway.
    /// </summary>
    private void StopStrayOpenOcd()
    {
        Process[] candidates;
        try { candidates = Process.GetProcessesByName("openocd"); }
        catch { return; }

        string temp;
        try { temp = Path.GetFullPath(Path.GetTempPath()); }
        catch { return; }
        string? ownDir = null;
        try { if (_toolDir != null) ownDir = Path.GetFullPath(_toolDir).TrimEnd('\\', '/'); }
        catch { }

        foreach (var p in candidates)
        {
            using (p)
            {
                try
                {
                    string? exe = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(exe)) continue;
                    string dir = Path.GetDirectoryName(Path.GetFullPath(exe)) ?? "";
                    if (!dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Path.GetFileName(dir).StartsWith("nf_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ownDir != null && string.Equals(dir.TrimEnd('\\', '/'), ownDir, StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.HasExited) continue;

                    _logService.Warn($"A flash programmer from an earlier Sylnar window was still running (process {p.Id}). " +
                                     "It has been stopped so it cannot share the JTAG adapter with this one.");
                    p.Kill(true);
                    p.WaitForExit(5000);
                }
                catch { }
            }
        }
    }

    public void Cleanup()
    {
        if (_toolDir != null && Directory.Exists(_toolDir))
        {
            try { Directory.Delete(_toolDir, true); } catch { }
            _toolDir = null;
            _extracted = false;
        }
    }

    public void Dispose() => Cleanup();

    // v1.1.25: single sanitizer for user-visible paths in log output. Preserves
    // the basename (usually the .bin filename) so support can still see which
    // firmware was involved, while stripping the leading directory chain that
    // could leak profile paths / usernames. Previously two inline regexes at
    // different call sites did this differently (one used "[…]", the other
    // "[path]", and both dropped the basename entirely - unhelpful for triage).
    private static readonly Regex _pathWinRegex = new(@"[A-Za-z]:[/\\][^\s""']+[/\\]([^\s""'/\\]+)", RegexOptions.Compiled);
    private static readonly Regex _pathUnixRegex = new(@"/tmp/[^\s""']+/([^\s""'/]+)", RegexOptions.Compiled);
    private static readonly Regex _pathUnixTailRegex = new(@"/tmp/[^\s""'/]+(?=\s|$|["")'])", RegexOptions.Compiled);
    private static string SanitizePath(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = _pathWinRegex.Replace(s, "[path]/$1");
        s = _pathUnixRegex.Replace(s, "[path]/$1");
        s = _pathUnixTailRegex.Replace(s, "[path]");
        return s;
    }

    private static uint ReverseBits32(uint value)
    {
        uint result = 0;
        for (int i = 0; i < 32; i++)
        {
            result <<= 1;
            result |= (value >> i) & 1;
        }
        return result;
    }
}
