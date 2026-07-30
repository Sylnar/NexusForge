using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class BoardInfoViewModel : BaseViewModel
{
    private readonly BoardDetectionService _boardService;
    private readonly NativeJtagService _jtagService;
    private readonly LogService _logService;

    private bool _isDetecting;
    private bool _isReloading;
    private bool _isBoardDetected;
    private string _connectionStatus = "Not Connected";
    private string _connectionStatusColor = "#FF5252";
    private string _deviceName = "—";
    private string _idCode = "—";
    private string _packageName = "—";
    private string _cableType = "—";
    private string _dna = "—";
    private string _dnaFormatted = string.Empty;

    public bool IsDetecting
    {
        get => _isDetecting;
        set => SetProperty(ref _isDetecting, value);
    }

    public bool IsBoardDetected
    {
        get => _isBoardDetected;
        set => SetProperty(ref _isBoardDetected, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public string ConnectionStatusColor
    {
        get => _connectionStatusColor;
        set => SetProperty(ref _connectionStatusColor, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public string IdCode
    {
        get => _idCode;
        set => SetProperty(ref _idCode, value);
    }

    public string PackageName
    {
        get => _packageName;
        set => SetProperty(ref _packageName, value);
    }

    public string CableType
    {
        get => _cableType;
        set => SetProperty(ref _cableType, value);
    }

    public string Dna
    {
        get => _dna;
        set => SetProperty(ref _dna, value);
    }

    public string DnaFormatted
    {
        get => _dnaFormatted;
        set => SetProperty(ref _dnaFormatted, value);
    }

    public bool IsReloading
    {
        get => _isReloading;
        set => SetProperty(ref _isReloading, value);
    }

    private int _reloadPercentage;
    public int ReloadPercentage
    {
        get => _reloadPercentage;
        set => SetProperty(ref _reloadPercentage, value);
    }

    private string _reloadMessage = string.Empty;
    public string ReloadMessage
    {
        get => _reloadMessage;
        set => SetProperty(ref _reloadMessage, value);
    }

    public ICommand DetectBoardCommand { get; }
    public ICommand CopyDnaCommand { get; }
    public ICommand HotReloadCommand { get; }

    public event EventHandler? BoardDetected;
    public event EventHandler? BoardLost;

    public BoardInfoViewModel(BoardDetectionService boardService, NativeJtagService jtagService, LogService logService)
    {
        _boardService = boardService;
        _jtagService = jtagService;
        _logService = logService;
        DetectBoardCommand = new AsyncRelayCommand(DetectBoardAsync, () => !IsDetecting && !IsReloading);
        CopyDnaCommand = new RelayCommand(CopyDna, () => !string.IsNullOrEmpty(Dna) && Dna != "—");
        HotReloadCommand = new AsyncRelayCommand(HotReloadAsync, () => !IsDetecting && !IsReloading);
    }

    private async Task HotReloadAsync()
    {
        IsReloading = true;
        ReloadPercentage = 0;
        ReloadMessage = "Starting hot reload...";

        try
        {
            var progress = new Progress<FlashProgress>(p =>
            {
                ReloadPercentage = p.Percentage;
                ReloadMessage = p.Message;
            });

            await Task.Run(() => _jtagService.HotResetFpgaAsync(progress));
        }
        catch (Exception ex)
        {
            _logService.Error($"Hot reload error: {ex.Message}");
        }
        finally
        {
            IsReloading = false;
            ReloadMessage = string.Empty;
            ReloadPercentage = 0;
        }
    }

    private async Task DetectBoardAsync()
    {
        IsDetecting = true;
        ConnectionStatus = "Detecting...";
        ConnectionStatusColor = "#FFC107";

        try
        {
            var board = await _boardService.DetectBoardAsync();

            if (board.IsDetected)
            {
                IsBoardDetected = true;
                ConnectionStatus = "Connected";
                ConnectionStatusColor = "#00E676";
                DeviceName = board.DeviceName;
                IdCode = board.IdCode;
                PackageName = board.Package;
                CableType = board.JtagCable;
                Dna = board.Dna;
                DnaFormatted = board.DnaFormatted;
                BoardDetected?.Invoke(this, EventArgs.Empty);
                (CopyDnaCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
            else
            {
                IsBoardDetected = false;
                ConnectionStatus = "Not Connected";
                ConnectionStatusColor = "#FF5252";
                DeviceName = "—";
                IdCode = "—";
                PackageName = "—";
                CableType = "—";
                Dna = "—";
                DnaFormatted = string.Empty;
                BoardLost?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"Board detection error: {ex.Message}");
            ConnectionStatus = "Error";
            ConnectionStatusColor = "#FF5252";
        }
        finally
        {
            IsDetecting = false;
        }
    }

    private async void CopyDna()
    {
        var dnaToCopy = !string.IsNullOrEmpty(Dna) && Dna != "—" ? Dna : null;
        if (dnaToCopy == null) return;

        try
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop?.MainWindow);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(dnaToCopy);
            }
            _logService.Info($"DNA copied to clipboard: {dnaToCopy}");
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to copy DNA: {ex.Message}");
        }
    }
}
