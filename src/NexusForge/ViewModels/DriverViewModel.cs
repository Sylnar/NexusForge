using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Services;

namespace Sylnar.ViewModels;

public class DriverViewModel : BaseViewModel
{
    private readonly DriverService _driverService;
    private readonly LogService    _logService;

    private bool   _isBusy;
    private bool   _isChecked;
    private bool   _isDriverOk;
    private bool   _isDeviceDetected;
    private string _statusText    = "Not checked";
    private string _statusColor   = "#6E7681";
    private string _versionText   = "—";
    private string _deviceName    = "—";
    private string _driverType    = "—";
    private string _vidPid        = "1A86:55DD";
    private string _infPath       = string.Empty;
    private string _busyMessage   = string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            ((AsyncRelayCommand)CheckDriverCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)InstallDriverCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)UninstallDriverCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                OnPropertyChanged(nameof(ShowNotDetectedWarning));
                OnPropertyChanged(nameof(ShowDriverMissing));
            }
        }
    }

    public bool IsDriverOk
    {
        get => _isDriverOk;
        set
        {
            if (SetProperty(ref _isDriverOk, value))
                OnPropertyChanged(nameof(ShowDriverMissing));
        }
    }

    public bool IsDeviceDetected
    {
        get => _isDeviceDetected;
        set
        {
            if (SetProperty(ref _isDeviceDetected, value))
            {
                OnPropertyChanged(nameof(ShowNotDetectedWarning));
                OnPropertyChanged(nameof(ShowDriverMissing));
            }
        }
    }

    public bool ShowNotDetectedWarning => IsChecked && !IsDeviceDetected;

    public bool ShowDriverMissing => IsChecked && IsDeviceDetected && !IsDriverOk;

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

    public string VersionText
    {
        get => _versionText;
        set => SetProperty(ref _versionText, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public string DriverType
    {
        get => _driverType;
        set => SetProperty(ref _driverType, value);
    }

    public string VidPid
    {
        get => _vidPid;
        set => SetProperty(ref _vidPid, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        set => SetProperty(ref _busyMessage, value);
    }

    public ICommand CheckDriverCommand    { get; }
    public ICommand InstallDriverCommand  { get; }
    public ICommand UninstallDriverCommand { get; }

    public DriverViewModel(DriverService driverService, LogService logService)
    {
        _driverService = driverService;
        _logService    = logService;

        CheckDriverCommand     = new AsyncRelayCommand(CheckDriverAsync,     () => !IsBusy);
        InstallDriverCommand   = new AsyncRelayCommand(InstallDriverAsync,   () => !IsBusy);
        UninstallDriverCommand = new AsyncRelayCommand(UninstallDriverAsync, () => !IsBusy && IsChecked);
    }

    private async Task CheckDriverAsync()
    {
        IsBusy = true;
        BusyMessage = "Checking driver status...";

        try
        {
            var info = await _driverService.CheckDriverAsync();

            IsDeviceDetected = info.IsDeviceDetected;
            IsDriverOk       = info.IsDriverOk;
            VersionText      = info.Version;
            DeviceName       = info.DeviceName;
            DriverType       = info.DriverType;
            VidPid           = info.VidPid;
            _infPath         = info.InfPath;

            StatusText  = info.Status;
            StatusColor = info.IsDriverOk
                ? "#3FB950"
                : (info.IsDeviceDetected ? "#E3B341" : "#F85149");

            IsChecked = true;

            ((AsyncRelayCommand)UninstallDriverCommand).NotifyCanExecuteChanged();
        }
        finally
        {
            IsBusy      = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task InstallDriverAsync()
    {
        IsBusy = true;
        BusyMessage = "Downloading driver...";

        try
        {
            bool ok = await _driverService.InstallDriverAsync();

            if (ok)
            {
                BusyMessage = "Verifying installation...";
                await Task.Delay(1500);
                await CheckDriverAsync();
                return;
            }
            StatusText  = "Install in browser";
            StatusColor = "#E3B341";
        }
        finally
        {
            IsBusy      = false;
            BusyMessage = string.Empty;
        }
    }

    private async Task UninstallDriverAsync()
    {
        IsBusy = true;
        BusyMessage = "Removing driver (elevation required)...";

        try
        {
            bool ok = await _driverService.UninstallDriverAsync(_infPath);

            if (ok)
            {
                BusyMessage = "Verifying removal...";
                await Task.Delay(1500);
                await CheckDriverAsync();
            }
        }
        finally
        {
            IsBusy      = false;
            BusyMessage = string.Empty;
        }
    }
}
