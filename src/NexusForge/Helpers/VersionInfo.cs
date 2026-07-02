using System.Reflection;

namespace NexusForge.Helpers;

/// <summary>
/// Single source of truth for user-visible version strings, resolved once at
/// type-load from the assembly's InformationalVersion attribute (populated by
/// the csproj &lt;Version&gt; element at build time).
///
/// v1.1.25: introduced so every user-visible label / window title / update
/// check binds to the same value. Before this, four separate XAML files
/// hardcoded "v1.1.24"/"v1.1.25" strings that a version bump had to touch by
/// hand. Now csproj &lt;Version&gt; is the only place that changes per release.
///
/// XAML usage:
///   xmlns:h="using:NexusForge.Helpers"
///   Text="{x:Static h:VersionInfo.WithV}"
///   Title="{x:Static h:VersionInfo.WindowTitle}"
/// </summary>
public static class VersionInfo
{
    /// <summary>Bare version string, e.g. "1.1.25".</summary>
    public static readonly string Value = Resolve();

    /// <summary>Version prefixed with "v", e.g. "v1.1.25", for badges/footers.</summary>
    public static readonly string WithV = "v" + Value;

    /// <summary>Full window title, e.g. "NexusForge v1.1.25".</summary>
    public static readonly string WindowTitle = "NexusForge v" + Value;

    private static string Resolve()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Prefer InformationalVersion (csproj Version -> "1.1.25"), fall
            // back to AssemblyVersion. InformationalVersion may include a
            // "+commit" SemVer build-metadata suffix - strip it for display.
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                var v = info.InformationalVersion;
                var plus = v.IndexOf('+');
                if (plus >= 0) v = v.Substring(0, plus);
                return v;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
