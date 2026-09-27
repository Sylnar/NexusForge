using System.Security.Principal;

namespace Sylnar.Helpers;

public static class Elevation
{
    /// <summary>
    /// True when the process runs elevated. The manifest asks for admin, but a
    /// user can still start the exe without it (e.g. from some launchers).
    /// </summary>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
