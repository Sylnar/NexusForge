using Sylnar.Helpers;

namespace Sylnar.Models;

public class AppSettings
{
    /// <summary>
    /// Version string, always matches the running binary. Backed by the shared
    /// <see cref="VersionInfo"/> helper (v1.1.25); previously duplicated a
    /// private ResolveAssemblyVersion() method here.
    /// </summary>
    public string Version { get; set; } = VersionInfo.Value;

    public string FpgaPart { get; set; } = "xc7a75tfgg484";
    public int FlashTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Firmware file picked last time, restored on the Flash tab at startup.
    /// Persisted as plain text next to the crash log (%LocalAppData%\Sylnar).
    /// </summary>
    public string LastFirmwarePath
    {
        get
        {
            try { return File.Exists(LastFirmwareFile) ? File.ReadAllText(LastFirmwareFile).Trim() : string.Empty; }
            catch { return string.Empty; }
        }
        set
        {
            try
            {
                if (string.IsNullOrEmpty(CrashLogger.LogDirectory)) return;
                File.WriteAllText(LastFirmwareFile, value ?? string.Empty);
            }
            catch { /* remembering the path is a convenience only */ }
        }
    }

    private static string LastFirmwareFile =>
        Path.Combine(string.IsNullOrEmpty(CrashLogger.LogDirectory) ? Path.GetTempPath() : CrashLogger.LogDirectory,
                     "last_firmware.txt");
}
