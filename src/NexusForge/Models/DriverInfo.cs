namespace Sylnar.Models;

public class DriverInfo
{
    public bool IsDeviceDetected { get; set; }
    public bool IsDriverOk      { get; set; }

    public string Status     { get; set; } = "Unknown";
    public string Version    { get; set; } = "—";
    public string DeviceName { get; set; } = "—";
    public string DriverType { get; set; } = "—";
    public string InfPath    { get; set; } = string.Empty;

    public string VidPid { get; set; } = "1A86:55DD";
}
