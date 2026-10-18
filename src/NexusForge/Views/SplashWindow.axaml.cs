using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Sylnar.Helpers;

namespace Sylnar.Views;

public partial class SplashWindow : Window
{
    private readonly ProgressBar _progress;
    private readonly TextBlock _statusText;

    private static readonly string[] LoadingMessages =
    [
        "Initializing core systems...",
        "Loading JTAG interface...",
        "Preparing flash engine...",
        "Scanning driver modules...",
        "Configuring DMA bridge...",
        "Ready."
    ];

    public SplashWindow()
    {
        InitializeComponent();
        _progress = this.FindControl<ProgressBar>("Progress")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;

        // The version label binds to VersionInfo in the XAML, so nothing is set here.
        Opacity = 0;
        Opened += OnOpened;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // async void on a window event is unavoidable here (Avalonia API shape). An
        // unhandled exception would go to the dispatcher and kill the process silently,
        // and the splash owns the handoff to the main window: App.axaml.cs reveals
        // MainWindow from this window's Closed event, so if this method ever escapes
        // without closing, the app is left with no window at all. Hence the try and
        // the Close() in the finally.
        try
        {
            await AnimateOpacity(0, 1, 300);

            for (int i = 0; i < LoadingMessages.Length; i++)
            {
                double target = (i + 1) * 100.0 / LoadingMessages.Length;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _statusText.Text = LoadingMessages[i];
                });

                await AnimateProgress(target, 350);
                await Task.Delay(i == LoadingMessages.Length - 1 ? 400 : 250);
            }

            // Brief pause on "Ready."
            await Task.Delay(300);

            await AnimateOpacity(1, 0, 250);
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("SplashWindow.OnOpened", ex);
        }
        finally
        {
            try { Close(); } catch { }
        }
    }

    /// <summary>
    /// Eases the bar to <paramref name="targetPercent"/> (0-100). The bar is a themed
    /// ProgressBar, so this sets Value rather than the width of a hand-built track -
    /// which is also why there is no longer a hardcoded track width to keep in step
    /// with the window size.
    /// </summary>
    private async Task AnimateProgress(double targetPercent, int durationMs)
    {
        double start = 0;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            start = _progress.Value;
        });

        const int steps = 30;
        int stepMs = Math.Max(1, durationMs / steps);

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            // Ease-out cubic
            double eased = 1 - Math.Pow(1 - t, 3);
            double v = start + (targetPercent - start) * eased;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _progress.Value = v;
            });

            await Task.Delay(stepMs);
        }
    }

    private async Task AnimateOpacity(double from, double to, int durationMs)
    {
        const int steps = 20;
        int stepMs = Math.Max(1, durationMs / steps);

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double eased = 1 - Math.Pow(1 - t, 2);
            double val = from + (to - from) * eased;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Opacity = val;
            });

            await Task.Delay(stepMs);
        }
    }
}
