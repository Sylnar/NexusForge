namespace Sylnar.Models;

public enum UpdateStage
{
    Found,
    Downloading,
    Applying
}

public class UpdateProgress
{
    public UpdateStage Stage { get; init; }
    public string Version { get; init; } = "";
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
}
