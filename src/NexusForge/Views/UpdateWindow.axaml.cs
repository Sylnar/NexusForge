using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sylnar.Views;

public partial class UpdateWindow : Window
{
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;

    public UpdateWindow()
    {
        InitializeComponent();
        _status = this.FindControl<TextBlock>("StatusText")!;
        _detail = this.FindControl<TextBlock>("DetailText")!;
        _bar = this.FindControl<ProgressBar>("Bar")!;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void SetStatus(string text) => _status.Text = text;

    public void SetProgress(long received, long total)
    {
        if (total > 0)
        {
            double pct = Math.Clamp(received * 100.0 / total, 0, 100);
            _bar.IsIndeterminate = false;
            _bar.Value = pct;
            _status.Text = "Downloading update...";
            _detail.Text = $"{received / 1048576.0:F1} / {total / 1048576.0:F1} MB  ({pct:F0}%)";
        }
        else
        {
            // No Content-Length: show indeterminate bar with raw MB downloaded.
            _bar.IsIndeterminate = true;
            _detail.Text = $"{received / 1048576.0:F1} MB";
        }
    }

    public void SetApplying()
    {
        _bar.IsIndeterminate = true;
        _bar.Value = 100;
        _status.Text = "Restarting to apply update...";
        _detail.Text = "";
    }
}
