using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Helpers;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class FlashViewModel : BaseViewModel
{
    private enum RunState { Idle, Running, Succeeded, Failed, Cancelled }

    private static readonly string[] SpiSteps = { "Bridge", "Erase", "Write", "Done" };
    private static readonly string[] SramSteps = { "Connect", "Program", "Done" };

    // Stages reported before the programmer process starts. Cancel is honoured
    // only there: after that, stopping would leave a half-written flash.
    private static readonly HashSet<string> CancellableStages =
        new(StringComparer.OrdinalIgnoreCase) { "Starting...", "Initializing", "Detecting" };

    private readonly FlashService _flashService;
    private readonly LogService _logService;
    private readonly AppSettings _settings;
    private readonly AsyncRelayCommand _flashFirmwareCommand;
    private readonly RelayCommand _cancelFlashCommand;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _stopwatch = new();

    private string _firmwarePath = string.Empty;
    private string _firmwareFileName = "No file selected";
    private string _fileSizeText = "—";
    private bool _isFileValid;
    private string _fileValidationMessage = string.Empty;
    private int _flashPercentage;
    private string _flashStage = string.Empty;
    private string _flashMessage = string.Empty;
    private bool _isFlashing;
    private bool _isFlashComplete;
    private bool _hasFlashError;
    private string _flashErrorMessage = string.Empty;
    private CancellationTokenSource? _cts;
    private RunState _runState = RunState.Idle;
    private int _activeStep;
    private int _furthestStep;
    private int _furthestStage;

    /// <summary>
    /// How far through a flash a stage is. Used to drop reports that arrive out
    /// of order; everything before the erase shares rank 0, since those are all
    /// the opening step and their order between themselves does not matter.
    /// </summary>
    private static int StageRank(string stage) => stage switch
    {
        "Erasing" => 1,
        "Erased" => 2,
        "Writing" or "Programming" => 3,
        "Written" => 4,
        "Complete" => 5,
        _ => 0,
    };

    public ObservableCollection<FlashStep> Steps { get; } = new();

    public string FirmwarePath
    {
        get => _firmwarePath;
        set
        {
            if (SetProperty(ref _firmwarePath, value))
            {
                // A new file makes the previous run's result stale.
                IsFlashComplete = false;
                HasFlashError = false;
                SetRunState(RunState.Idle);
                ValidateFile();
                ResetSteps();
                OnPropertyChanged(nameof(HasFile));
                OnPropertyChanged(nameof(IsBitFile));
                OnPropertyChanged(nameof(FlashModeText));
                OnPropertyChanged(nameof(CancelNote));
                OnPropertyChanged(nameof(FileDetailText));
                OnPropertyChanged(nameof(FileDetailTone));
            }
        }
    }

    public string FirmwareFileName
    {
        get => _firmwareFileName;
        set => SetProperty(ref _firmwareFileName, value);
    }

    public string FileSizeText
    {
        get => _fileSizeText;
        set => SetProperty(ref _fileSizeText, value);
    }

    public bool IsFileValid
    {
        get => _isFileValid;
        set
        {
            if (SetProperty(ref _isFileValid, value))
            {
                _flashFirmwareCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public string FileValidationMessage
    {
        get => _fileValidationMessage;
        set => SetProperty(ref _fileValidationMessage, value);
    }

    public bool HasFile => !string.IsNullOrEmpty(FirmwarePath);

    public bool IsBitFile => FirmwarePath.EndsWith(".bit", StringComparison.OrdinalIgnoreCase);

    public string FlashModeText => IsBitFile ? "SRAM · until power-off" : "SPI flash · persistent";

    public string FileDetailText =>
        !HasFile ? "Drop a .bin or .bit file here, or browse"
        : IsFileValid ? $"{FileSizeText} · drop a .bin or .bit here, or browse"
        : FileValidationMessage;

    public Tone FileDetailTone => HasFile && !IsFileValid ? Tone.Warn : Tone.Neutral;

    public string CancelNote => IsBitFile
        ? "Cancel works until the bitstream starts loading. A .bit load is lost at power-off anyway."
        : "Cancel works until the SPI erase starts — stopping mid-write would leave the board unbootable.";

    public int FlashPercentage
    {
        get => _flashPercentage;
        set
        {
            if (SetProperty(ref _flashPercentage, value))
                OnPropertyChanged(nameof(PercentText));
        }
    }

    public string PercentText => $"{FlashPercentage}%";

    public string FlashStage
    {
        get => _flashStage;
        set
        {
            if (SetProperty(ref _flashStage, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                _cancelFlashCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public string FlashMessage
    {
        get => _flashMessage;
        set
        {
            if (SetProperty(ref _flashMessage, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    /// <summary>Status line above the bar: the live stage message while flashing.</summary>
    public string ProgressText => _runState switch
    {
        RunState.Running => FlashMessage,
        RunState.Succeeded => "Flash complete",
        RunState.Failed => "Flash failed",
        RunState.Cancelled => "Flash cancelled",
        _ => !HasFile ? "No firmware selected" : IsFileValid ? "Ready to flash" : "This file can't be flashed"
    };

    public Tone ProgressTone => _runState switch
    {
        RunState.Running or RunState.Succeeded => Tone.Ok,
        RunState.Failed => Tone.Danger,
        _ => Tone.Neutral
    };

    public bool IsFailedRun => _runState == RunState.Failed;

    public string ElapsedText => _runState switch
    {
        RunState.Running => $"Elapsed {FormatElapsed(_stopwatch.Elapsed)}",
        RunState.Succeeded => $"Finished in {FormatElapsed(_stopwatch.Elapsed)}",
        RunState.Failed or RunState.Cancelled => $"Stopped after {FormatElapsed(_stopwatch.Elapsed)}",
        _ => ".bin writes SPI flash (kept at power-off) · .bit loads SRAM (lost at power-off)"
    };

    public bool IsFlashing
    {
        get => _isFlashing;
        set
        {
            if (SetProperty(ref _isFlashing, value))
            {
                _flashFirmwareCommand?.NotifyCanExecuteChanged();
                _cancelFlashCommand?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(FlashButtonText));
            }
        }
    }

    public string FlashButtonText => IsFlashing ? "Flashing…" : "Flash firmware";

    public bool CanCancel => IsFlashing && CancellableStages.Contains(FlashStage);

    public bool IsFlashComplete
    {
        get => _isFlashComplete;
        set => SetProperty(ref _isFlashComplete, value);
    }

    public bool HasFlashError
    {
        get => _hasFlashError;
        set => SetProperty(ref _hasFlashError, value);
    }

    /// <summary>What to do after a successful flash; differs for SPI (.bin) vs SRAM (.bit).</summary>
    public string FlashSuccessMessage =>
        IsBitFile
            ? "Bitstream loaded into the FPGA. It is active now and is lost on power-off."
            : "Firmware written to SPI flash. Fully power-cycle the target PC to load it.";

    public string FlashErrorMessage
    {
        get => _flashErrorMessage;
        set => SetProperty(ref _flashErrorMessage, value);
    }

    public ICommand BrowseFileCommand { get; }
    public ICommand FlashFirmwareCommand => _flashFirmwareCommand;
    public ICommand CancelFlashCommand => _cancelFlashCommand;

    public FlashViewModel(FlashService flashService, LogService logService, AppSettings settings)
    {
        _flashService = flashService;
        _logService = logService;
        _settings = settings;
        BrowseFileCommand = new AsyncRelayCommand(BrowseFileAsync);
        _flashFirmwareCommand = new AsyncRelayCommand(FlashFirmwareAsync, () => IsFileValid && !IsFlashing);
        _cancelFlashCommand = new RelayCommand(CancelFlash, () => CanCancel);

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));

        ResetSteps();

        // Restore the firmware picked last session so a re-flash is one click.
        var last = _settings.LastFirmwarePath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last))
            FirmwarePath = last;
    }

    /// <summary>A file dropped on the Firmware card.</summary>
    public void LoadDroppedFile(string path)
    {
        if (IsFlashing) return;
        FirmwarePath = path;
    }

    private async Task BrowseFileAsync()
    {
        try
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop?.MainWindow);

            if (topLevel?.StorageProvider is { } sp)
            {
                var files = await sp.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Select Firmware File",
                    AllowMultiple = false,
                    FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
                    {
                        new("Firmware Files") { Patterns = new List<string> { "*.bin", "*.bit" } },
                        new("All Files") { Patterns = new List<string> { "*.*" } }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var uri = files[0].Path;
                    if (uri.IsFile)
                        FirmwarePath = uri.LocalPath;
                }
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"File browser error: {ex.Message}");
        }
    }

    private void ValidateFile()
    {
        if (string.IsNullOrEmpty(FirmwarePath))
        {
            FirmwareFileName = "No file selected";
            FileSizeText = "—";
            IsFileValid = false;
            return;
        }

        var firmware = FileValidator.Validate(FirmwarePath);
        FirmwareFileName = firmware.FileName;
        FileSizeText = firmware.FileSize > 0 ? $"{firmware.FileSize / 1024:N0} KB" : "—";
        IsFileValid = firmware.IsValid;
        FileValidationMessage = firmware.ValidationMessage;
        if (firmware.IsValid)
            _settings.LastFirmwarePath = FirmwarePath;
    }

    private async Task FlashFirmwareAsync()
    {
        if (string.IsNullOrEmpty(FirmwarePath) || !IsFileValid)
            return;

        IsFlashComplete = false;
        HasFlashError = false;
        FlashPercentage = 0;
        FlashStage = "Starting...";
        FlashMessage = "Preparing to flash firmware...";
        IsFlashing = true;
        ResetSteps();
        SetActiveStep(0, 0);
        _stopwatch.Restart();
        _elapsedTimer.Start();
        SetRunState(RunState.Running);

        _cts = new CancellationTokenSource();

        var progress = new Progress<FlashProgress>(p =>
        {
            // A report can be queued behind the final result; ignore it then.
            if (_runState != RunState.Running) return;

            // "Failed" reports 0%; keep the bar where it stopped.
            if (p.Stage == "Failed")
            {
                FlashStage = p.Stage;
                FlashMessage = p.Message;
                MarkActiveStepFailed();
            }
            else
            {
                // Drop a report that belongs to a stage already passed. The
                // service reads OpenOCD's stderr and stdout on two independent
                // tasks with no ordering between them, so an erase line can be
                // handled after a write line from the other pipe; applying it
                // would wind the percentage and the message backwards while the
                // flash is in fact further along.
                int rank = StageRank(p.Stage);
                if (rank < _furthestStage) return;
                _furthestStage = rank;

                FlashStage = p.Stage;
                FlashMessage = p.Message;
                FlashPercentage = p.Percentage;
                ApplyStageToSteps(p.Stage, p.Percentage);
            }

            if (p.IsComplete)
                IsFlashComplete = true;
            if (p.HasError)
            {
                HasFlashError = true;
                FlashErrorMessage = p.ErrorMessage;
            }
        });

        try
        {
            var result = await _flashService.FlashAsync(FirmwarePath, progress, _cts.Token);

            if (result.Success)
            {
                OnPropertyChanged(nameof(FlashSuccessMessage));
                IsFlashComplete = true;
                FlashPercentage = 100;
                FlashStage = "Complete";
                FlashMessage = "Firmware flashed successfully!";
                SetActiveStep(Steps.Count, 0);
                SetRunState(RunState.Succeeded);
            }
            else
            {
                HasFlashError = true;
                FlashErrorMessage = result.ErrorMessage;
                FlashStage = "Failed";
                FlashMessage = $"Flash failed: {result.ErrorMessage}";
                MarkActiveStepFailed();
                SetRunState(RunState.Failed);
            }
        }
        catch (OperationCanceledException)
        {
            FlashStage = "Cancelled";
            FlashMessage = "Flash operation was cancelled";
            if (_activeStep < Steps.Count) Steps[_activeStep].SetPending();
            SetRunState(RunState.Cancelled);
        }
        catch (Exception ex)
        {
            HasFlashError = true;
            FlashErrorMessage = ex.Message;
            FlashStage = "Error";
            FlashMessage = $"Flash error: {ex.Message}";
            MarkActiveStepFailed();
            SetRunState(RunState.Failed);
        }
        finally
        {
            _stopwatch.Stop();
            _elapsedTimer.Stop();
            IsFlashing = false;
            OnPropertyChanged(nameof(ElapsedText));
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelFlash()
    {
        // Honoured only before the SPI erase/write starts; after that the
        // service logs that the write will be allowed to finish.
        _logService.Warn("Cancel requested by user.");
        _cts?.Cancel();
    }

    private void SetRunState(RunState state)
    {
        _runState = state;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressTone));
        OnPropertyChanged(nameof(IsFailedRun));
        OnPropertyChanged(nameof(ElapsedText));
    }

    private void ResetSteps()
    {
        Steps.Clear();
        foreach (var label in IsBitFile ? SramSteps : SpiSteps)
            Steps.Add(new FlashStep(label));
        _activeStep = 0;
        _furthestStep = 0;
        _furthestStage = 0;
    }

    /// <summary>
    /// Maps the service's stage/percentage onto the step bar.
    ///
    /// Only ever forwards. The service parses two pipes - OpenOCD's log goes to
    /// stderr while the write's per-page lines are a plain printf on stdout - and
    /// they are drained by two independent tasks, so a line can be handled after
    /// a later one from the other pipe. Without this the bar could be dragged
    /// back from Write to Erase by a straggling erase line. Flashing only ever
    /// moves forward, so the furthest step reached is the honest one to show.
    /// </summary>
    private void ApplyStageToSteps(string stage, int pct)
    {
        var (index, fraction) = IsBitFile
            ? stage switch
            {
                "Programming" => (1, 0.5),
                "Complete" => (Steps.Count, 0.0),
                _ => (0, pct / 20.0)
            }
            : stage switch
            {
                "Erasing" => (1, (pct - 15) / 40.0),
                "Erased" => (2, 0.0),
                "Writing" => (2, (pct - 55) / 33.0),
                "Written" => (3, 0.5),
                "Complete" => (Steps.Count, 0.0),
                _ => (0, pct / 15.0)
            };

        if (index < _furthestStep)
        {
            // A straggler from the other pipe. Hold the step it belongs to at
            // full, rather than un-doing the progress already shown.
            if (index >= 0 && index < Steps.Count) Steps[index].SetDone();
            return;
        }

        _furthestStep = index;
        SetActiveStep(index, fraction);
    }

    private void SetActiveStep(int index, double fraction)
    {
        _activeStep = index;
        if (index > _furthestStep) _furthestStep = index;
        for (int i = 0; i < Steps.Count; i++)
        {
            if (i < index) Steps[i].SetDone();
            else if (i == index) Steps[i].SetActive(fraction);
            else Steps[i].SetPending();
        }
    }

    private void MarkActiveStepFailed()
    {
        if (_activeStep < Steps.Count)
            Steps[_activeStep].SetFailed();
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
