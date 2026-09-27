using Avalonia;
using Sylnar.ViewModels;

namespace Sylnar.Views.Controls;

/// <summary>
/// <c>ctrl:ToneClass.Tone="{Binding SomeTone}"</c> mirrors a <see cref="Tone"/>
/// into the element's classes (ok / warn / danger / info; none for neutral),
/// so Styles/Theme.axaml can colour pills, dots, notes and cards.
/// </summary>
public static class ToneClass
{
    public static readonly AttachedProperty<Tone> ToneProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, Tone>("Tone", typeof(ToneClass));

    static ToneClass()
    {
        ToneProperty.Changed.AddClassHandler<StyledElement>(
            (element, e) => Apply(element, e.GetNewValue<Tone>()));
    }

    public static Tone GetTone(StyledElement element) => element.GetValue(ToneProperty);

    public static void SetTone(StyledElement element, Tone value) => element.SetValue(ToneProperty, value);

    private static void Apply(StyledElement element, Tone tone)
    {
        element.Classes.Set("ok", tone == Tone.Ok);
        element.Classes.Set("warn", tone == Tone.Warn);
        element.Classes.Set("danger", tone == Tone.Danger);
        element.Classes.Set("info", tone == Tone.Info);
    }
}
