using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class DmaTestViewModel : BaseViewModel
{
    private readonly FtdiDriverService _ftdiService;
    private readonly DmaTestService _dmaTestService;
    private readonly LogService _logService;

    private bool _isFtdiBusy;
    private bool _isFtdiChecked;
    private bool _isFtdiDriverOk;
    private bool _isFtdiDeviceDetected;
    private string _ftdiStatusText = "Not checked";
    private string _ftdiStatusColor = "#6E7681";
    private string _ftdiVersionText = "-";
    private string _ftdiDeviceName = "-";
    private string _ftdiDriverType = "-";
    private string _ftdiBusyMessage = "";
    private string _ftdiInfPath = "";

    private bool _isTesting;
    private bool _hasResults;
    private int _testPercentage;
    private string _testMessage = "";
    private string _testRating = "-";
    private string _testRps = "-";
    private string _testLatency = "-";
    private string _testStatus = "Not tested";
    private string _testThroughput = "-";
    private string _testMinRead = "-";
    private string _testMaxRead = "-";
    private string _testFailed = "-";

    public bool IsFtdiBusy { get => _isFtdiBusy; set { SetProperty(ref _isFtdiBusy, value); ((AsyncRelayCommand)CheckFtdiCommand).NotifyCanExecuteChanged(); ((AsyncRelayCommand)InstallFtdiCommand).NotifyCanExecuteChanged(); ((AsyncRelayCommand)UninstallFtdiCommand).NotifyCanExecuteChanged(); } }
    public bool IsFtdiChecked { get => _isFtdiChecked; set { if (SetProperty(ref _isFtdiChecked, value)) { OnPropertyChanged(nameof(ShowFtdiNotConnected)); OnPropertyChanged(nameof(ShowFtdiDriverMissing)); } } }
    public bool IsFtdiDriverOk { get => _isFtdiDriverOk; set { if (SetProperty(ref _isFtdiDriverOk, value)) OnPropertyChanged(nameof(ShowFtdiDriverMissing)); } }
    public bool IsFtdiDeviceDetected { get => _isFtdiDeviceDetected; set { if (SetProperty(ref _isFtdiDeviceDetected, value)) { OnPropertyChanged(nameof(ShowFtdiNotConnected)); OnPropertyChanged(nameof(ShowFtdiDriverMissing)); } } }
    public string FtdiStatusText { get => _ftdiStatusText; set => SetProperty(ref _ftdiStatusText, value); }
    public string FtdiStatusColor { get => _ftdiStatusColor; set => SetProperty(ref _ftdiStatusColor, value); }
    public string FtdiVersionText { get => _ftdiVersionText; set => SetProperty(ref _ftdiVersionText, value); }
    public string FtdiDeviceName { get => _ftdiDeviceName; set => SetProperty(ref _ftdiDeviceName, value); }
    public string FtdiDriverType { get => _ftdiDriverType; set => SetProperty(ref _ftdiDriverType, value); }
    public string FtdiBusyMessage { get => _ftdiBusyMessage; set => SetProperty(ref _ftdiBusyMessage, value); }

    public bool ShowFtdiNotConnected => IsFtdiChecked && !IsFtdiDeviceDetected;
    public bool ShowFtdiDriverMissing => IsFtdiChecked && IsFtdiDeviceDetected && !IsFtdiDriverOk;

    public bool IsTesting { get => _isTesting; set { SetProperty(ref _isTesting, value); ((AsyncRelayCommand)RunSpeedTestCommand)?.NotifyCanExecuteChanged(); } }
    public bool HasResults { get => _hasResults; set => SetProperty(ref _hasResults, value); }
    public int TestPercentage { get => _testPercentage; set => SetProperty(ref _testPercentage, value); }
    public string TestMessage { get => _testMessage; set => SetProperty(ref _testMessage, value); }
    public string TestRating { get => _testRating; set => SetProperty(ref _testRating, value); }
    public string TestRps { get => _testRps; set => SetProperty(ref _testRps, value); }
    public string TestLatency { get => _testLatency; set => SetProperty(ref _testLatency, value); }
    public string TestStatus { get => _testStatus; set => SetProperty(ref _testStatus, value); }
    public string TestThroughput { get => _testThroughput; set => SetProperty(ref _testThroughput, value); }
    public string TestMinRead { get => _testMinRead; set => SetProperty(ref _testMinRead, value); }
    public string TestMaxRead { get => _testMaxRead; set => SetProperty(ref _testMaxRead, value); }
    public string TestFailed { get => _testFailed; set => SetProperty(ref _testFailed, value); }

    private int _selectedTestType = 0;
    public int SelectedTestType { get => _selectedTestType; set { if (SetProperty(ref _selectedTestType, value)) OnPropertyChanged(nameof(IsStressTestSelected)); } }
    public string[] TestTypes => new[] { "Full Test", "Latency Test", "Throughput Test", "Stress Test" };

    // Stress duration in minutes; visible only when Stress Test is selected.
    private int _stressMinutes = 5;
    public int StressMinutes { get => _stressMinutes; set => SetProperty(ref _stressMinutes, Math.Clamp(value, 1, 60)); }
    public bool IsStressTestSelected => _selectedTestType == 3;

    // mmap generation
    private bool _isGeneratingMmap;
    private string _mmapStatus = "";
    private string _mmapStatusColor = "#6E7681";
    private string _mmapCacheStatus = "";
    private string _mmapCacheColor = "#6E7681";
    public bool IsGeneratingMmap { get => _isGeneratingMmap; set { SetProperty(ref _isGeneratingMmap, value); ((AsyncRelayCommand)GenerateMmapCommand)?.NotifyCanExecuteChanged(); ((AsyncRelayCommand)DeployToToolsCommand)?.NotifyCanExecuteChanged(); } }
    public string MmapStatus { get => _mmapStatus; set => SetProperty(ref _mmapStatus, value); }
    public string MmapStatusColor { get => _mmapStatusColor; set => SetProperty(ref _mmapStatusColor, value); }
    public string MmapCacheStatus { get => _mmapCacheStatus; set => SetProperty(ref _mmapCacheStatus, value); }
    public string MmapCacheColor { get => _mmapCacheColor; set => SetProperty(ref _mmapCacheColor, value); }

    // Deploy to DMA tools
    private bool _isDeploying;
    private string _deployStatus = "";
    private string _deployStatusColor = "#6E7681";
    public bool IsDeploying { get => _isDeploying; set { SetProperty(ref _isDeploying, value); ((AsyncRelayCommand)DeployToToolsCommand)?.NotifyCanExecuteChanged(); ((AsyncRelayCommand)GenerateMmapCommand)?.NotifyCanExecuteChanged(); } }
    public string DeployStatus { get => _deployStatus; set => SetProperty(ref _deployStatus, value); }
    public string DeployStatusColor { get => _deployStatusColor; set => SetProperty(ref _deployStatusColor, value); }

    public void RefreshMmapCacheStatus()
    {
        var age = DmaTestService.GetMmapCacheAge();
        if (age == null)
        {
            MmapCacheStatus = "No mmap cached - generate before running tests";
            MmapCacheColor  = "#F85149";
        }
        else
        {
            var days = (DateTime.Now - age.Value).TotalDays;
            var label = days < 1 ? "today" : days < 2 ? "1 day ago" : $"{(int)days} days ago";
            MmapCacheStatus = $"mmap cached ({label}) - auto-used for all tests";
            MmapCacheColor  = days > 30 ? "#E3B341" : "#3FB950";
        }
    }

    /// <summary>Set by the View to handle the save dialog when mmap content is ready.</summary>
    public Func<string, Task>? MmapReadyToSave { get; set; }

    public ICommand CheckFtdiCommand { get; }
    public ICommand InstallFtdiCommand { get; }
    public ICommand UninstallFtdiCommand { get; }
    public ICommand RunSpeedTestCommand { get; }
    public ICommand GenerateMmapCommand { get; }
    public ICommand DeployToToolsCommand { get; }

    public DmaTestViewModel(FtdiDriverService ftdiService, DmaTestService dmaTestService, LogService logService)
    {
        _ftdiService = ftdiService;
        _dmaTestService = dmaTestService;
        _logService = logService;

        CheckFtdiCommand = new AsyncRelayCommand(CheckFtdiAsync, () => !IsFtdiBusy);
        InstallFtdiCommand = new AsyncRelayCommand(InstallFtdiAsync, () => !IsFtdiBusy);
        UninstallFtdiCommand = new AsyncRelayCommand(UninstallFtdiAsync, () => !IsFtdiBusy);
        RunSpeedTestCommand = new AsyncRelayCommand(RunSpeedTestAsync, () => !IsTesting);
        GenerateMmapCommand = new AsyncRelayCommand(GenerateMmapAsync, () => !IsGeneratingMmap && !IsDeploying);
        DeployToToolsCommand = new AsyncRelayCommand(DeployToToolsAsync, () => !IsDeploying && !IsGeneratingMmap);

        RefreshMmapCacheStatus();
    }

    private async Task CheckFtdiAsync()
    {
        IsFtdiBusy = true;
        FtdiBusyMessage = "Checking FTDI driver...";
        try
        {
            var info = await _ftdiService.CheckDriverAsync();
            IsFtdiDeviceDetected = info.IsDeviceDetected;
            IsFtdiDriverOk = info.IsDriverOk;
            FtdiStatusText = info.Status;
            FtdiStatusColor = info.IsDriverOk ? "#3FB950" : (info.IsDeviceDetected ? "#F85149" : "#6E7681");
            FtdiVersionText = info.Version;
            FtdiDeviceName = info.DeviceName;
            FtdiDriverType = info.DriverType;
            _ftdiInfPath = info.InfPath;
            IsFtdiChecked = true;
        }
        catch (Exception ex)
        {
            _logService.Error($"FTDI driver check failed: {ex.Message}");
            FtdiStatusText = "Check failed";
            FtdiStatusColor = "#F85149";
        }
        finally { IsFtdiBusy = false; FtdiBusyMessage = ""; }
    }

    private async Task InstallFtdiAsync()
    {
        IsFtdiBusy = true;
        FtdiBusyMessage = "Installing FTDI driver...";
        try
        {
            await _ftdiService.InstallDriverAsync();
            await CheckFtdiAsync();
        }
        catch (Exception ex) { _logService.Error($"FTDI driver install failed: {ex.Message}"); }
        finally { IsFtdiBusy = false; FtdiBusyMessage = ""; }
    }

    private async Task UninstallFtdiAsync()
    {
        IsFtdiBusy = true;
        FtdiBusyMessage = "Removing FTDI driver...";
        try
        {
            await _ftdiService.UninstallDriverAsync(_ftdiInfPath);
            await CheckFtdiAsync();
        }
        catch (Exception ex) { _logService.Error($"FTDI driver removal failed: {ex.Message}"); }
        finally { IsFtdiBusy = false; FtdiBusyMessage = ""; }
    }

    private async Task RunSpeedTestAsync()
    {
        IsTesting = true;
        HasResults = false;
        ResetResults();

        try
        {
            var progress = new Progress<FlashProgress>(p =>
            {
                TestPercentage = p.Percentage;
                TestMessage = p.Message;
            });

            DmaTestResult result = SelectedTestType switch
            {
                0 => await _dmaTestService.RunFullTestAsync(progress, CancellationToken.None),
                1 => await _dmaTestService.RunLatencyTestAsync(TimeSpan.FromSeconds(30), progress, CancellationToken.None),
                2 => await _dmaTestService.RunThroughputTestAsync(TimeSpan.FromSeconds(15), progress, CancellationToken.None),
                3 => await _dmaTestService.RunStressTestAsync(TimeSpan.FromMinutes(StressMinutes), progress, CancellationToken.None),
                _ => await _dmaTestService.RunFullTestAsync(progress, CancellationToken.None)
            };

            ApplyResults(result);
        }
        catch (Exception ex)
        {
            _logService.Error($"Speed test error: {ex.Message}");
            TestStatus = "Error";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task GenerateMmapAsync()
    {
        IsGeneratingMmap = true;
        MmapStatus = "Connecting to DMA device...";
        MmapStatusColor = "#E3B341";

        try
        {
            var progress = new Progress<FlashProgress>(p => MmapStatus = p.Message);

            var result = await _dmaTestService.GenerateMmapAsync(progress, CancellationToken.None);

            if (result.Success)
            {
                MmapStatusColor = "#00D4AA";
                RefreshMmapCacheStatus();
                if (MmapReadyToSave != null)
                    await MmapReadyToSave(result.Content);
            }
            else
            {
                MmapStatus = result.ErrorMessage;
                MmapStatusColor = "#F85149";
            }
        }
        catch (Exception ex)
        {
            MmapStatus = $"Failed: {ex.Message}";
            MmapStatusColor = "#F85149";
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
        DeployStatusColor = "#E3B341";

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

                DeployStatusColor =
                    result.FoldersFound == 0 ? "#E3B341" :
                    result.Skipped.Count > 0 ? "#E3B341" : "#00D4AA";

                if (result.FoldersFound == 0)
                    DeployStatus = "No DMA tool folders found on any fixed drive.";
            }
            else
            {
                DeployStatus = result.Error;
                DeployStatusColor = "#F85149";
            }
        }
        catch (Exception ex)
        {
            DeployStatus = $"Deploy failed: {ex.Message}";
            DeployStatusColor = "#F85149";
            _logService.Error($"Deploy to tools error: {ex.Message}");
        }
        finally
        {
            IsDeploying = false;
        }
    }

    private void ResetResults()
    {
        TestPercentage = 0;
        TestMessage = "Starting...";
        TestStatus = "Running";
        TestRating = "-";
        TestRps = "-";
        TestLatency = "-";
        TestThroughput = "-";
        TestMinRead = "-";
        TestMaxRead = "-";
        TestFailed = "-";
    }

    private void ApplyResults(DmaTestResult result)
    {
        if (!result.Success)
        {
            TestRating = "FAIL";
            TestStatus = "Failed";
            HasResults = true;
            return;
        }

        TestStatus = result.OverallRating == "FAIL" ? "Fail" : "Pass";
        TestRating = result.OverallRating;

        if (result.LatencyRps > 0)
        {
            TestRps = $"{result.LatencyRps:N0}";
            TestLatency = $"{result.LatencyAvgUs:N0} us";
            TestMinRead = $"{result.LatencyMinUs:N0} us";
            TestMaxRead = $"{result.LatencyMaxUs:N0} us";
        }

        if (result.ThroughputMBps > 0)
            TestThroughput = $"{result.ThroughputMBps:F1} MB/s";
        else if (result.ThroughputRating == "SKIP")
            TestThroughput = "Skipped";

        long totalFailed = result.LatencyFailedReads + result.ThroughputFailedReads;
        if (result.TestType == "Stress")
        {
            TestRps        = $"{(result.StressDuration.TotalSeconds > 0 ? result.StressTotalReads / result.StressDuration.TotalSeconds : 0):N0}";
            TestLatency    = $"{result.StressDuration.TotalMinutes:F1} min";
            TestThroughput = $"{result.StressMaxConsecFails} max-streak";
            TestMinRead    = $"{result.StressTotalReads:N0} reads";
            TestMaxRead    = $"{result.StressFailPct:F3} % fail";
            totalFailed    = result.StressFailedReads;
        }
        TestFailed = $"{totalFailed:N0}";

        HasResults = true;
    }
}
