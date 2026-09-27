using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Models;
using Sylnar.Services;

namespace Sylnar.ViewModels;

/// <summary>Per-driver wording for a <see cref="DriverStatusViewModel"/> card.</summary>
public sealed record DriverCopy(
    string Title,
    string Subtitle,
    string ShortName,
    string BundledDriver,
    string ReadyMessage,
    string MissingMessage,
    string NotConnectedMessage,
    string WrongDriverMessage = "");

/// <summary>
/// One USB link's driver (CH347 JTAG or FT601 data): its last check, the
/// Check / Install / Uninstall actions, and how the card and sidebar show it.
/// </summary>
public sealed class DriverStatusViewModel : BaseViewModel
{
    private enum State { NotChecked, Installed, Missing, WrongDriver, NotConnected, CheckFailed }

    private const string Empty = "—";

    private readonly DriverCopy _copy;
    private readonly Func<Task<DriverInfo>> _check;
    private readonly Func<Task<bool>> _install;
    private readonly Func<string, Task<bool>> _uninstall;
    private readonly LogService _log;
    private readonly AsyncRelayCommand _checkCommand;
    private readonly AsyncRelayCommand _installCommand;
    private readonly AsyncRelayCommand _uninstallCommand;

    private State _state = State.NotChecked;
    private bool _isBusy;
    private string _busyMessage = string.Empty;
    private string _busyPill = string.Empty;
    private string? _actionNote;
    private string _deviceName = Empty;
    private string _version = Empty;
    private string _driverType = Empty;
    private string _vidPid;
    private string _infPath = string.Empty;

