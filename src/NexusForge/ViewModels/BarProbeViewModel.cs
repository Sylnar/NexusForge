using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class BarProbeViewModel : BaseViewModel
{
    private readonly BarProbeService _probe;
    private readonly PciEnumService _pciEnum;
    private readonly LogService _log;

    // Inputs
    private string _addressHex = "";            // user must enter a real address
    private string _length     = "256";         // bytes
    private string _periodMs   = "100";         // poll interval
    private string _logFile    = "";            // poll log path (auto-filled if empty)

    // State
    private bool _isProbing;
    private bool _isPolling;
    private string _statusText = "Idle";
    private Tone _statusTone = Tone.Neutral;
    private long _pollSamples;
    private CancellationTokenSource? _pollCts;
    private bool _devicesRequested;

    // Last read
    private string _lastReadInfo = "Nothing read yet";
    private string _lastReadError = "";
    private string _decodedText = "";
    private PciBarRow? _selectedBar;

    public string AddressHex
    {
        get => _addressHex;
        set => SetProperty(ref _addressHex, value);
    }

    public string Length
    {
        get => _length;
        set => SetProperty(ref _length, value);
    }

    public string PeriodMs
    {
        get => _periodMs;
        set => SetProperty(ref _periodMs, value);
    }

    public string LogFile
    {
        get => _logFile;
        set => SetProperty(ref _logFile, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        set
        {
            SetProperty(ref _isProbing, value);
            NotifyCommands();
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        set
        {
            SetProperty(ref _isPolling, value);
            NotifyCommands();
            OnPropertyChanged(nameof(SamplesText));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public Tone StatusTone
    {
        get => _statusTone;
        set => SetProperty(ref _statusTone, value);
    }

    public long PollSamples
    {
        get => _pollSamples;
        set
        {
            if (SetProperty(ref _pollSamples, value))
                OnPropertyChanged(nameof(SamplesText));
        }
    }

    public string SamplesText => IsPolling || PollSamples > 0 ? $"{PollSamples:N0} samples logged" : "";

    // ── Last read ───────────────────────────────────────────────────────────

    /// <summary>"0x0000000000100000 · 16 B".</summary>
    public string LastReadInfo
    {
        get => _lastReadInfo;
        private set => SetProperty(ref _lastReadInfo, value);
    }

    public ObservableCollection<HexRow> HexRows { get; } = new();

    public ObservableCollection<DwordCell> Dwords { get; } = new();

    /// <summary>Decoded PCIe config header (config-space reads only).</summary>
    public string DecodedText
    {
        get => _decodedText;
        private set
        {
            if (SetProperty(ref _decodedText, value))
                OnPropertyChanged(nameof(HasDecoded));
        }
    }

    public bool HasDecoded => !string.IsNullOrEmpty(DecodedText);

    public bool HasDwords => Dwords.Count > 0;

    public string LastReadError
    {
        get => _lastReadError;
        private set
        {
            if (SetProperty(ref _lastReadError, value))
                OnPropertyChanged(nameof(HasLastReadError));
        }
    }

    public bool HasLastReadError => !string.IsNullOrEmpty(LastReadError);

    public bool HasRead => HexRows.Count > 0;

    public bool HasNoRead => HexRows.Count == 0 && !HasLastReadError;

    // ── PCIe devices ────────────────────────────────────────────────────────

    /// <summary>One row per memory BAR of each host PCIe device.</summary>
    public ObservableCollection<PciBarRow> Devices { get; } = new();

    public bool HasDevices => Devices.Count > 0;

    /// <summary>Picking a row copies its BAR start into Address.</summary>
    public PciBarRow? SelectedBar
    {
        get => _selectedBar;
        set
        {
            if (SetProperty(ref _selectedBar, value) && value != null)
                AddressHex = value.Start.ToString("X", CultureInfo.InvariantCulture);
        }
    }

    public ICommand ReadOnceCommand        { get; }
    public ICommand StartPollCommand       { get; }
    public ICommand StopPollCommand        { get; }
    public ICommand TestConnectionCommand  { get; }
    public ICommand ReadFpgaCfgCommand     { get; }
    public ICommand EnumerateDevicesCommand { get; }

    public BarProbeViewModel(BarProbeService probe, PciEnumService pciEnum, LogService log)
    {
        _probe   = probe;
        _pciEnum = pciEnum;
        _log     = log;

        ReadOnceCommand         = new AsyncRelayCommand(ReadOnceAsync,         () => !IsProbing && !IsPolling);
        StartPollCommand        = new AsyncRelayCommand(StartPollAsync,        () => !IsProbing && !IsPolling);
        StopPollCommand         = new RelayCommand(StopPoll,                   () => IsPolling);
        TestConnectionCommand   = new AsyncRelayCommand(TestConnectionAsync,   () => !IsProbing && !IsPolling);
        ReadFpgaCfgCommand      = new AsyncRelayCommand(ReadFpgaCfgAsync,      () => !IsProbing && !IsPolling);
        EnumerateDevicesCommand = new AsyncRelayCommand(EnumerateDevicesAsync, () => !IsProbing && !IsPolling);
    }

    /// <summary>First visit to the page: list the host's PCIe devices (a WMI query, no DMA).</summary>
    public void OnShown()
    {
        if (_devicesRequested) return;
        _devicesRequested = true;
        if (EnumerateDevicesCommand.CanExecute(null))
            EnumerateDevicesCommand.Execute(null);
    }

    private void NotifyCommands()
    {
        ((AsyncRelayCommand)ReadOnceCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)StartPollCommand).NotifyCanExecuteChanged();
        ((RelayCommand)StopPollCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)TestConnectionCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ReadFpgaCfgCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)EnumerateDevicesCommand).NotifyCanExecuteChanged();
    }

    private async Task TestConnectionAsync()
    {
        IsProbing = true;
        SetStatus("Testing FPGA link (read RAM at 0x100000)...", Tone.Info);
        try
        {
            var data = await Task.Run(() => _probe.TestConnection());
            ShowRead(0x100000, data, decoded: null);
            SetStatus("Link OK: read 16 bytes from system RAM at 0x100000", Tone.Ok);
            _log.Info("BarProbe: link test passed");
        }
        catch (Exception ex)
        {
            ShowReadError("Link test failed", ex.Message);
            SetStatus("Link test failed - check FTDI driver / FPGA / firmware", Tone.Danger);
            _log.Error($"BarProbe link test failed: {ex.Message}");
        }
        finally
        {
            IsProbing = false;
        }
    }

    private async Task ReadFpgaCfgAsync()
    {
        IsProbing = true;
        SetStatus("Reading FPGA PCIe config space...", Tone.Info);
        try
        {
            var data = await Task.Run(() => _probe.ReadFpgaConfigSpace());
            ShowRead(null, data, ParseConfigSpace(data));
            SetStatus($"Read {data.Length} B of FPGA config space (Type 0 cfg TLPs, no P2P needed)", Tone.Ok);
            _log.Info($"BarProbe: read {data.Length} B FPGA config space");
        }
        catch (Exception ex)
        {
            ShowReadError("Config read failed", ex.Message);
            SetStatus("FPGA config read failed", Tone.Danger);
            _log.Error($"BarProbe FPGA config read failed: {ex.Message}");
        }
        finally
        {
            IsProbing = false;
        }
    }

    private async Task EnumerateDevicesAsync()
    {
        IsProbing = true;
        SetStatus("Listing PCIe devices via WMI...", Tone.Info);
        try
        {
            var list = await Task.Run(() => _pciEnum.Enumerate());
            var rows = new List<PciBarRow>();
            foreach (var d in list)
            {
                foreach (var b in d.Bars)
                {
                    var sizeStr = b.Size >= 1024 * 1024
                        ? $"{b.Size / (1024 * 1024)} MB"
                        : (b.Size >= 1024 ? $"{b.Size / 1024} KB" : $"{b.Size} B");
                    rows.Add(new PciBarRow(d.Name, $"{d.Vendor}:{d.Device}", b.Start, b.End, sizeStr));
                }
            }

            Devices.Clear();
            foreach (var row in rows)
                Devices.Add(row);
            OnPropertyChanged(nameof(HasDevices));

            SetStatus($"Found {list.Count} PCIe devices, {rows.Count} memory BARs", Tone.Ok);
            _log.Info($"BarProbe: enumerated {list.Count} PCIe devices, {rows.Count} BARs");
        }
        catch (Exception ex)
        {
            SetStatus("Listing PCIe devices failed", Tone.Danger);
            _log.Error($"BarProbe enumeration failed: {ex.Message}");
        }
        finally
        {
            IsProbing = false;
        }
    }

    private async Task ReadOnceAsync()
    {
        if (!TryParseAddress(AddressHex, out var addr) || !TryParseLength(Length, out var len))
            return;

        IsProbing = true;
        SetStatus($"Reading 0x{addr:X16} ({len} bytes)...", Tone.Info);
        try
        {
            var data = await Task.Run(() => _probe.ReadOnce(addr, len));
            ShowRead(addr, data, decoded: null);
            SetStatus($"Read OK at 0x{addr:X16}", Tone.Ok);
            _log.Info($"BarProbe: read {data.Length} B at 0x{addr:X16}");
        }
        catch (Exception ex)
        {
            ShowReadError($"Read failed at 0x{addr:X16}", ex.Message);
            SetStatus("Read failed", Tone.Danger);
            _log.Error($"BarProbe read failed: {ex.Message}");
        }
        finally
        {
            IsProbing = false;
        }
    }

    private async Task StartPollAsync()
    {
        if (!TryParseAddress(AddressHex, out var addr) ||
            !TryParseLength(Length, out var len) ||
            !TryParsePeriod(PeriodMs, out var period))
            return;

        var path = string.IsNullOrWhiteSpace(LogFile)
            ? Path.Combine(Path.GetTempPath(), $"barprobe_{DateTime.Now:yyyyMMdd_HHmmss}.log")
            : LogFile;
        LogFile = path;

        IsPolling = true;
        PollSamples = 0;
        SetStatus($"Polling -> {Path.GetFileName(path)}", Tone.Info);
        _log.Info($"BarProbe poll start: addr=0x{addr:X16} len={len} period={period}ms log={path}");

        var cts = new CancellationTokenSource();
        _pollCts = cts;
        var ct = cts.Token;

        // Background poll loop. UI updates via dispatcher inside the loop.
        var pollTask = Task.Run(async () =>
        {
            try
            {
                await _probe.PollAsync(addr, len, period, path, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"BarProbe poll loop crashed: {ex.Message}");
            }
        });

        // UI sample counter — read the log file size every second as a rough proxy.
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var lines = 0L;
                        using var sr = new StreamReader(path);
                        while (await sr.ReadLineAsync() is not null) lines++;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => PollSamples = lines);
                    }
                }
                catch { /* file in use, skip */ }
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
        });

        await pollTask;

        // The poll can end on its own (read failures, connect error); stop the
        // sample-counter loop too, or it keeps re-reading the log file forever.
        cts.Cancel();
        if (ReferenceEquals(_pollCts, cts)) _pollCts = null;
        cts.Dispose();

        IsPolling = false;
        SetStatus($"Poll stopped. Samples={PollSamples}, file={Path.GetFileName(path)}", Tone.Ok);
        _log.Info($"BarProbe poll stopped: {PollSamples} samples in {path}");
    }

    private void StopPoll()
    {
        _pollCts?.Cancel();
        _log.Info("BarProbe stop requested");
    }

    private void SetStatus(string text, Tone tone)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = text;
            StatusTone = tone;
        });
    }

    // ---- last-read display -------------------------------------------------

    /// <summary>Shows a successful read: hex rows plus DWORDs, or the decoded config header.</summary>
    private void ShowRead(ulong? address, byte[] data, string? decoded)
    {
        LastReadError = "";
        LastReadInfo = address is { } a
            ? $"0x{a:X16} · {data.Length} B"
            : $"FPGA config space · {data.Length} B";

        HexRows.Clear();
        var offsetFormat = data.Length > 0x100 ? "X3" : "X2";
        for (int i = 0; i < data.Length; i += 16)
        {
            var bytes = new StringBuilder(47);
            for (int j = i; j < Math.Min(i + 16, data.Length); j++)
            {
                if (j > i) bytes.Append(' ');
                bytes.Append(data[j].ToString("X2", CultureInfo.InvariantCulture));
            }
            HexRows.Add(new HexRow("+" + i.ToString(offsetFormat, CultureInfo.InvariantCulture), bytes.ToString()));
        }

        Dwords.Clear();
        DecodedText = decoded ?? "";
        if (decoded == null && data.Length % 4 == 0)
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                uint dw = BitConverter.ToUInt32(data, i);
                Dwords.Add(new DwordCell($"{dw:X8}", $"+0x{i:X3}: 0x{dw:X8} ({dw})"));
            }
        }

        RaiseLastReadChanged();
    }

    private void ShowReadError(string title, string message)
    {
        LastReadInfo = title;
        LastReadError = message;
        HexRows.Clear();
        Dwords.Clear();
        DecodedText = "";
        RaiseLastReadChanged();
    }

    private void RaiseLastReadChanged()
    {
        OnPropertyChanged(nameof(HasRead));
        OnPropertyChanged(nameof(HasDwords));
        OnPropertyChanged(nameof(HasNoRead));
    }

    // ---- parsing helpers ---------------------------------------------------

    private bool TryParseAddress(string s, out ulong addr)
    {
        addr = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            SetStatus("Enter an address first (hex, e.g. 100000), or pick a PCIe device", Tone.Warn);
            return false;
        }
        var trim = s.Trim();
        if (trim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trim = trim[2..];
        if (!ulong.TryParse(trim, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr))
        {
            SetStatus("Address must be hex (e.g. DCC001F0 or 0xDCC001F0)", Tone.Danger);
            return false;
        }
        return true;
    }

    private bool TryParseLength(string s, out uint len)
    {
        len = 0;
        if (!uint.TryParse(s, out len) || len == 0 || len > 4096)
        {
            SetStatus("Length must be 1..4096 bytes", Tone.Danger);
            return false;
        }
        return true;
    }

    private bool TryParsePeriod(string s, out int period)
    {
        period = 0;
        if (!int.TryParse(s, out period) || period < 10 || period > 60_000)
        {
            SetStatus("Period must be 10..60000 ms", Tone.Danger);
            return false;
        }
        return true;
    }

    private static string ParseConfigSpace(byte[] data)
    {
        if (data.Length < 64) return "(config space too short)";
        var sb = new StringBuilder();
        ushort vid = (ushort)(data[0]  | (data[1]  << 8));
        ushort did = (ushort)(data[2]  | (data[3]  << 8));
        ushort cmd = (ushort)(data[4]  | (data[5]  << 8));
        ushort sts = (ushort)(data[6]  | (data[7]  << 8));
        byte rev   = data[8];
        uint  cls  = (uint)(data[9] | (data[10] << 8) | (data[11] << 16));
        sb.AppendLine($"Vendor    : 0x{vid:X4}");
        sb.AppendLine($"Device    : 0x{did:X4}");
        sb.AppendLine($"Class/PI  : 0x{cls:X6}  (rev 0x{rev:X2})");
        sb.AppendLine($"Command   : 0x{cmd:X4}");
        sb.AppendLine($"Status    : 0x{sts:X4}");
        sb.AppendLine();
        sb.AppendLine("BARs:");
        for (int i = 0; i < 6; i++)
        {
            int off = 0x10 + i * 4;
            if (off + 4 > data.Length) break;
            uint bar = BitConverter.ToUInt32(data, off);
            string kind = (bar & 1) == 1 ? "I/O " : "Mem";
            string masked = $"0x{bar & 0xFFFFFFF0:X8}";
            sb.AppendLine($"  BAR{i} = 0x{bar:X8}  ({kind} -> {masked})");
        }
        if (data.Length >= 0x40)
        {
            ushort subVid = (ushort)(data[0x2C] | (data[0x2D] << 8));
            ushort subDid = (ushort)(data[0x2E] | (data[0x2F] << 8));
            byte capPtr = data[0x34];
            sb.AppendLine();
            sb.AppendLine($"Sub VID/DID: 0x{subVid:X4}:0x{subDid:X4}");
            sb.AppendLine($"CapPtr     : 0x{capPtr:X2}");
        }
        return sb.ToString().TrimEnd();
    }
}
