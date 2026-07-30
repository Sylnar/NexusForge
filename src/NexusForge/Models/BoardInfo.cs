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
    public DateTime DetectedAt { get; set; }
}
