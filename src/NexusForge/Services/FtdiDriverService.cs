using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Sylnar.Helpers;
using Sylnar.Models;

namespace Sylnar.Services;

public class FtdiDriverService
{
    private const string VID = "0403";
    private const string PID = "601F";

    private static readonly string[] FtdiDriverFiles = { "FTD3XXWU.Inf", "FTD3XXWU.cat" };

    private readonly LogService _log;

    public FtdiDriverService(LogService log) => _log = log;

    public async Task<DriverInfo> CheckDriverAsync()
    {
        var info = new DriverInfo { VidPid = $"{VID}:{PID}" };
        _log.Info("Checking FTDI FT601 SuperSpeed driver...");

        const string script = @"
$devs = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VID_0403.*PID_601F' }

if (-not $devs -or ($devs | Measure-Object).Count -eq 0) {
    Write-Output 'NOT_FOUND'
    exit
}

foreach ($d in $devs) {
    $ver  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_DriverVersion' -ErrorAction SilentlyContinue).Data
    $inf  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
    $svc  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data
    $desc = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_DriverDesc' -ErrorAction SilentlyContinue).Data

    Write-Output ('DEV_STATUS='  + $d.Status)
    Write-Output ('DEV_NAME='    + $d.FriendlyName)
    Write-Output ('DEV_VERSION=' + $ver)
    Write-Output ('DEV_INF='     + $inf)
    Write-Output ('DEV_SERVICE=' + $svc)
    Write-Output ('DEV_DESC='    + $desc)
    Write-Output '---'
}
";
        var (_, output, _) = await RunPsAsync(script);

        if (output.Contains("NOT_FOUND"))
        {
            _log.Warn("FTDI FT601 not found. Is the DATA USB 3.0 cable connected?");
            info.IsDeviceDetected = false;
            info.IsDriverOk = false;
            info.Status = "Not Connected";
            return info;
        }

        info.IsDeviceDetected = true;

        bool hasCorrectDriver = false;
        bool hasOldDriver = false;

        foreach (var entry in output.Split("---", StringSplitOptions.RemoveEmptyEntries))
        {
            string Get(string key) =>
                Regex.Match(entry, $@"(?m)^{key}=(.*)$").Groups[1].Value.Trim();

            var st   = Get("DEV_STATUS");
            var nm   = Get("DEV_NAME");
            var vr   = Get("DEV_VERSION");
            var inf  = Get("DEV_INF");
            var svc  = Get("DEV_SERVICE");
            var desc = Get("DEV_DESC");

            if (inf.Equals("usb.inf", StringComparison.OrdinalIgnoreCase)) continue;

            bool isOk = st.Equals("OK", StringComparison.OrdinalIgnoreCase);

            bool isWinUsb = svc.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) ||
                            svc.Equals("WUDFRd", StringComparison.OrdinalIgnoreCase) ||
                            nm.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("SuperSpeed", StringComparison.OrdinalIgnoreCase);

            bool isOldFtdi = svc.Contains("FTDIBUS", StringComparison.OrdinalIgnoreCase) ||
                             svc.Contains("FTD3XX", StringComparison.OrdinalIgnoreCase);

            if (isOk && isWinUsb)
            {
                hasCorrectDriver = true;
                info.Version = !string.IsNullOrEmpty(vr) ? vr : info.Version;
                info.DeviceName = !string.IsNullOrEmpty(nm) ? nm : "FTDI SuperSpeed-FIFO Bridge";
                info.InfPath = inf;
                info.DriverType = "WinUSB (SuperSpeed)";
            }
            else if (isOk && isOldFtdi)
            {
                hasOldDriver = true;
                if (!hasCorrectDriver)
                {
                    info.Version = !string.IsNullOrEmpty(vr) ? vr : info.Version;
                    info.DeviceName = !string.IsNullOrEmpty(nm) ? nm : "FTDI FT601";
                    info.InfPath = inf;
                    info.DriverType = svc + " (OLD — needs update)";
                }
            }
            else if (isOk && !string.IsNullOrEmpty(svc))
            {
                if (!hasCorrectDriver && !hasOldDriver)
                {
                    info.Version = !string.IsNullOrEmpty(vr) ? vr : info.Version;
                    info.DeviceName = !string.IsNullOrEmpty(nm) ? nm : "FTDI FT601";
                    info.InfPath = inf;
                    info.DriverType = svc;
                }
            }
        }

        if (string.IsNullOrEmpty(info.DeviceName) || info.DeviceName == "—")
            info.DeviceName = "FTDI FT601";

