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
    private string _lastReadHex = "—";
    private string _lastReadParsed = "—";
    private string _statusText = "Idle";
    private string _statusColor = "#6E7681";
    private long _pollSamples;
    private CancellationTokenSource? _pollCts;

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
            ((AsyncRelayCommand)ReadOnceCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartPollCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        set
        {
            SetProperty(ref _isPolling, value);
            ((AsyncRelayCommand)ReadOnceCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartPollCommand).NotifyCanExecuteChanged();
            ((RelayCommand)StopPollCommand).NotifyCanExecuteChanged();
        }
    }

    public string LastReadHex
    {
        get => _lastReadHex;
        set => SetProperty(ref _lastReadHex, value);
    }

    public string LastReadParsed
    {
        get => _lastReadParsed;
        set => SetProperty(ref _lastReadParsed, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public long PollSamples
    {
        get => _pollSamples;
        set => SetProperty(ref _pollSamples, value);
    }

    public ICommand ReadOnceCommand        { get; }
    public ICommand StartPollCommand       { get; }
    public ICommand StopPollCommand        { get; }
    public ICommand TestConnectionCommand  { get; }
    public ICommand ReadFpgaCfgCommand     { get; }
    public ICommand EnumerateDevicesCommand { get; }

    /// <summary>One line per device found, formatted for the UI list.</summary>
    public ObservableCollection<string> DeviceList { get; } = new();

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

    private async Task TestConnectionAsync()
    {
        IsProbing = true;
        SetStatus("Testing FPGA link (read RAM at 0x100000)...", "#58A6FF");
        try
        {
            var data = await Task.Run(() => _probe.TestConnection());
            LastReadHex = ToHex(data);
            LastReadParsed = ParseAsDwords(data);
            SetStatus("Link OK: read 16 bytes from system RAM at 0x100000", "#3FB950");
            _log.Info("BarProbe: link test passed");
        }
        catch (Exception ex)
        {
            LastReadHex = "(test failed)";
            LastReadParsed = ex.Message;
            SetStatus("Link test failed - check FTDI driver / FPGA / firmware", "#F85149");
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
        SetStatus("Reading FPGA PCIe config space...", "#58A6FF");
        try
        {
            var data = await Task.Run(() => _probe.ReadFpgaConfigSpace());
            LastReadHex = ToHex(data);
            LastReadParsed = ParseConfigSpace(data);
            SetStatus($"Read {data.Length} B of FPGA config space (Type 0 cfg TLPs, no P2P needed)", "#3FB950");
            _log.Info($"BarProbe: read {data.Length} B FPGA config space");
        }
        catch (Exception ex)
        {
            LastReadHex = "(config read failed)";
            LastReadParsed = ex.Message;
            SetStatus("FPGA config read failed", "#F85149");
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
        SetStatus("Enumerating PCIe devices via WMI...", "#58A6FF");
        try
        {
            var list = await Task.Run(() => _pciEnum.Enumerate());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => DeviceList.Clear());
            int withMem = 0;
            foreach (var d in list)
            {
                foreach (var b in d.Bars)
                {
                    var sizeStr = b.Size >= 1024 * 1024
                        ? $"{b.Size / (1024 * 1024)} MB"
                        : (b.Size >= 1024 ? $"{b.Size / 1024} KB" : $"{b.Size} B");
                    var line = $"VID:{d.Vendor} DID:{d.Device}  0x{b.Start:X8}..0x{b.End:X8} ({sizeStr})  {d.Name}";
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => DeviceList.Add(line));
                    withMem++;
                }
            }
            SetStatus($"Found {list.Count} PCIe devices, {withMem} memory BARs (click an entry to copy address)", "#3FB950");
            _log.Info($"BarProbe: enumerated {list.Count} PCIe devices, {withMem} BARs");
        }
        catch (Exception ex)
        {
            SetStatus("Enumeration failed", "#F85149");
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
        SetStatus($"Reading 0x{addr:X16} ({len} bytes)...", "#58A6FF");
        try
        {
            var data = await Task.Run(() => _probe.ReadOnce(addr, len));
            LastReadHex = ToHex(data);
            LastReadParsed = ParseAsDwords(data);
            SetStatus($"Read OK at 0x{addr:X16}", "#3FB950");
            _log.Info($"BarProbe: read {data.Length} B at 0x{addr:X16}");
        }
        catch (Exception ex)
        {
            LastReadHex = "(read failed)";
            LastReadParsed = ex.Message;
            SetStatus("Read failed", "#F85149");
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
        SetStatus($"Polling -> {Path.GetFileName(path)}", "#58A6FF");
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
        SetStatus($"Poll stopped. Samples={PollSamples}, file={Path.GetFileName(path)}", "#3FB950");
        _log.Info($"BarProbe poll stopped: {PollSamples} samples in {path}");
    }

    private void StopPoll()
    {
        _pollCts?.Cancel();
        _log.Info("BarProbe stop requested");
    }

    private void SetStatus(string text, string color)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText  = text;
            StatusColor = color;
        });
    }

    // ---- parsing helpers ---------------------------------------------------

    private bool TryParseAddress(string s, out ulong addr)
    {
        addr = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trim = s.Trim();
        if (trim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trim = trim[2..];
        if (!ulong.TryParse(trim, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr))
        {
            SetStatus("Address must be hex (e.g. DCC001F0 or 0xDCC001F0)", "#F85149");
            return false;
        }
        return true;
    }

    private bool TryParseLength(string s, out uint len)
    {
        len = 0;
        if (!uint.TryParse(s, out len) || len == 0 || len > 4096)
        {
            SetStatus("Length must be 1..4096 bytes", "#F85149");
            return false;
        }
        return true;
    }

    private bool TryParsePeriod(string s, out int period)
    {
        period = 0;
        if (!int.TryParse(s, out period) || period < 10 || period > 60_000)
        {
            SetStatus("Period must be 10..60000 ms", "#F85149");
            return false;
        }
        return true;
    }

    private static string ToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("X2"));
            if (i + 1 < data.Length) sb.Append(' ');
            if ((i + 1) % 16 == 0 && i + 1 < data.Length) sb.AppendLine();
        }
        return sb.ToString();
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

    private static string ParseAsDwords(byte[] data)
    {
        if (data.Length % 4 != 0)
            return "(length not 4-byte aligned)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            uint dw = BitConverter.ToUInt32(data, i);
            sb.AppendLine($"+0x{i:X3}: 0x{dw:X8}  ({dw})");
        }
        return sb.ToString().TrimEnd();
    }
}
