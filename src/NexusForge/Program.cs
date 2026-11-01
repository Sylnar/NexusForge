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

        // The single-file host extracts native libraries before Main runs.
        // Do not remove that cache here: Skia and other native libraries may
        // not have been loaded yet, and deleting them breaks this very launch.

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

    // Fonts are embedded (Assets/Fonts) and wired up in Styles/Theme.axaml.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

}
