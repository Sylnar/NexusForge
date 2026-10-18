using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Sylnar.Services;
using Sylnar.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Sylnar.Views;

public partial class MainWindow : Window
{
    private bool _cleanupDone;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        (DataContext as MainViewModel)?.OnWindowOpened();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // async void on a window event is unavoidable here (Avalonia API shape) but
        // EVERY await must be wrapped — an unhandled exception in this method goes
        // to the dispatcher and silently terminates the process, leaving no log.
        if (_cleanupDone)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        try
        {
            Hide();

            var cleanupWindow = new ShutdownWindow();
            cleanupWindow.Show();

            await Task.Run(() =>
            {
                try
                {
                    var app = (App)Avalonia.Application.Current!;
                    var services = app.Services;
                    if (services != null)
                    {
                        try { services.GetService<NativeJtagService>()?.Dispose(); } catch { }
                        try { services.GetService<DmaTestService>()?.Cleanup(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    Helpers.CrashLogger.WriteLine($"OnClosing service-disposal error: {ex.Message}");
                }

                try { SpawnCleanupProcess(); }
                catch (Exception ex)
                {
                    Helpers.CrashLogger.WriteLine($"OnClosing SpawnCleanupProcess error: {ex.Message}");
                }
            });

            await Task.Delay(2000);

            cleanupWindow.Close();
            _cleanupDone = true;
            // MarkCleanExit so Program.cs knows the next launch doesn't need to
            // wipe the .NET single-file extraction cache.
            Helpers.CrashLogger.MarkCleanExit();
            Dispatcher.UIThread.Post(() => Close());
        }
        catch (Exception ex)
        {
            Helpers.CrashLogger.LogException("MainWindow.OnClosing", ex);
            // We must complete shutdown even on error — re-allow the close so the
            // user isn't left with a hung window.
            _cleanupDone = true;
            Dispatcher.UIThread.Post(() => Close());
        }
    }

    private static void SpawnCleanupProcess()
    {
        try
        {
            var pid = Environment.ProcessId;
            var tempDir = Path.GetTempPath().TrimEnd('\\');
            var batPath = Path.Combine(tempDir, $"nf_cleanup_{pid}.bat");

            // Delete only the folders this process created (anything still locked
            // after service disposal), never a %TEMP% wildcard.
            var rdLines = string.Join("\r\n", Helpers.OwnedTempDirs.Snapshot()
                .Where(Directory.Exists)
                .Select(d => $"rd /s /q \"{d}\" 2>nul"));

            var bat = $"""
                @echo off
                rem File is written as UTF-8; switch cmd so non-ASCII profile paths parse.
                chcp 65001 >nul
                :wait
                tasklist /FI "PID eq {pid}" 2>nul | findstr /I "{pid}" >nul
                if not errorlevel 1 (
                    ping -n 2 127.0.0.1 >nul
                    goto :wait
                )
                ping -n 2 127.0.0.1 >nul
                {rdLines}
                del /f /q "{batPath}" 2>nul
                """;

            File.WriteAllText(batPath, bat);

            Process.Start(new ProcessStartInfo
            {
                FileName = batPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();
        }
        catch { }
    }
}