    public DriverStatusViewModel(
        DriverCopy copy,
        string vidPid,
        Func<Task<DriverInfo>> check,
        Func<Task<bool>> install,
        Func<string, Task<bool>> uninstall,
        LogService log)
    {
        _copy = copy;
        _vidPid = vidPid;
        _check = check;
        _install = install;
        _uninstall = uninstall;
        _log = log;

        _checkCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        _installCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy);
        // Nothing to remove when the device is present without a driver.
        _uninstallCommand = new AsyncRelayCommand(UninstallAsync,
            () => !IsBusy && _state is not (State.NotChecked or State.Missing));
    }

    public string Title => _copy.Title;
    public string Subtitle => _copy.Subtitle;
    public string ShortName => _copy.ShortName;

    public ICommand CheckCommand => _checkCommand;
    public ICommand InstallCommand => _installCommand;
    public ICommand UninstallCommand => _uninstallCommand;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _checkCommand.NotifyCanExecuteChanged();
            _installCommand.NotifyCanExecuteChanged();
            _uninstallCommand.NotifyCanExecuteChanged();
            RaiseStatusChanged();
        }
    }

    public bool IsDriverOk => _state == State.Installed;

    // ── Card ──────────────────────────────────────────────────────────────

    public string StatusText => IsBusy ? _busyPill : _state switch
    {
        State.Installed => "Installed",
        State.Missing => "Missing",
        State.WrongDriver => "Old driver",
        State.NotConnected => "Not connected",
        State.CheckFailed => "Check failed",
        _ => "Not checked"
    };

    public Tone StatusTone => IsBusy ? Tone.Info : _state switch
    {
        State.Installed => Tone.Ok,
        State.Missing or State.WrongDriver => Tone.Warn,
        State.CheckFailed => Tone.Danger,
        _ => Tone.Neutral
    };

    /// <summary>Card border and icon tile colour.</summary>
    public Tone CardTone => _state switch
    {
        State.Installed => Tone.Ok,
        State.Missing or State.WrongDriver => Tone.Warn,
        State.CheckFailed => Tone.Danger,
        _ => Tone.Neutral
    };

    public string Message => IsBusy ? _busyMessage : _actionNote ?? _state switch
    {
        State.Installed => _copy.ReadyMessage,
        State.Missing => _copy.MissingMessage,
        State.WrongDriver => _copy.WrongDriverMessage,
        State.NotConnected => _copy.NotConnectedMessage,
        State.CheckFailed => "The driver check did not finish. See the log for details, then try again.",
        _ => "Not checked yet. Press Check to read the driver status."
    };

    public Tone MessageTone => IsBusy ? Tone.Info : _actionNote != null ? Tone.Warn : _state switch
    {
        State.Installed => Tone.Ok,
        State.Missing or State.WrongDriver => Tone.Warn,
        State.CheckFailed => Tone.Danger,
        _ => Tone.Neutral
    };

    public string DeviceText => _state switch
    {
        State.Missing => $"{_deviceName} (no driver)",
        State.NotChecked or State.NotConnected or State.CheckFailed => Empty,
        _ => _deviceName
    };

    public string VidPidText => _vidPid.Replace(":", " : ");

    public string VersionText => _state is State.Installed or State.WrongDriver ? _version : Empty;

    public bool HasVersion => VersionText != Empty;

    /// <summary>The bound driver, or the bundled package Install will use.</summary>
    public string DriverText => (_state is State.Installed or State.WrongDriver) && _driverType != Empty
        ? _driverType
        : _copy.BundledDriver;

    // ── Sidebar ───────────────────────────────────────────────────────────

    public string SidebarText => IsBusy ? _busyPill : _state switch
    {
        State.Installed => "Ready",
        State.Missing => "Driver missing",
        State.WrongDriver => "Old driver",
        State.NotConnected => "Not connected",
        State.CheckFailed => "Check failed",
        _ => "Not checked"
    };

    public Tone SidebarTone => IsBusy ? Tone.Neutral : StatusTone;

    // ── Actions ───────────────────────────────────────────────────────────

    public async Task CheckAsync()
    {
        if (IsBusy) return;
        SetBusy("Checking…", "Checking driver status…");
        try
        {
            await CheckCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckCoreAsync()
    {
        _actionNote = null;
        try
        {
            var info = await _check();

            _deviceName = info.DeviceName;
            _version = string.IsNullOrWhiteSpace(info.Version) ? Empty : info.Version;
            _driverType = string.IsNullOrWhiteSpace(info.DriverType) ? Empty : info.DriverType;
            _vidPid = info.VidPid;
            _infPath = info.InfPath;

            _state = info.Status == "Check failed" ? State.CheckFailed
                : !info.IsDeviceDetected ? State.NotConnected
                : info.IsDriverOk ? State.Installed
                : info.Status == "Wrong Driver" ? State.WrongDriver
                : State.Missing;
        }
        catch (Exception ex)
        {
            _log.Error($"{ShortName} driver check failed: {ex.Message}");
            _state = State.CheckFailed;
        }
        _uninstallCommand.NotifyCanExecuteChanged();
        RaiseStatusChanged();
    }

    private async Task InstallAsync()
    {
        SetBusy("Installing…", "Installing driver (administrator rights needed)…");
        try
        {
            bool ok = await _install();

            _busyMessage = "Verifying installation…";
            OnPropertyChanged(nameof(Message));
            if (ok) await Task.Delay(1500);
            await CheckCoreAsync();

            // Not bound yet: usually a replug or, if Windows asked, a restart.
            if (!ok && _state != State.Installed)
                _actionNote = "The driver was added but is not active yet. Replug the USB cable and press Check; restart first if Windows asked for it.";
        }
        catch (Exception ex)
        {
            _log.Error($"{ShortName} driver install failed: {ex.Message}");
            _actionNote = "The install did not finish. See the log for the reason.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallAsync()
    {
        SetBusy("Removing…", "Removing driver (administrator rights needed)…");
        try
        {
            bool ok = await _uninstall(_infPath);
            if (ok)
            {
                _busyMessage = "Verifying removal…";
                OnPropertyChanged(nameof(Message));
                await Task.Delay(1500);
            }
            await CheckCoreAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"{ShortName} driver removal failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetBusy(string pill, string message)
    {
        _busyPill = pill;
        _busyMessage = message;
        IsBusy = true;
    }

    private void RaiseStatusChanged()
    {
        OnPropertyChanged(nameof(IsDriverOk));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(CardTone));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(MessageTone));
        OnPropertyChanged(nameof(DeviceText));
        OnPropertyChanged(nameof(VidPidText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(HasVersion));
        OnPropertyChanged(nameof(DriverText));
        OnPropertyChanged(nameof(SidebarText));
        OnPropertyChanged(nameof(SidebarTone));
    }
}
