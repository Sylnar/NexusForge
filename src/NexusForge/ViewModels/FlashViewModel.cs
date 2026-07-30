using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Helpers;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class FlashViewModel : BaseViewModel
{
    private readonly FlashService _flashService;
    private readonly LogService _logService;

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

    public string FirmwarePath
    {
        get => _firmwarePath;
        set
        {
            if (SetProperty(ref _firmwarePath, value))
                ValidateFile();
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
                (FlashFirmwareCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public string FileValidationMessage
    {
        get => _fileValidationMessage;
        set => SetProperty(ref _fileValidationMessage, value);
    }

    public int FlashPercentage
    {
        get => _flashPercentage;
        set => SetProperty(ref _flashPercentage, value);
    }

    public string FlashStage
    {
        get => _flashStage;
        set => SetProperty(ref _flashStage, value);
    }

    public string FlashMessage
    {
        get => _flashMessage;
        set => SetProperty(ref _flashMessage, value);
    }

    public bool IsFlashing
    {
        get => _isFlashing;
        set => SetProperty(ref _isFlashing, value);
    }

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

    public string FlashErrorMessage
    {
        get => _flashErrorMessage;
        set => SetProperty(ref _flashErrorMessage, value);
    }

    public ICommand BrowseFileCommand { get; }
    public ICommand FlashFirmwareCommand { get; }
    public ICommand CancelFlashCommand { get; }

    public FlashViewModel(FlashService flashService, LogService logService)
    {
        _flashService = flashService;
        _logService = logService;
        BrowseFileCommand = new AsyncRelayCommand(BrowseFileAsync);
        FlashFirmwareCommand = new AsyncRelayCommand(FlashFirmwareAsync, () => IsFileValid && !IsFlashing);
        CancelFlashCommand = new RelayCommand(CancelFlash, () => IsFlashing);
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
        FileSizeText = firmware.FileSize > 0 ? $"{firmware.FileSize / 1024}KB" : "—";
        IsFileValid = firmware.IsValid;
        FileValidationMessage = firmware.ValidationMessage;
    }

    private async Task FlashFirmwareAsync()
    {
        if (string.IsNullOrEmpty(FirmwarePath) || !IsFileValid)
            return;

        IsFlashing = true;
        IsFlashComplete = false;
        HasFlashError = false;
        FlashPercentage = 0;
        FlashStage = "Starting...";
        FlashMessage = "Preparing to flash firmware...";

        _cts = new CancellationTokenSource();

        var progress = new Progress<FlashProgress>(p =>
        {
            FlashPercentage = p.Percentage;
            FlashStage = p.Stage;
            FlashMessage = p.Message;

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
                IsFlashComplete = true;
                FlashPercentage = 100;
                FlashStage = "Complete";
                FlashMessage = "Firmware flashed successfully!";
            }
            else
            {
                HasFlashError = true;
                FlashErrorMessage = result.ErrorMessage;
                FlashStage = "Failed";
                FlashMessage = $"Flash failed: {result.ErrorMessage}";
            }
        }
        catch (OperationCanceledException)
        {
            FlashStage = "Cancelled";
            FlashMessage = "Flash operation was cancelled";
        }
        catch (Exception ex)
        {
            HasFlashError = true;
            FlashErrorMessage = ex.Message;
            FlashStage = "Error";
            FlashMessage = $"Flash error: {ex.Message}";
        }
        finally
        {
            IsFlashing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelFlash()
    {
        _cts?.Cancel();
        _logService.Warn("Flash operation cancelled by user");
    }
}
