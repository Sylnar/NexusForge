using System.Diagnostics;

namespace Sylnar.Helpers;

/// <summary>
/// Runs pnputil.exe. Both output streams are drained (an unread redirected pipe
/// can stall the child once its buffer fills) and the process is disposed.
/// </summary>
public static class PnpUtil
{
    public const int Success = 0;
    public const int NothingToInstall = 259;   // ERROR_NO_MORE_ITEMS: already current / no matching device
    public const int RebootRequired = 3010;    // ERROR_SUCCESS_REBOOT_REQUIRED

    public static async Task<(int ExitCode, string Output)> RunAsync(string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = arguments,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            }
        };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, (await stdout) + (await stderr));
    }

    public static bool IsSuccess(int exitCode) =>
        exitCode is Success or NothingToInstall or RebootRequired;
}
