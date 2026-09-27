using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sylnar.Views.Controls;

/// <summary>
/// A 24×24 line icon (see the Icon* geometries in Styles/Theme.axaml), stroked
/// with the inherited Foreground so it follows hover and disabled colours of
/// the button or list item it sits in.
/// </summary>
public sealed class StrokeIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<StrokeIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<StrokeIcon>();

    /// <summary>Stroke width in the icon's 24-unit space (scales with the icon).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeThickness), 1.8);

    /// <summary>Fill the shape instead of stroking it (the play triangle).</summary>
    public static readonly StyledProperty<bool> IsFilledProperty =
        AvaloniaProperty.Register<StrokeIcon, bool>(nameof(IsFilled));

    static StrokeIcon()
    {
        AffectsRender<StrokeIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, IsFilledProperty);
        WidthProperty.OverrideDefaultValue<StrokeIcon>(16);
        HeightProperty.OverrideDefaultValue<StrokeIcon>(16);
        VerticalAlignmentProperty.OverrideDefaultValue<StrokeIcon>(Avalonia.Layout.VerticalAlignment.Center);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool IsFilled
    {
        get => GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
            return;

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var scale = size / 24.0;
        var offset = Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * offset))
        {
            if (IsFilled)
                context.DrawGeometry(brush, null, data);
            else
                context.DrawGeometry(null, new Pen(brush, StrokeThickness,
                    lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
