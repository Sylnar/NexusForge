using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Sylnar.Views;

/// <summary>
/// The exit splash, shown from MainWindow.OnClosing while services are disposed and
/// the temp-folder cleanup process is spawned. It carries no state and no code: it is
/// a sibling of <see cref="SplashWindow"/> and gets everything from Styles/Theme.axaml.
/// </summary>
public partial class ShutdownWindow : Window
{
    public ShutdownWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
