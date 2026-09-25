using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sylnar.Helpers;
using Sylnar.Models;
using Sylnar.Services;
using Sylnar.ViewModels;
using Sylnar.Views;

namespace Sylnar;

public class App : Application
{
    private IServiceProvider? _services;
    public IServiceProvider? Services => _services;

    private bool _updating;
    private UpdateWindow? _updateWindow;

    public override void Initialize()
    {
        try
        {
            AvaloniaXamlLoader.Load(this);
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("App.Initialize/AvaloniaXamlLoader", ex);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // NB: this method is intentionally NOT async void anymore. Async-void on the
        // Avalonia init lifecycle silently terminates the process on any exception
        // (no managed handler can catch it before the dispatcher dies). Run startup
        // synchronously here, then fire-and-forget the post-init background tasks.
        try
        {
            InitializeFrameworkInternal();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("App.OnFrameworkInitializationCompleted", ex);
            throw;
        }
        finally
        {
            base.OnFrameworkInitializationCompleted();
        }
    }

    private void InitializeFrameworkInternal()
    {
        AntiTamper.Verify();

        if (!IntegrityGuard.IsValid())
        {
            CrashLogger.WriteLine("IntegrityGuard reported invalid — exiting.");
            Environment.Exit(IntegrityGuard.Probe());
            return;
        }

        _services = ConfigureServices();

        CleanupTempFolders();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(mainViewModel);

            // Show splash, then reveal main window when splash finishes.
            var splash = new SplashWindow();
            desktop.MainWindow = splash;

            splash.Closed += (_, _) =>
            {
                // If an update has taken over, the UpdateWindow owns the screen until the
                // swap + relaunch; do NOT reveal the main window underneath it.
                if (_updating) return;
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            };

            // Auto-update runs in the background after a short settle period. A visible
            // UpdateWindow with live progress is shown the moment an update is found, so
            // the app no longer silently vanishes during the swap (the flash-and-vanish
            // perception). Wrapped tightly so a network/parse blowup never bubbles to the
            // dispatcher.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1200);
                    var updater = _services.GetRequiredService<AutoUpdateService>();

                    var progress = new Progress<UpdateProgress>(p =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            switch (p.Stage)
                            {
                                case UpdateStage.Found:
                                    _updating = true;
                                    if (_updateWindow == null)
                                    {
                                        _updateWindow = new UpdateWindow();
                                        _updateWindow.Show();
                                        // Own MainWindow so closing the splash can't shut us down.
                                        desktop.MainWindow = _updateWindow;
                                    }
                                    if (mainWindow.IsVisible) mainWindow.Hide();
                                    _updateWindow.SetStatus($"Found v{p.Version}. Preparing download...");
                                    break;
                                case UpdateStage.Downloading:
                                    _updateWindow?.SetProgress(p.BytesReceived, p.TotalBytes);
                                    break;
                                case UpdateStage.Applying:
                                    _updateWindow?.SetApplying();
                                    break;
                            }
                        }));

                    bool shouldRestart = await updater.CheckAndApplyUpdateAsync(progress);

                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (shouldRestart)
                        {
                            CrashLogger.WriteLine("AutoUpdate: applying update, exiting cleanly.");
                            CrashLogger.MarkCleanExit();
                            Environment.Exit(0);
                        }
                        else if (_updating)
                        {
                            // Update was found but did not complete (download/verify failed):
                            // tear down the update UI and fall back to the normal app window.
                            _updateWindow?.Close();
                            _updateWindow = null;
                            _updating = false;
                            desktop.MainWindow = mainWindow;
                            if (!mainWindow.IsVisible) mainWindow.Show();
                        }
                    });
                }
                catch (Exception ex)
                {
                    CrashLogger.LogException("AutoUpdate background task", ex);
                }
            });
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CrashLogger.MarkCleanExit();
            // Only our own tool dir: a sweep here would pull the tools out from
            // under another Sylnar instance that is still running.
            try { _services?.GetService<NativeJtagService>()?.Dispose(); } catch { }
        };
    }

    // Only folders NativeJtagService creates: "nf_" + a 32-hex-digit GUID. A bare
    // "nf_*" glob also matched unrelated folders other programs keep in %TEMP%.
    private static readonly System.Text.RegularExpressions.Regex ToolDirName =
        new(@"^nf_[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Leftovers from a crashed run. Skip anything recent so a second instance
    // that is still running keeps its extracted tools.
    private static readonly TimeSpan StaleToolDirAge = TimeSpan.FromHours(12);

    private static void CleanupTempFolders()
    {
        try
        {
            var tempDir = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(tempDir, "nf_*"))
            {
                try
                {
                    if (!ToolDirName.IsMatch(Path.GetFileName(dir))) continue;
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) < StaleToolDirAge) continue;
                    Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static IServiceProvider ConfigureServices()
    {
        var settings = new AppSettings();

        var services = new ServiceCollection();

        services.AddSingleton(settings);

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<LogService>();
        services.AddSingleton<NativeJtagService>();
        services.AddSingleton<BoardDetectionService>();
        services.AddSingleton<FlashService>();
        services.AddSingleton<FtdiDriverService>();
        services.AddSingleton<DriverService>();
        services.AddSingleton<DmaTestService>();
        services.AddSingleton<BarProbeService>();
        services.AddSingleton<PciEnumService>();
        services.AddSingleton<AutoUpdateService>();

        services.AddSingleton<BoardInfoViewModel>();
        services.AddSingleton<FlashViewModel>();
        services.AddSingleton<DriverViewModel>();
        services.AddSingleton<DmaTestViewModel>();
        services.AddSingleton<BarProbeViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
