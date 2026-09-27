using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class DmaTestViewModel : BaseViewModel
{
    private const int StressTest = 3;

    private static readonly string[] TestNames = { "Full", "Latency", "Throughput", "Stress" };
    private static readonly string[] SpeedLabels =
        { "Reads / sec", "Throughput", "Avg read", "Min read", "Max read", "Failed reads" };
    private static readonly string[] StressLabels =
        { "Reads / sec", "Duration", "Total reads", "Fail rate", "Max fail streak", "Failed reads" };

    private readonly DmaTestService _dmaTestService;
    private readonly LogService _logService;
    private readonly AsyncRelayCommand _runSpeedTestCommand;
    private readonly AsyncRelayCommand _generateMmapCommand;
    private readonly AsyncRelayCommand _deployToToolsCommand;

    // ── Test selection ──────────────────────────────────────────────────────

    private int _selectedTestType;
    public int SelectedTestType
    {
        get => _selectedTestType;
        set
        {
            if (value < 0 || value >= TestNames.Length) return;
            if (SetProperty(ref _selectedTestType, value))
                OnPropertyChanged(nameof(IsStressTestSelected));
        }
    }

    // Stress duration in minutes; shown only when Stress is selected.
    private int _stressMinutes = 5;
    public int StressMinutes { get => _stressMinutes; set => SetProperty(ref _stressMinutes, Math.Clamp(value, 1, 60)); }
    public bool IsStressTestSelected => _selectedTestType == StressTest;

    // ── Run state and results ───────────────────────────────────────────────

    private bool _isTesting;
    private bool _hasResults;
    private int _testPercentage;
    private string _testMessage = "";
    private int _runType;
    private DateTime _runFinishedAt;
    private TimeSpan _runDuration;
    private string _rating = "";
    private long _failedReads;
    private bool _runFailed;

    public bool IsTesting
    {
        get => _isTesting;
        set
        {
            if (!SetProperty(ref _isTesting, value)) return;
            _runSpeedTestCommand?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(RunButtonText));
            RaiseRunChanged();
        }
    }

    public bool HasResults
    {
        get => _hasResults;
        set
        {
            if (SetProperty(ref _hasResults, value))
                RaiseRunChanged();
        }
    }

    public int TestPercentage
    {
        get => _testPercentage;
        set
        {
            if (SetProperty(ref _testPercentage, value))
                OnPropertyChanged(nameof(StatusSummary));
        }
    }

    public string TestMessage
    {
        get => _testMessage;
        set
        {
            if (SetProperty(ref _testMessage, value))
                OnPropertyChanged(nameof(RunMeta));
        }
    }

    public string RunButtonText => IsTesting ? "Running…" : "Run test";

    public ObservableCollection<MetricTile> Metrics { get; } = new(SpeedLabels.Select(l => new MetricTile(l)));

    /// <summary>"LAST RUN · FULL TEST", "RUNNING · STRESS TEST" or "NO RUNS YET".</summary>
    public string RunTitle =>
        IsTesting ? $"RUNNING · {TestNames[_runType].ToUpperInvariant()} TEST"
        : HasResults ? $"LAST RUN · {TestNames[_runType].ToUpperInvariant()} TEST"
        : "NO RUNS YET";

    public string RunMeta =>
        IsTesting ? TestMessage
        : !HasResults ? "Pick a test type, then press Run test"
        : _runFailed ? $"Stopped {_runFinishedAt:HH:mm:ss} · see the log"
        : $"Completed {_runFinishedAt:HH:mm:ss} · {FormatDuration(_runDuration)}";

    public string RatingText =>
        IsTesting ? "TESTING"
        : !HasResults ? "—"
        : _runFailed ? "FAIL"
        : _rating;

    public Tone RatingTone => IsTesting || !HasResults ? Tone.Neutral
        : _runFailed ? Tone.Danger
        : _rating switch
        {
            "PERFECT" => Tone.Ok,
            "EXCELLENT" => Tone.Info,
            "GOOD" => Tone.Warn,
            "FAIL" => Tone.Danger,
            _ => Tone.Neutral
        };

    public string StatusSummary =>
        IsTesting ? $"{TestPercentage}%"
        : !HasResults ? "Not tested"
        : _runFailed ? "FAIL · test did not complete"
        : $"{(_rating == "FAIL" ? "FAIL" : "PASS")} · {_failedReads:N0} failed {(_failedReads == 1 ? "read" : "reads")}";

    // Rating scale under the tiles. Latency/throughput tests share one scale;
    // the stress test is graded by duration without failures instead.
    public bool ShowSpeedScale => !(HasResults && _runType == StressTest);
    public bool ShowStressScale => !ShowSpeedScale;
    private string ScaleRating => HasResults && !_runFailed && !IsTesting ? _rating : "";
    public bool IsScaleAcceptable => ScaleRating == "ACCEPTABLE";
    public bool IsScaleGood => ScaleRating == "GOOD";
    public bool IsScaleExcellent => ScaleRating == "EXCELLENT";
    public bool IsScalePerfect => ScaleRating == "PERFECT";

    // ── Memory map ──────────────────────────────────────────────────────────

    private bool _isGeneratingMmap;
    private string _mmapStatus = "";
    private Tone _mmapStatusTone;
    private string _mmapStateText = "";
    private string _mmapAgeText = "";
    private Tone _mmapStateTone;
    private string _generateMmapLabel = "Regenerate map";

    public bool IsGeneratingMmap
    {
        get => _isGeneratingMmap;
        set
        {
            if (!SetProperty(ref _isGeneratingMmap, value)) return;
            _generateMmapCommand?.NotifyCanExecuteChanged();
            _deployToToolsCommand?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsMmapBusy));
        }
    }

    public string MmapStatus { get => _mmapStatus; set => SetProperty(ref _mmapStatus, value); }
    public Tone MmapStatusTone { get => _mmapStatusTone; set => SetProperty(ref _mmapStatusTone, value); }

    /// <summary>"Cached" or "Not generated".</summary>
    public string MmapStateText { get => _mmapStateText; private set => SetProperty(ref _mmapStateText, value); }
    /// <summary>"· 2 h old".</summary>
    public string MmapAgeText { get => _mmapAgeText; private set => SetProperty(ref _mmapAgeText, value); }
    public Tone MmapStateTone { get => _mmapStateTone; private set => SetProperty(ref _mmapStateTone, value); }
    public string GenerateMmapLabel { get => _generateMmapLabel; private set => SetProperty(ref _generateMmapLabel, value); }

    /// <summary>Where the cached map lives, with the profile folder shortened.</summary>
    public string MmapPathText { get; } = ShortenAppData(DmaTestService.MmapCachePath);

    // Deploy to DMA tools
    private bool _isDeploying;
    private string _deployStatus = "";
    private Tone _deployStatusTone;

    public bool IsDeploying
    {
        get => _isDeploying;
        set
        {
            if (!SetProperty(ref _isDeploying, value)) return;
            _deployToToolsCommand?.NotifyCanExecuteChanged();
            _generateMmapCommand?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsMmapBusy));
        }
    }

    public string DeployStatus { get => _deployStatus; set => SetProperty(ref _deployStatus, value); }
    public Tone DeployStatusTone { get => _deployStatusTone; set => SetProperty(ref _deployStatusTone, value); }

    public bool IsMmapBusy => IsGeneratingMmap || IsDeploying;

    public void RefreshMmapCacheStatus()
    {
        var written = DmaTestService.GetMmapCacheAge();
        if (written == null)
        {
            MmapStateText = "Not generated";
            MmapAgeText = "· generate before testing";
            MmapStateTone = Tone.Warn;
            GenerateMmapLabel = "Generate map";
        }
        else
        {
            var age = DateTime.Now - written.Value;
            MmapStateText = "Cached";
            MmapAgeText = $"· {FormatAge(age)} old";
            MmapStateTone = age.TotalDays > 30 ? Tone.Warn : Tone.Ok;
            GenerateMmapLabel = "Regenerate map";
        }
    }

    /// <summary>Set by the View to handle the save dialog when mmap content is ready.</summary>
    public Func<string, Task>? MmapReadyToSave { get; set; }

    public ICommand RunSpeedTestCommand => _runSpeedTestCommand;
    public ICommand GenerateMmapCommand => _generateMmapCommand;
    public ICommand DeployToToolsCommand => _deployToToolsCommand;

    public DmaTestViewModel(DmaTestService dmaTestService, LogService logService)
    {
        _dmaTestService = dmaTestService;
        _logService = logService;

        _runSpeedTestCommand = new AsyncRelayCommand(RunSpeedTestAsync, () => !IsTesting);
        _generateMmapCommand = new AsyncRelayCommand(GenerateMmapAsync, () => !IsGeneratingMmap && !IsDeploying);
        _deployToToolsCommand = new AsyncRelayCommand(DeployToToolsAsync, () => !IsDeploying && !IsGeneratingMmap);

        RefreshMmapCacheStatus();
    }

    private async Task RunSpeedTestAsync()
    {
        _runType = SelectedTestType;
        _runFailed = false;
        _rating = "";
        ResetMetrics();
        TestPercentage = 0;
        TestMessage = "Starting...";
        IsTesting = true;

        var started = DateTime.Now;
        try
        {
            var progress = new Progress<FlashProgress>(p =>
            {
                TestPercentage = p.Percentage;
                TestMessage = p.Message;
            });

            DmaTestResult result = _runType switch
            {
                1 => await _dmaTestService.RunLatencyTestAsync(TimeSpan.FromSeconds(30), progress, CancellationToken.None),
                2 => await _dmaTestService.RunThroughputTestAsync(TimeSpan.FromSeconds(15), progress, CancellationToken.None),
                StressTest => await _dmaTestService.RunStressTestAsync(TimeSpan.FromMinutes(StressMinutes), progress, CancellationToken.None),
                _ => await _dmaTestService.RunFullTestAsync(progress, CancellationToken.None)
            };

            ApplyResults(result);
            _runDuration = result.Duration > TimeSpan.Zero ? result.Duration : DateTime.Now - started;
        }
        catch (Exception ex)
        {
            _logService.Error($"Speed test error: {ex.Message}");
            _runFailed = true;
        }
        finally
        {
            _runFinishedAt = DateTime.Now;
            HasResults = true;
            IsTesting = false;
        }
    }

    private async Task GenerateMmapAsync()
    {
        IsGeneratingMmap = true;
        MmapStatus = "Connecting to DMA device...";
        MmapStatusTone = Tone.Info;

        try
        {
            var progress = new Progress<FlashProgress>(p => MmapStatus = p.Message);

            var result = await _dmaTestService.GenerateMmapAsync(progress, CancellationToken.None);

            if (result.Success)
            {
                MmapStatusTone = Tone.Ok;
                RefreshMmapCacheStatus();
                if (MmapReadyToSave != null)
                    await MmapReadyToSave(result.Content);
            }
            else
            {
                MmapStatus = result.ErrorMessage;
                MmapStatusTone = Tone.Danger;
            }
        }
        catch (Exception ex)
        {
            MmapStatus = $"Failed: {ex.Message}";
            MmapStatusTone = Tone.Danger;
            _logService.Error($"mmap generation error: {ex.Message}");
        }
        finally
        {
            IsGeneratingMmap = false;
        }
    }

    private async Task DeployToToolsAsync()
    {
        IsDeploying = true;
        DeployStatus = "Preparing deployment...";
        DeployStatusTone = Tone.Info;

        try
        {
            // Ensure a fresh mmap exists; generate if not cached, then read content.
            string mmapContent = "";
            if (DmaTestService.HasCachedMmap())
            {
                try { mmapContent = await File.ReadAllTextAsync(DmaTestService.MmapCachePath); }
                catch { mmapContent = ""; }
            }

            if (string.IsNullOrWhiteSpace(mmapContent))
            {
                DeployStatus = "No mmap cached - generating first...";
                var genProgress = new Progress<FlashProgress>(p => DeployStatus = p.Message);
                var gen = await _dmaTestService.GenerateMmapAsync(genProgress, CancellationToken.None);
                if (gen.Success)
                {
                    mmapContent = gen.Content;
                    RefreshMmapCacheStatus();
                }
                else
                {
                    DeployStatus = $"Could not generate mmap: {gen.ErrorMessage}. Deploying DLLs without mmap.";
                    mmapContent = "";
                }
            }

            var progress = new Progress<FlashProgress>(p => DeployStatus = p.Message);
            var result = await _dmaTestService.DeployToToolsAsync(mmapContent, progress, CancellationToken.None);

            if (result.Success)
            {
                DeployStatus =
                    $"{result.FoldersFound} folder(s): {result.LeechcoreReplaced} leechcore, " +
                    $"{result.FtdiChainCompleted} FTDI chain, {result.MmapWritten} mmap" +
                    (result.Skipped.Count > 0 ? $" - {result.Skipped.Count} skipped" : "") +
                    (result.Flagged.Count > 0 ? $" - {result.Flagged.Count} flagged" : "");

                DeployStatusTone = result.FoldersFound == 0 || result.Skipped.Count > 0 ? Tone.Warn : Tone.Ok;

                if (result.FoldersFound == 0)
                    DeployStatus = "No DMA tool folders found on any fixed drive.";
            }
            else
            {
                DeployStatus = result.Error;
                DeployStatusTone = Tone.Danger;
            }
        }
        catch (Exception ex)
        {
            DeployStatus = $"Deploy failed: {ex.Message}";
            DeployStatusTone = Tone.Danger;
            _logService.Error($"Deploy to tools error: {ex.Message}");
        }
        finally
        {
            IsDeploying = false;
        }
    }

    private void ResetMetrics()
    {
        var labels = _runType == StressTest ? StressLabels : SpeedLabels;
        for (int i = 0; i < Metrics.Count; i++)
        {
            Metrics[i].Label = labels[i];
            Metrics[i].Value = "—";
        }
    }

    private void SetMetrics(params string[] values)
    {
        for (int i = 0; i < Metrics.Count && i < values.Length; i++)
            Metrics[i].Value = values[i];
    }

    private void ApplyResults(DmaTestResult result)
    {
        if (!result.Success)
        {
            _runFailed = true;
            return;
        }

        _rating = result.OverallRating;

        if (result.TestType == "Stress")
        {
            var rps = result.StressDuration.TotalSeconds > 0 ? result.StressTotalReads / result.StressDuration.TotalSeconds : 0;
            _failedReads = result.StressFailedReads;
            SetMetrics(
                $"{rps:N0}",
                $"{result.StressDuration.TotalMinutes:F1} min",
                $"{result.StressTotalReads:N0}",
                $"{result.StressFailPct:F3} %",
                $"{result.StressMaxConsecFails:N0}",
                $"{result.StressFailedReads:N0}");
            return;
        }

        _failedReads = result.LatencyFailedReads + result.ThroughputFailedReads;
        bool hasLatency = result.LatencyRps > 0;
        SetMetrics(
            hasLatency ? $"{result.LatencyRps:N0}" : "—",
            result.ThroughputMBps > 0 ? $"{result.ThroughputMBps:N0} MB/s"
                : result.ThroughputRating == "SKIP" ? "Skipped" : "—",
            hasLatency ? $"{result.LatencyAvgUs:N0} µs" : "—",
            hasLatency ? $"{result.LatencyMinUs:N0} µs" : "—",
            hasLatency ? $"{result.LatencyMaxUs:N0} µs" : "—",
            $"{_failedReads:N0}");
    }

    private void RaiseRunChanged()
    {
        OnPropertyChanged(nameof(RunTitle));
        OnPropertyChanged(nameof(RunMeta));
        OnPropertyChanged(nameof(RatingText));
        OnPropertyChanged(nameof(RatingTone));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(ShowSpeedScale));
        OnPropertyChanged(nameof(ShowStressScale));
        OnPropertyChanged(nameof(IsScaleAcceptable));
        OnPropertyChanged(nameof(IsScaleGood));
        OnPropertyChanged(nameof(IsScaleExcellent));
        OnPropertyChanged(nameof(IsScalePerfect));
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s"
        : $"{(int)t.TotalHours} h {t.Minutes} min";

    private static string FormatAge(TimeSpan age) =>
        age.TotalMinutes < 1 ? "under a minute"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours} h"
        : age.TotalDays < 2 ? "1 day"
        : $"{(int)age.TotalDays} days";

    private static string ShortenAppData(string path)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return !string.IsNullOrEmpty(appData) && path.StartsWith(appData, StringComparison.OrdinalIgnoreCase)
            ? "%AppData%" + path[appData.Length..]
            : path;
    }
}
