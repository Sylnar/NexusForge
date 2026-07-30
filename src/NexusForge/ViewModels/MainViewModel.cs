using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly LogService _logService;
    private readonly AppSettings _settings;
    private readonly BoardInfoViewModel _boardInfo;
    private readonly FlashViewModel _flash;
    private readonly DriverViewModel _driver;
    private readonly DmaTestViewModel _dmaTest;
    private readonly BarProbeViewModel _barProbe;

    private string _statusBarText = "";

    public BoardInfoViewModel BoardInfo => _boardInfo;
    public FlashViewModel Flash => _flash;
    public DriverViewModel Driver => _driver;
    public DmaTestViewModel DmaTest => _dmaTest;
    public BarProbeViewModel BarProbe => _barProbe;

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public string StatusBarText
    {
        get => _statusBarText;
        set => SetProperty(ref _statusBarText, value);
    }

    public int LogCount => LogEntries.Count;

    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogCommand { get; }

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

        _statusBarText = $"Sylnar v{_settings.Version}  ·  DMA FPGA Management Tool";

        ClearLogCommand = new RelayCommand(ClearLog);
        CopyLogCommand = new AsyncRelayCommand(CopyLogAsync);

        _logService.LogAdded += OnLogAdded;
        _logService.Info($"Sylnar v{_settings.Version} started");
    }

    private void OnLogAdded(object? sender, LogEntry entry)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogEntries.Add(entry);
            if (LogEntries.Count > 1000)
                LogEntries.RemoveAt(0);
            OnPropertyChanged(nameof(LogCount));
        });
    }

    private void ClearLog()
    {
        LogEntries.Clear();
        OnPropertyChanged(nameof(LogCount));
    }

    private async Task CopyLogAsync()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var entry in LogEntries)
                sb.AppendLine(entry.Formatted);

            var text = sb.ToString();
            if (string.IsNullOrEmpty(text)) return;

            var desktop = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop?.MainWindow);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(text);
                _logService.Info($"Copied {LogEntries.Count} log entries to clipboard.");
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to copy log: {ex.Message}");
        }
    }
}
