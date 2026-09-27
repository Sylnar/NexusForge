using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using Sylnar.Helpers;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class MainViewModel : BaseViewModel
{
    // Sidebar order: Board & Flash, Drivers, DMA Test, BAR Probe.
    public const int BoardPage = 0, DriversPage = 1, DmaPage = 2, ProbePage = 3;

    // Log filter order in the log dock: All, Warnings, Errors.
    private const int FilterAll = 0, FilterWarnings = 1, FilterErrors = 2;

    private const int MaxLogEntries = 1000;

    private readonly LogService _logService;
    private readonly AppSettings _settings;
    private readonly BoardInfoViewModel _boardInfo;
    private readonly FlashViewModel _flash;
    private readonly DriverViewModel _driver;
    private readonly DmaTestViewModel _dmaTest;
    private readonly BarProbeViewModel _barProbe;

    private int _selectedPageIndex = BoardPage;
    private int _logFilterIndex = FilterAll;
    private int _warningCount;
    private int _errorCount;
    private bool _isLogExpanded = true;
    private string _updateStatusText = "Checking updates…";
    private bool _started;

    public BoardInfoViewModel BoardInfo => _boardInfo;
    public FlashViewModel Flash => _flash;
    public DriverViewModel Driver => _driver;
    public DmaTestViewModel DmaTest => _dmaTest;
    public BarProbeViewModel BarProbe => _barProbe;

    // ── Navigation ──────────────────────────────────────────────────────────

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            // The sidebar list can report -1 while it is being rebuilt; keep a page selected.
            if (value < BoardPage || value > ProbePage) return;
            if (!SetProperty(ref _selectedPageIndex, value)) return;

            OnPropertyChanged(nameof(IsBoardPage));
            OnPropertyChanged(nameof(IsDriversPage));
            OnPropertyChanged(nameof(IsDmaPage));
            OnPropertyChanged(nameof(IsProbePage));

            if (value == ProbePage)
                _barProbe.OnShown();
        }
    }

    public bool IsBoardPage => _selectedPageIndex == BoardPage;
    public bool IsDriversPage => _selectedPageIndex == DriversPage;
    public bool IsDmaPage => _selectedPageIndex == DmaPage;
    public bool IsProbePage => _selectedPageIndex == ProbePage;

    // ── Sidebar footer ──────────────────────────────────────────────────────

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool IsElevated { get; } = Elevation.IsAdministrator();

    public string PrivilegeText => IsElevated ? "Admin" : "Not admin";

    public Tone PrivilegeTone => IsElevated ? Tone.Neutral : Tone.Warn;

    // ── Log dock ────────────────────────────────────────────────────────────

    /// <summary>Everything logged this session (last <see cref="MaxLogEntries"/>).</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>The entries that pass the current filter; this is what the dock shows.</summary>
    public ObservableCollection<LogEntry> VisibleLogEntries { get; } = new();

    public int LogCount => LogEntries.Count;

    public int LogFilterIndex
    {
        get => _logFilterIndex;
        set
        {
            if (value < FilterAll || value > FilterErrors) return;
            if (SetProperty(ref _logFilterIndex, value))
                RebuildVisibleLog();
        }
    }

    public string WarningsFilterLabel => $"Warnings {_warningCount}";
    public string ErrorsFilterLabel => $"Errors {_errorCount}";

    public bool IsLogExpanded
    {
        get => _isLogExpanded;
        set => SetProperty(ref _isLogExpanded, value);
    }

    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand ToggleLogCommand { get; }

    public MainViewModel(
        LogService logService,
        AppSettings settings,
        BoardInfoViewModel boardInfo,
        FlashViewModel flash,
        DriverViewModel driver,
        DmaTestViewModel dmaTest,
        BarProbeViewModel barProbe)
    {
        _logService = logService;
        _settings = settings;
        _boardInfo = boardInfo;
        _flash = flash;
        _driver = driver;
        _dmaTest = dmaTest;
        _barProbe = barProbe;

        ClearLogCommand = new RelayCommand(ClearLog);
        CopyLogCommand = new AsyncRelayCommand(CopyLogAsync);
        ToggleLogCommand = new RelayCommand(() => IsLogExpanded = !IsLogExpanded);

        _logService.LogAdded += OnLogAdded;
        _logService.Info($"Sylnar v{_settings.Version} started");
    }

    /// <summary>
    /// Called once when the main window first appears. Checks both USB drivers
    /// in the background so the sidebar's hardware card shows real status.
    /// </summary>
    public void OnWindowOpened()
    {
        if (_started) return;
        _started = true;
        _ = _driver.CheckBothAsync();
    }

    public void SetUpdateOutcome(UpdateCheckOutcome outcome)
    {
        UpdateStatusText = outcome switch
        {
            UpdateCheckOutcome.UpToDate => "Up to date",
            UpdateCheckOutcome.UpdateAvailable => "Update available",
            UpdateCheckOutcome.Failed => "Update check failed",
            _ => "Checking updates…"
        };
    }

    private void OnLogAdded(object? sender, LogEntry entry)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(entry);
            AdjustCounts(entry, +1);
            if (Matches(entry))
                VisibleLogEntries.Add(entry);

            if (LogEntries.Count > MaxLogEntries)
            {
                var oldest = LogEntries[0];
                LogEntries.RemoveAt(0);
                AdjustCounts(oldest, -1);
                if (VisibleLogEntries.Count > 0 && ReferenceEquals(VisibleLogEntries[0], oldest))
                    VisibleLogEntries.RemoveAt(0);
            }

            OnPropertyChanged(nameof(LogCount));
        });
    }

    private bool Matches(LogEntry entry) => _logFilterIndex switch
    {
        FilterWarnings => entry.Level == "WARN",
        FilterErrors => entry.Level == "ERROR",
        _ => true
    };

    private void AdjustCounts(LogEntry entry, int delta)
    {
        if (entry.Level == "WARN")
        {
            _warningCount += delta;
            OnPropertyChanged(nameof(WarningsFilterLabel));
        }
        else if (entry.Level == "ERROR")
        {
            _errorCount += delta;
            OnPropertyChanged(nameof(ErrorsFilterLabel));
        }
    }

    private void RebuildVisibleLog()
    {
        VisibleLogEntries.Clear();
        foreach (var entry in LogEntries)
            if (Matches(entry))
                VisibleLogEntries.Add(entry);
    }

    private void ClearLog()
    {
        LogEntries.Clear();
        VisibleLogEntries.Clear();
        _warningCount = 0;
        _errorCount = 0;
        OnPropertyChanged(nameof(LogCount));
        OnPropertyChanged(nameof(WarningsFilterLabel));
        OnPropertyChanged(nameof(ErrorsFilterLabel));
    }

    /// <summary>Copies what the dock currently shows (respects the filter).</summary>
    private async Task CopyLogAsync()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var entry in VisibleLogEntries)
                sb.AppendLine(entry.Formatted);

            var text = sb.ToString();
            if (string.IsNullOrEmpty(text)) return;

            var desktop = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop?.MainWindow);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                _logService.Info($"Copied {VisibleLogEntries.Count} log entries to clipboard.");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to copy log: {ex.Message}");
        }
    }
}