        if (hasCorrectDriver)
        {
            info.IsDriverOk = true;
            info.Status = "Installed";
            _log.Info("FTDI FT601 SuperSpeed driver: Installed");
            _log.Info($"  Device:  {info.DeviceName}");
            _log.Info($"  Version: {info.Version}");
            _log.Info($"  Type:    {info.DriverType}");
        }
        else if (hasOldDriver)
        {
            info.IsDriverOk = false;
            info.Status = "Wrong Driver";
            _log.Warn("FTDI FT601 has the OLD driver (FTDIBUS3).");
            _log.Warn("  LeechCore v2.22+ requires the WinUSB SuperSpeed driver (v1.4.x).");
            _log.Error("  Click 'Install Driver' to update to the correct WinUSB driver.");
        }
        else
        {
            info.IsDriverOk = false;
            info.Status = "Driver Missing";
            _log.Error("FTDI FT601 driver NOT installed.");
            _log.Error("  Click 'Install Driver' to install the WinUSB SuperSpeed driver.");
        }

        return info;
    }

    public async Task<bool> InstallDriverAsync()
    {
        _log.Info("Installing FTDI FT601 WinUSB SuperSpeed driver...");

        var driverDir = OwnedTempDirs.Register(Path.Combine(Path.GetTempPath(), $"drv_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(driverDir);

        try
        {
            _log.Info("Preparing driver package...");
            var assembly = Assembly.GetExecutingAssembly();
            int extracted = 0;

            foreach (var fileName in FtdiDriverFiles)
            {
                if (ResourceCrypto.ExtractResource(assembly, fileName, Path.Combine(driverDir, fileName)))
                    extracted++;
            }

            if (extracted < 2)
            {
                _log.Error("Driver package incomplete.");
                return false;
            }

            _log.Info($"Driver package ready ({extracted} files).");

            var infPath = Path.Combine(driverDir, "FTD3XXWU.Inf");
            _log.Info("Adding driver to Windows...");

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = $"/add-driver \"{infPath}\" /install",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            });

            if (proc != null)
            {
                await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (proc.ExitCode == 0 || proc.ExitCode == 259)
                    _log.Info("Driver added to store.");
                else
                    _log.Warn($"pnputil returned code {proc.ExitCode} (may already exist).");
            }

            _log.Info("Binding driver to hardware...");
            var scanProc = Process.Start(new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = "/scan-devices",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            });
            if (scanProc != null) await scanProc.WaitForExitAsync();

            _log.Info("Waiting for driver to initialize...");
            await Task.Delay(3000);

            var check = await CheckDriverAsync();
            if (check.IsDriverOk)
            {
                _log.Info("FTDI SuperSpeed driver installed and active!");
                return true;
            }

            _log.Warn("Driver installed but not yet bound.");
            _log.Warn("Replug the DATA USB cable, then click Check Driver.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Installation failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(driverDir, true); } catch { }
        }
    }

    public async Task<bool> UninstallDriverAsync(string infPath)
    {
        _log.Info("Uninstalling FTDI driver...");

        string infFileName = "";
        if (!string.IsNullOrWhiteSpace(infPath))
            infFileName = Path.GetFileName(infPath).Trim();

        if (string.IsNullOrEmpty(infFileName) ||
            !infFileName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
        {
            const string findScript = @"
$devs = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VID_0403.*PID_601F' }
foreach ($d in $devs) {
    $inf = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
            -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
    if ($inf -and $inf -match '^oem') { Write-Output ('INF=' + $inf); break }
}
";
            var (_, qOut, _) = await RunPsAsync(findScript);
            var m = Regex.Match(qOut, @"INF=(.+)");
            if (m.Success) infFileName = Path.GetFileName(m.Groups[1].Value.Trim());
        }

        if (!string.IsNullOrEmpty(infFileName) &&
            infFileName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("Removing driver...");
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = $"/delete-driver {infFileName} /uninstall /force",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    _log.Info("FTDI driver removed.");
                    _log.Warn("Replug the DATA USB cable, then click Check Driver.");
                    return true;
                }
            }
        }
        else
        {
            _log.Warn("No OEM INF found. Opening Device Manager...");
            try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); }
            catch { }
        }
        return false;
    }

    private static async Task<(int exitCode, string output, string error)> RunPsAsync(
        string script, int timeoutMs = 15000)
    {
        var sf = Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(sf, script);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{sf}\"",
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var proc = new Process { StartInfo = psi };
        var sb = new System.Text.StringBuilder();
        var sbe = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) sbe.AppendLine(e.Data); };
        proc.Start(); proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
        var exited = await Task.Run(() => proc.WaitForExit(timeoutMs));
        if (!exited) { try { proc.Kill(true); } catch { } }
        try { File.Delete(sf); } catch { }
        return (proc.ExitCode, sb.ToString(), sbe.ToString());
    }
}
