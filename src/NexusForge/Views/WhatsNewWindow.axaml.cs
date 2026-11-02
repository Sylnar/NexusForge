using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Sylnar.Helpers;

namespace Sylnar.Views;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow()
    {
        InitializeComponent();
    }

    public WhatsNewWindow(string notes) : this()
    {
        this.FindControl<TextBlock>("HeadingText")!.Text = $"What's new in {VersionInfo.WithV}";
        this.FindControl<TextBlock>("NotesText")!.Text = notes;

        var close = this.FindControl<Button>("CloseButton");
        if (close != null) close.Click += (_, _) => Close();
        Opened += (_, _) => close?.Focus();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
