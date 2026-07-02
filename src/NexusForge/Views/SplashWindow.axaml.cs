using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using NexusForge.Helpers;

namespace NexusForge.Views;

public partial class SplashWindow : Window
{
    private readonly Border _progressFill;
    private readonly TextBlock _statusText;
    private readonly TextBlock _versionText;
    private readonly Border _logoBorder;
    private readonly double _trackWidth;

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
        _progressFill = this.FindControl<Border>("ProgressFill")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _versionText = this.FindControl<TextBlock>("VersionText")!;
        _logoBorder = this.FindControl<Border>("LogoBorder")!;
        _trackWidth = 460 - 96; // window width minus margins (48*2)

        // v1.1.25: sourced from shared VersionInfo helper so all user-visible
        // version strings agree without cross-file editing.
        try { _versionText.Text = VersionInfo.WithV; }
        catch { }

        Opacity = 0;
        Opened += OnOpened;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Fade in
        await AnimateOpacity(0, 1, 300);

        // Animate progress through loading stages
        for (int i = 0; i < LoadingMessages.Length; i++)
        {
            double progress = (double)(i + 1) / LoadingMessages.Length;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _statusText.Text = LoadingMessages[i];
            });

            await AnimateProgress(progress, 350);
            await Task.Delay(i == LoadingMessages.Length - 1 ? 400 : 250);
        }

        // Brief pause on "Ready."
        await Task.Delay(300);

        // Fade out then close (triggers App.cs Closed handler → shows main window)
        await AnimateOpacity(1, 0, 250);
        Close();
    }

    private async Task AnimateProgress(double targetFraction, int durationMs)
    {
        double targetWidth = _trackWidth * targetFraction;
        double startWidth = 0;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            startWidth = _progressFill.Width;
            if (double.IsNaN(startWidth)) startWidth = 0;
        });

        const int steps = 30;
        int stepMs = durationMs / steps;

        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            // Ease-out cubic
            double eased = 1 - Math.Pow(1 - t, 3);
            double w = startWidth + (targetWidth - startWidth) * eased;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _progressFill.Width = w;
            });

            await Task.Delay(stepMs);
        }
    }

    private async Task AnimateOpacity(double from, double to, int durationMs)
    {
        const int steps = 20;
        int stepMs = durationMs / steps;

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
