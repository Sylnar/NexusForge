namespace Sylnar.ViewModels;

/// <summary>
/// Status colour family. View models pick a tone; the view turns it into
/// palette colours through <c>ctrl:ToneClass.Tone</c> and Styles/Theme.axaml.
/// </summary>
public enum Tone
{
    Neutral,
    Ok,
    Warn,
    Danger,
    Info
}
