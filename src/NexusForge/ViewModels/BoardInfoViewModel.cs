using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class BoardInfoViewModel : BaseViewModel
{
    private const string Empty = "—";
    private const string NoBoardSubtitle = "Connect the board, then press Detect board";

    private readonly BoardDetectionService _boardService;
    private readonly NativeJtagService _jtagService;
    private readonly LogService _logService;
    private readonly AsyncRelayCommand _copyDnaCommand;

    private bool _isDetecting;
    private bool _isReloading;
    private bool _isBoardDetected;
    private string _connectionStatus = "Not detected";
    private Tone _connectionTone = Tone.Neutral;
    private string _deviceName = Empty;
    private string _devicePart = "No board";
    private string _deviceSubtitle = NoBoardSubtitle;
    private string _idCode = Empty;
    private string _packageName = Empty;
    private string _cableType = Empty;
    private string _bridge = Empty;
    private string _dna = Empty;
    private string _dnaFormatted = string.Empty;
    private string _copyDnaLabel = "Copy";

    public bool IsDetecting
    {
        get => _isDetecting;
        set
        {
            if (SetProperty(ref _isDetecting, value))
                OnPropertyChanged(nameof(DetectButtonText));
        }
    }

    public string DetectButtonText => IsDetecting ? "Detecting…" : "Detect board";

    public bool IsBoardDetected
    {
        get => _isBoardDetected;
        set => SetProperty(ref _isBoardDetected, value);
    }

    /// <summary>Status pill text: Not detected / Detecting… / Connected / Not found / Error.</summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public Tone ConnectionTone
    {
        get => _connectionTone;
        set => SetProperty(ref _connectionTone, value);
    }

    /// <summary>Full name from detection, e.g. "XC7A75T (Artix-7)".</summary>
    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    /// <summary>Headline part number, e.g. "XC7A75T".</summary>
    public string DevicePart
    {
        get => _devicePart;
        set => SetProperty(ref _devicePart, value);
    }

    /// <summary>Line under the part number, e.g. "Artix-7 · detected 14:32:06".</summary>
    public string DeviceSubtitle
    {
        get => _deviceSubtitle;
        set => SetProperty(ref _deviceSubtitle, value);
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

    /// <summary>BSCAN SPI bridge used for flashing this part.</summary>
    public string Bridge
    {
        get => _bridge;
        set => SetProperty(ref _bridge, value);
    }

    public string Dna
    {
        get => _dna;
        set
        {
            if (!SetProperty(ref _dna, value)) return;
            OnPropertyChanged(nameof(HasDna));
            OnPropertyChanged(nameof(DnaDisplay));
            OnPropertyChanged(nameof(DnaHint));
            _copyDnaCommand.NotifyCanExecuteChanged();
        }
    }

    public string DnaFormatted
    {
        get => _dnaFormatted;
        set => SetProperty(ref _dnaFormatted, value);
    }

    public bool HasDna => !string.IsNullOrEmpty(Dna) && Dna != Empty;

    /// <summary>The DNA in groups of four ("004a 91c2 e37f 5b08"); Copy still copies the raw value.</summary>
    public string DnaDisplay
    {
        get
        {
            if (!HasDna) return "──── ──── ──── ────";
            var groups = Enumerable.Range(0, (Dna.Length + 3) / 4)
                .Select(i => Dna.Substring(i * 4, Math.Min(4, Dna.Length - i * 4)));
            return string.Join(' ', groups);
        }
    }

    public string DnaHint => HasDna || !IsBoardDetected
        ? "Send this to your firmware provider"
        : "Not read: replug USB and detect again";

    public string CopyDnaLabel
    {
        get => _copyDnaLabel;
        set => SetProperty(ref _copyDnaLabel, value);
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
    public ICommand CopyDnaCommand => _copyDnaCommand;
    public ICommand HotReloadCommand { get; }

    public event EventHandler? BoardDetected;
    public event EventHandler? BoardLost;

    public BoardInfoViewModel(BoardDetectionService boardService, NativeJtagService jtagService, LogService logService)
    {
        _boardService = boardService;
        _jtagService = jtagService;
        _logService = logService;
        DetectBoardCommand = new AsyncRelayCommand(DetectBoardAsync, () => !IsDetecting && !IsReloading);
        // Concurrent so the button stays enabled while it shows "Copied".
        _copyDnaCommand = new AsyncRelayCommand(CopyDnaAsync, () => HasDna,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
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
        ConnectionStatus = "Detecting…";
        ConnectionTone = Tone.Info;
        DeviceSubtitle = "Reading IDCODE and DNA over JTAG…";

        try
        {
            var board = await _boardService.DetectBoardAsync();

            if (board.IsDetected)
            {
                var (part, family) = SplitDeviceName(board.DeviceName);
                IsBoardDetected = true;
                ConnectionStatus = "Connected";
                ConnectionTone = Tone.Ok;
                DeviceName = board.DeviceName;
                DevicePart = part;
                DeviceSubtitle = $"{family} · detected {board.DetectedAt:HH:mm:ss}";
                IdCode = board.IdCode;
                PackageName = board.Package;
                CableType = board.JtagCable;
                Bridge = string.IsNullOrEmpty(board.Bridge) ? "None (detect only)" : board.Bridge;
                Dna = string.IsNullOrEmpty(board.Dna) ? Empty : board.Dna;
                DnaFormatted = board.DnaFormatted;
                BoardDetected?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ClearBoard("Not found", Tone.Warn, "Nothing answered on JTAG. Check power and the USB cable.");
                BoardLost?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logService.Error($"Board detection error: {ex.Message}");
            ClearBoard("Error", Tone.Danger, "Detection failed. See the log for details.");
        }
        finally
        {
            IsDetecting = false;
            OnPropertyChanged(nameof(DnaHint));
        }
    }

    private void ClearBoard(string status, Tone tone, string subtitle)
    {
        IsBoardDetected = false;
        ConnectionStatus = status;
        ConnectionTone = tone;
        DeviceName = Empty;
        DevicePart = "No board";
        DeviceSubtitle = subtitle;
        IdCode = Empty;
        PackageName = Empty;
        CableType = Empty;
        Bridge = Empty;
        Dna = Empty;
        DnaFormatted = string.Empty;
    }

    /// <summary>"XC7A75T (Artix-7)" → ("XC7A75T", "Artix-7").</summary>
    private static (string Part, string Family) SplitDeviceName(string name)
    {
        if (name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            return ("Unknown part", "Xilinx, not in the supported list");

        var open = name.IndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !name.EndsWith(')'))
            return (name, "Xilinx 7-series");
        return (name[..open], name[(open + 2)..^1]);
    }

    private async Task CopyDnaAsync()
    {
        if (!HasDna) return;
        var dnaToCopy = Dna;

        try
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop?.MainWindow);
            if (topLevel?.Clipboard != null)
            {
                await topLevel.Clipboard.SetTextAsync(dnaToCopy);
            }
            _logService.Info($"DNA copied to clipboard: {dnaToCopy}");

            CopyDnaLabel = "Copied";
            await Task.Delay(1500);
            CopyDnaLabel = "Copy";
        }
        catch (Exception ex)
        {
            _logService.Error($"Failed to copy DNA: {ex.Message}");
        }
    }
}
