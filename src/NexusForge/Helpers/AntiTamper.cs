using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sylnar.Helpers;
// CrashLogger is in this namespace too — no extra using needed.


internal static partial class AntiTamper
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsDebuggerPresent();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CheckRemoteDebuggerPresent(
        nint hProcess, [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint processHandle, int processInformationClass,
        out nint processInformation, int processInformationLength, out int returnLength);

    private static readonly byte[] _h = { 0x4E, 0x46, 0x2D, 0x76, 0x31 };
    private static readonly int[] _z = { 7, 31, 0x1F, 0x0007, 7 };

    public static void Verify()
    {
        var rеsult = false;
        try { rеsult |= P1(); } catch (Exception ex) { CrashLogger.WriteLine($"AntiTamper.P1 threw: {ex.Message}"); }
        try { rеsult |= P2(); } catch (Exception ex) { CrashLogger.WriteLine($"AntiTamper.P2 threw: {ex.Message}"); }
        try { rеsult |= P3(); } catch (Exception ex) { CrashLogger.WriteLine($"AntiTamper.P3 threw: {ex.Message}"); }
        if (rеsult)
        {
            CrashLogger.WriteLine("AntiTamper.Verify tripped (P1|P2|P3) — exiting at startup.");
            Bail();
        }
        StartWatchdog();
    }

    private static bool P1()
    {
        if (Debugger.IsAttached) return true;
        try { if (IsDebuggerPresent()) return true; } catch { }
        try
        {
            if (CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out bool r) && r)
                return true;
        }
        catch { }
        try
        {
            int s = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle, _z[0],
                out nint dp, nint.Size, out _);
            if (s == 0 && dp != 0) return true;
        }
        catch { }
        return false;
    }

    private static bool P2()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            long а = 0;
            for (int i = 0; i < 1000; i++) а += i * i;
            sw.Stop();
            if (sw.ElapsedMilliseconds > 500) return true;
            GC.KeepAlive(а);
        }
        catch { }
        return false;
    }

    private static bool P3()
    {
        try
        {
            string[] m =
            {
                "gqVs|", "LOVs|", "grwGvp", "fkgde",
                "z64gej", "z65gej", "roolgej",
            };
            var ps = Process.GetProcesses();
            foreach (var p in ps)
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    foreach (var е in m)
                    {
                        string d = new(е.Select(c => (char)(c - 3)).ToArray());
                        if (n.Contains(d, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static void StartWatchdog()
    {
        // Watchdog used to call Bail() on any P1/P3 trigger, which silently killed
        // the app via Environment.Exit(0) from a BACKGROUND THREAD with no surface.
        // On Win11 25H2 the process scan (P3) can flake on protected/ELAM processes
        // and we don't want to surprise-exit the whole app for that. Detect, log,
        // do not kill. Ship as logging-only watchdog.
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(10_000);
                try
                {
                    if (P1())
                        CrashLogger.WriteLine("AntiTamper.Watchdog: P1 (debugger) tripped — ignoring.");
                    if (P3())
                        CrashLogger.WriteLine("AntiTamper.Watchdog: P3 (process scan) tripped — ignoring.");
                }
                catch { }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        t.Start();
    }

    private static void Bail()
    {
        try { Environment.Exit(0); } catch { }
        try { Process.GetCurrentProcess().Kill(); } catch { }
    }
}
