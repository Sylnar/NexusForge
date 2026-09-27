using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sylnar.Views.Controls;

/// <summary>One USB link's driver card on the Drivers page.</summary>
public partial class DriverCard : UserControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<DriverCard, Geometry?>(nameof(Icon));

    public DriverCard()
    {
        InitializeComponent();
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
