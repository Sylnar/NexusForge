using Avalonia;
using Sylnar.Helpers;

namespace Sylnar;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless CLI path for SSH / automation verification:
        //   Sylnar.exe --dmatest full | latency | throughput | stress
        // Must be handled BEFORE any Avalonia bootstrap so the GUI never comes up.
        // HeadlessDmaTest.Run attaches a console, runs the test, prints + writes the
        // result, and Environment.Exit()s — it never returns here.
        if (args.Length >= 1 && args[0] == "--dmatest")
        {
            HeadlessDmaTest.Run(args);
            return;
        }
        if (args.Length >= 1 && args[0] == "--deploy")
        {
            HeadlessDmaTest.RunDeploy(args);
            return;
        }

        // Crash logger MUST be the very first thing — it has to be live to catch
        // anything that fails during AppBuilder.Configure or XAML load.
        CrashLogger.Install();

        // Win11 25H2 fix: when the previous run did not exit cleanly (marker file
        // is still on disk from a prior crash, or the user killed the process),
        // wipe the .NET single-file native-extraction cache. Defender/SmartScreen
        // on 25H2 can quarantine files in %TEMP%\.net\Sylnar\<hash>\ between
        // launches, leaving the cache present-but-broken so the next launch dies
        // silently in the apphost before any managed code runs. PC reboot clears
        // this because Windows resets the AV scan state for fresh sessions.
        try
        {
            if (CrashLogger.PreviousRunCrashed)
                ResetSingleFileExtractCache();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("ResetSingleFileExtractCache", ex);
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            CrashLogger.MarkCleanExit();
        }
        catch (Exception ex)
        {
            CrashLogger.LogException("Program.Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void ResetSingleFileExtractCache()
    {
        // Standard apphost extract location on Windows when DOTNET_BUNDLE_EXTRACT_BASE_DIR
        // is not overridden. We can't redirect this from managed code (the apphost has
        // already extracted by the time Main runs) but we CAN delete it so the next
        // launch gets a fresh extract.
        var temp = Path.GetTempPath();
        var dotnetCache = Path.Combine(temp, ".net", "Sylnar");
        if (!Directory.Exists(dotnetCache)) return;

        CrashLogger.WriteLine($"Clearing stale single-file extract cache at {dotnetCache}");

        foreach (var sub in Directory.GetDirectories(dotnetCache))
        {
            try
            {
                Directory.Delete(sub, recursive: true);
                CrashLogger.WriteLine($"  Deleted {sub}");
            }
            catch (Exception ex)
            {
                // Files may be locked by the currently-running apphost (we can't
                // delete the cache backing OUR own process) — log and move on.
                CrashLogger.WriteLine($"  Could not delete {sub}: {ex.Message}");
            }
        }
    }
}
