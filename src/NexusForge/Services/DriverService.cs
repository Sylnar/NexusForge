using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Sylnar.Helpers;
using Sylnar.Models;

namespace Sylnar.Services;

public class DriverService
{
    private const string VID = "1A86";
    private const string PID = "55DD";

    private static readonly string[] DriverFiles =
    {
        "CH341WDM.INF",
        "CH341WDM.CAT",
        "CH341WDM.SYS",
        "CH341W64.SYS",
        "CH341M64.SYS",
        "CH341DLL.DLL",
        "CH341DLLA64.DLL",
        "CH347DLL.DLL",
        "CH347DLLA64.DLL",
    };

    private readonly LogService _log;

    public DriverService(LogService log) => _log = log;

    public async Task<DriverInfo> CheckDriverAsync()
    {
        var info = new DriverInfo();

        _log.Info("Checking CH347 driver status...");

        const string script = @"
$devs = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VID_1A86.*PID_55DD' }

if (-not $devs -or ($devs | Measure-Object).Count -eq 0) {
    Write-Output 'NOT_FOUND'
    exit
}

# Check each device
$anyOK = $false
foreach ($d in $devs) {
    $ver  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_DriverVersion' -ErrorAction SilentlyContinue).Data
    $inf  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
    $desc = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_DriverDesc' -ErrorAction SilentlyContinue).Data
    $svc  = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
             -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data

    Write-Output ('DEV_STATUS='  + $d.Status)
    Write-Output ('DEV_NAME='    + $d.FriendlyName)
    Write-Output ('DEV_CLASS='   + $d.Class)
    Write-Output ('DEV_VERSION=' + $ver)
    Write-Output ('DEV_INF='     + $inf)
    Write-Output ('DEV_DESC='    + $desc)
    Write-Output ('DEV_SERVICE=' + $svc)
    Write-Output '---'
}

# Also check if CH341 kernel service exists
$svcObj = Get-Service -Name 'CH341_A64' -ErrorAction SilentlyContinue
if ($svcObj) {
    Write-Output ('KERNEL_SVC_STATUS=' + $svcObj.Status)
} else {
    $svcObj = Get-Service -Name 'CH341' -ErrorAction SilentlyContinue
    if ($svcObj) {
        Write-Output ('KERNEL_SVC_STATUS=' + $svcObj.Status)
    } else {
        Write-Output 'KERNEL_SVC_STATUS=NotFound'
    }
}
";
        var (_, output, _) = await RunPowerShellAsync(script);

        if (output.Contains("NOT_FOUND"))
        {
            _log.Warn("CH347 USB device not found — is the JTAG USB cable connected?");
            info.IsDeviceDetected = false;
            info.IsDriverOk       = false;
            info.Status           = "Not Connected";
            return info;
        }

        info.IsDeviceDetected = true;

        var entries = output.Split("---", StringSplitOptions.RemoveEmptyEntries);
        bool wchDriverFound = false;
        string bestInf = "", bestVer = "", bestName = "", bestDesc = "", bestSvc = "";
        string worstStatus = "";
        bool anyChildWithoutDriver = false;

        foreach (var entry in entries)
        {
            if (entry.Contains("KERNEL_SVC")) continue;

            string Get(string key) =>
                Regex.Match(entry, $@"(?m)^{key}=(.*)$").Groups[1].Value.Trim();

            var st   = Get("DEV_STATUS");
            var nm   = Get("DEV_NAME");
            var vr   = Get("DEV_VERSION");
            var inf  = Get("DEV_INF");
            var desc = Get("DEV_DESC");
            var svc  = Get("DEV_SERVICE");

            bool isOk = st.Equals("OK", StringComparison.OrdinalIgnoreCase);

            bool isGenericUsb = inf.Equals("usb.inf", StringComparison.OrdinalIgnoreCase) ||
                                svc.Equals("usbccgp", StringComparison.OrdinalIgnoreCase) ||
                                svc.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                                nm.Contains("USB Composite", StringComparison.OrdinalIgnoreCase);

            if (isGenericUsb) continue;

            bool hasCh341Svc = svc.Contains("CH341", StringComparison.OrdinalIgnoreCase);
            bool hasCh341Inf = inf.Contains("ch341", StringComparison.OrdinalIgnoreCase) ||
                               inf.Contains("ch347", StringComparison.OrdinalIgnoreCase);

            if (isOk && (hasCh341Svc || hasCh341Inf))
            {
                wchDriverFound = true;
                bestVer  = !string.IsNullOrEmpty(vr) ? vr : bestVer;
                bestInf  = !string.IsNullOrEmpty(inf) ? inf : bestInf;
                bestName = !string.IsNullOrEmpty(nm) ? nm : bestName;
                bestDesc = !string.IsNullOrEmpty(desc) ? desc : bestDesc;
                bestSvc  = !string.IsNullOrEmpty(svc) ? svc : bestSvc;
            }
            else if (!isOk)
            {
                anyChildWithoutDriver = true;
                if (string.IsNullOrEmpty(worstStatus)) worstStatus = st;
            }
        }

        var kernelMatch = Regex.Match(output, @"KERNEL_SVC_STATUS=(\w+)");
        string kernelSvc = kernelMatch.Success ? kernelMatch.Groups[1].Value : "NotFound";

        info.InfPath    = bestInf;
        info.Version    = !string.IsNullOrEmpty(bestVer) ? bestVer : "—";
        info.DeviceName = !string.IsNullOrEmpty(bestName) ? bestName : "CH347 USB Device";

        if (!string.IsNullOrEmpty(bestSvc) && bestSvc.Contains("CH341", StringComparison.OrdinalIgnoreCase))
            info.DriverType = "WCH CH341/CH347 WDM";
        else if (!string.IsNullOrEmpty(bestInf))
            info.DriverType = Path.GetFileName(bestInf);
        else if (!string.IsNullOrEmpty(bestDesc))
            info.DriverType = bestDesc;
        else
            info.DriverType = "—";

        if (wchDriverFound)
        {
            info.IsDriverOk = true;
            info.Status = "Installed";
            _log.Info("CH347 driver: Installed");
            _log.Info($"  Device:  {info.DeviceName}");
            _log.Info($"  Version: {info.Version}");
            _log.Info($"  Type:    {info.DriverType}");
        }
        else
        {
            info.IsDriverOk = false;
            info.Status = "Driver Missing";
            _log.Error("CH347 WCH driver is NOT installed.");
            _log.Error("  The USB device is connected but the JTAG driver is missing.");
            _log.Error("  Click 'Install Driver' to install the bundled WCH driver.");
        }

        return info;
    }

