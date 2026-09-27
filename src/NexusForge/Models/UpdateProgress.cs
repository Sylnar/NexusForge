namespace Sylnar.Models;

public enum UpdateStage
{
    Found,
    Downloading,
    Applying
}

/// <summary>Result of the launch-time update check, for the sidebar footer.</summary>
public enum UpdateCheckOutcome
{
    Pending,
    UpToDate,
    UpdateAvailable,
    Failed
}

public class UpdateProgress
{
    public UpdateStage Stage { get; init; }
    public string Version { get; init; } = "";
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
}
