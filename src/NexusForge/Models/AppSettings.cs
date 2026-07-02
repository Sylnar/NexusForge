using NexusForge.Helpers;

namespace NexusForge.Models;

public class AppSettings
{
    /// <summary>
    /// Version string, always matches the running binary. Backed by the shared
    /// <see cref="VersionInfo"/> helper (v1.1.25); previously duplicated a
    /// private ResolveAssemblyVersion() method here.
    /// </summary>
    public string Version { get; set; } = VersionInfo.Value;

    public string FpgaPart { get; set; } = "xc7a75tfgg484";
    public string SpiFlashPart { get; set; } = "is25lp128f";
    public string ExpectedIdCode { get; set; } = "0x0362d093";
    public int FlashTimeoutSeconds { get; set; } = 300;
    public string LastFirmwarePath { get; set; } = string.Empty;
    public string LogLevel { get; set; } = "Info";
}