    public async Task<bool> InstallDriverAsync()
    {
        _log.Info("Installing WCH CH341/CH347 driver...");

        var driverDir = Path.Combine(Path.GetTempPath(), $"drv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(driverDir);

        try
        {
            _log.Info("Preparing driver package...");

            var assembly = Assembly.GetExecutingAssembly();
            int extracted = 0;

            foreach (var fileName in DriverFiles)
            {
                if (ResourceCrypto.ExtractResource(assembly, fileName, Path.Combine(driverDir, fileName)))
                    extracted++;
            }

            if (extracted < 3)
            {
                _log.Error("Driver package is incomplete. Cannot install.");
                return false;
            }

            _log.Info($"Driver package ready ({extracted} files).");

            _log.Info("Adding driver to Windows driver store...");

            var infPath = Path.Combine(driverDir, "CH341WDM.INF");
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
                    _log.Info("Driver added to store successfully.");
                else
                    _log.Warn($"Driver store returned code {proc.ExitCode} (may already exist).");
            }

            _log.Info("Binding driver to hardware...");
            var scanProc = Process.Start(new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = "/scan-devices",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            });
            if (scanProc != null)
            {
                await scanProc.StandardOutput.ReadToEndAsync();
                await scanProc.WaitForExitAsync();
            }

            _log.Info("Waiting for driver to initialize...");
            await Task.Delay(3000);

            var check = await CheckDriverAsync();
            if (check.IsDriverOk)
            {
                _log.Info("CH347 driver installed and active!");
                return true;
            }

            _log.Warn("Driver installed but not yet bound to device.");
            _log.Warn("Replug the USB cable, then click 'Check Driver'.");
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
        _log.Info("Uninstalling WCH CH341/CH347 driver...");

        string infFileName = string.Empty;
        if (!string.IsNullOrWhiteSpace(infPath))
            infFileName = Path.GetFileName(infPath).Trim();

        if (string.IsNullOrEmpty(infFileName) ||
            !infFileName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("Looking up driver registration...");
            const string findInfScript = @"
$devs = Get-PnpDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match 'VID_1A86.*PID_55DD' }
foreach ($d in $devs) {
    $inf = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
            -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue).Data
    if ($inf -and $inf -match '^oem') {
        Write-Output ('INF=' + $inf)
        break
    }
}
";
            var (_, qOut, _) = await RunPowerShellAsync(findInfScript);
            var m = Regex.Match(qOut, @"INF=(.+)");
            if (m.Success)
                infFileName = Path.GetFileName(m.Groups[1].Value.Trim());
        }

        if (!string.IsNullOrEmpty(infFileName) &&
            infFileName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("Removing driver from Windows...");

            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName               = "pnputil.exe",
                    Arguments              = $"/delete-driver {infFileName} /uninstall /force",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                });

                if (proc != null)
                {
                    await proc.StandardOutput.ReadToEndAsync();
                    await proc.WaitForExitAsync();

                    if (proc.ExitCode == 0)
                    {
                        _log.Info("CH347 driver removed successfully.");
                        _log.Warn("Replug the USB cable, then click 'Check Driver'.");
                        await Task.Delay(1000);
                        await CheckDriverAsync();
                        return true;
                    }

                    _log.Warn("Driver removal may have partially succeeded. Click 'Check Driver' to verify.");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Uninstall failed: {ex.Message}");
            }
        }
        else
        {
            _log.Warn("Could not find OEM INF file for the CH347 driver.");
            _log.Info("The driver may not be installed. Opening Device Manager...");
            try
            {
                Process.Start(new ProcessStartInfo("devmgmt.msc")
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        return false;
    }

    private static async Task<(int exitCode, string output, string error)> RunPowerShellAsync(
        string script, int timeoutMs = 15000)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"nf_drv_{Guid.NewGuid():N}.ps1");
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
        if (!exited) { try { proc.Kill(true); } catch { } }

        try { File.Delete(scriptFile); } catch { }

        return (proc.ExitCode, sb.ToString(), sbe.ToString());
    }
}
