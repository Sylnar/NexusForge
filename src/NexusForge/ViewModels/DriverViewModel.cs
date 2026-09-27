using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Sylnar.Services;

namespace Sylnar.ViewModels;

/// <summary>The Drivers page: both USB links side by side.</summary>
public class DriverViewModel : BaseViewModel
{
    private readonly AsyncRelayCommand _checkBothCommand;

    /// <summary>CH347 USB-JTAG: detection, DNA and flashing.</summary>
    public DriverStatusViewModel Ch347 { get; }

    /// <summary>FTDI FT601 USB 3 data port: DMA transfers and speed tests.</summary>
    public DriverStatusViewModel Ft601 { get; }

    public ICommand CheckBothCommand => _checkBothCommand;

    public DriverViewModel(DriverService driverService, FtdiDriverService ftdiService, LogService logService)
    {
        Ch347 = new DriverStatusViewModel(
            new DriverCopy(
                Title: "CH347 · JTAG",
                Subtitle: "Detection, DNA and flashing",
                ShortName: "CH347 JTAG",
                BundledDriver: "CH341WDM (bundled, signed)",
                ReadyMessage: "Driver active. JTAG is ready for detection and flashing.",
                MissingMessage: "The JTAG cable is connected but has no driver. Install it, then replug the USB cable.",
                NotConnectedMessage: "No CH347 found. Plug in the JTAG USB cable (the smaller port on the board), then press Check."),
            "1A86:55DD",
            driverService.CheckDriverAsync,
            driverService.InstallDriverAsync,
            driverService.UninstallDriverAsync,
            logService);

        Ft601 = new DriverStatusViewModel(
            new DriverCopy(
                Title: "FT601 · USB 3 data",
                Subtitle: "DMA transfers and speed tests",
                ShortName: "FT601 Data",
                BundledDriver: "FTD3XXWU (bundled)",
                ReadyMessage: "Driver active. The DMA data path is ready.",
                MissingMessage: "The data cable is connected but has no driver. Install it, then replug the USB 3 cable.",
                NotConnectedMessage: "No FT601 found. Connect the USB 3 data cable, then press Check.",
                WrongDriverMessage: "An old FTDI driver is bound. LeechCore needs the WinUSB SuperSpeed driver: install to replace it."),
            "0403:601F",
            ftdiService.CheckDriverAsync,
            ftdiService.InstallDriverAsync,
            ftdiService.UninstallDriverAsync,
            logService);

        _checkBothCommand = new AsyncRelayCommand(CheckBothAsync, () => !Ch347.IsBusy && !Ft601.IsBusy);
        Ch347.PropertyChanged += OnDriverChanged;
        Ft601.PropertyChanged += OnDriverChanged;
    }

    public Task CheckBothAsync() => Task.WhenAll(Ch347.CheckAsync(), Ft601.CheckAsync());

    private void OnDriverChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DriverStatusViewModel.IsBusy))
            _checkBothCommand.NotifyCanExecuteChanged();
    }
}
