namespace Sylnar.ViewModels;

/// <summary>One segment of the flash step bar (Bridge · Erase · Write · Done).</summary>
public sealed class FlashStep : BaseViewModel
{
    private double _progress;
    private bool _isActive;
    private bool _isDone;
    private bool _isFailed;

    public FlashStep(string label) => Label = label;

    public string Label { get; }

    /// <summary>Fill of this step's bar, 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool IsDone
    {
        get => _isDone;
        private set => SetProperty(ref _isDone, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        private set => SetProperty(ref _isFailed, value);
    }

    public void SetPending() => Set(0, active: false, done: false, failed: false);

    public void SetActive(double fraction) => Set(Math.Clamp(fraction, 0, 1), active: true, done: false, failed: false);

    public void SetDone() => Set(1, active: false, done: true, failed: false);

    public void SetFailed() => Set(Math.Max(Progress, 0.15), active: true, done: false, failed: true);

    private void Set(double progress, bool active, bool done, bool failed)
    {
        Progress = progress;
        IsActive = active;
        IsDone = done;
        IsFailed = failed;
    }
}
