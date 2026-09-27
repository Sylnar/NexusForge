namespace Sylnar.Models;

public class BoardInfo
{
    public bool IsDetected { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public string IdCode { get; set; } = string.Empty;
    public string Dna { get; set; } = string.Empty;
    public string DnaFormatted { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public string JtagCable { get; set; } = string.Empty;
    /// <summary>BSCAN SPI bridge used to flash this part, or empty if it has none.</summary>
    public string Bridge { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
}
