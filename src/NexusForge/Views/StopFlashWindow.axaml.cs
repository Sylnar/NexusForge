using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Sylnar.Views;

/// <summary>
/// Asks whether to stop a running SPI flash when the main window is closed.
/// Shown with ShowDialog&lt;bool&gt;: true means stop the flash and close, false
/// (the default, and what Escape or any other way of dismissing it returns) means
/// keep flashing and keep Sylnar open.
/// </summary>
public partial class StopFlashWindow : Window
{
    public StopFlashWindow()
    {
        InitializeComponent();

        var stop = this.FindControl<Button>("StopButton");
        var keep = this.FindControl<Button>("KeepButton");
        if (stop != null) stop.Click += (_, _) => Close(true);
        if (keep != null) keep.Click += (_, _) => Close(false);

        Opened += (_, _) => keep?.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
            return;
        }
        base.OnKeyDown(e);
    }
}
