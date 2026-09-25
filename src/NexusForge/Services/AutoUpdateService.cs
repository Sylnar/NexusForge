using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Sylnar.Helpers;
using Sylnar.Models;

namespace Sylnar.Services;

public class AutoUpdateService
{
    private readonly LogService _logService;
    private readonly AppSettings _settings;

    private const string Owner = "Sylnar";
    private const string Repo = "NexusForge";

    // Safety net: if a given target version fails to apply this many times in a row,
    // stop trying and just launch the current build. An update failure must NEVER
    // brick the app into a flash-and-vanish relaunch loop (the v1.1.22->v1.1.23 bug).
    private const int MaxUpdateAttempts = 3;

    public AutoUpdateService(LogService logService, AppSettings settings)
    {
        _logService = logService;
        _settings = settings;
    }

    public async Task<bool> CheckAndApplyUpdateAsync(IProgress<UpdateProgress>? progress = null)
    {
        try
        {
            _logService.Info("Checking for updates...");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Sylnar-Updater/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(15);

            var apiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            var response = await client.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logService.Info("No updates available.");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var remote = tag.TrimStart('v', 'V');

            if (!Version.TryParse(remote, out var rv) ||
                !Version.TryParse(_settings.Version, out var lv))
            {
                _logService.Info("Could not parse version info.");
                return false;
            }

            if (rv <= lv)
            {
                _logService.Info($"Sylnar is up to date (v{_settings.Version}).");
                // We're current: clear any stuck-update bookkeeping for a clean slate.
                ClearUpdateAttempts();
                return false;
            }

            // Loop-guard: if this exact target has already failed to apply repeatedly,
            // give up and let the app open normally instead of relaunch-looping forever.
            int attempts = GetUpdateAttempts(remote);
            if (attempts >= MaxUpdateAttempts)
            {
                _logService.Warn(
                    $"Update to v{remote} failed {attempts}x — launching current v{_settings.Version} instead. " +
                    "Please update manually.");
                CrashLogger.WriteLine(
                    $"AutoUpdate: giving up on v{remote} after {attempts} attempts; running current build.");
                return false;
            }

            _logService.Info($"Update available: v{_settings.Version} -> v{remote}");

            string? url = null;
            string? expectedSha256 = null;
            long expectedSize = 0;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var nm = a.GetProperty("name").GetString() ?? "";
                    if (nm.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.GetProperty("browser_download_url").GetString();
                        if (a.TryGetProperty("size", out var szProp))
                            expectedSize = szProp.GetInt64();
                        // GitHub publishes a "sha256:<hex>" digest for every release
                        // asset. Size + MZ checks only catch truncation; the digest
                        // catches corruption and a tampered asset.
                        if (a.TryGetProperty("digest", out var dgProp) &&
                            dgProp.ValueKind == JsonValueKind.String &&
                            dgProp.GetString() is { } dg &&
                            dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            expectedSha256 = dg.Substring("sha256:".Length).Trim();
                        break;
                    }
                }
            }

            if (url == null)
            {
                _logService.Warn("Update found but no exe asset in release.");
                return false;
            }

            progress?.Report(new UpdateProgress { Stage = UpdateStage.Found, Version = remote });
            _logService.Info($"Downloading update (~{expectedSize / 1024} KB)...");
            var temp = Path.Combine(Path.GetTempPath(), $"Sylnar_update_{Guid.NewGuid():N}.exe");

            using var dl = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!dl.IsSuccessStatusCode)
            {
                _logService.Warn("Failed to download update.");
                return false;
            }

            // Use the response Content-Length header (more reliable than the GitHub
            // API "size" because some redirects don't preserve it).
            long contentLength = dl.Content.Headers.ContentLength ?? expectedSize;

            // Stream the download with progress reporting so the UpdateWindow can show
            // live MB / % instead of the app silently vanishing during the swap.
            progress?.Report(new UpdateProgress
            {
                Stage = UpdateStage.Downloading, Version = remote, BytesReceived = 0, TotalBytes = contentLength
            });
            await using (var fs = File.Create(temp))
            await using (var src = await dl.Content.ReadAsStreamAsync())
            {
                var buffer = new byte[81920];
                long received = 0;
                long sinceReport = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    sinceReport += read;
                    if (sinceReport >= 262144)
                    {
                        sinceReport = 0;
                        progress?.Report(new UpdateProgress
                        {
                            Stage = UpdateStage.Downloading, Version = remote, BytesReceived = received, TotalBytes = contentLength
                        });
                    }
                }
                progress?.Report(new UpdateProgress
                {
                    Stage = UpdateStage.Downloading, Version = remote, BytesReceived = received, TotalBytes = contentLength
                });
            }

            // Verify the downloaded file is intact: header is MZ AND size matches
            // Content-Length (or expected size from API). A truncated download with
            // a valid MZ header was a previous bug — size check prevents replacing a
            // working exe with a broken one.
            var actualSize = new FileInfo(temp).Length;
            if (contentLength > 0 && actualSize != contentLength)
            {
                _logService.Warn($"Update aborted: downloaded {actualSize} bytes, expected {contentLength}.");
                CrashLogger.WriteLine($"AutoUpdate size mismatch: got {actualSize}, expected {contentLength}");
                try { File.Delete(temp); } catch { }
                return false;
            }
            if (actualSize < 1_000_000)
            {
                _logService.Warn($"Update aborted: downloaded file is suspiciously small ({actualSize} bytes).");
                try { File.Delete(temp); } catch { }
                return false;
            }

            var hdr = new byte[2];
            await using (var ck = File.OpenRead(temp))
            {
                if (await ck.ReadAsync(hdr) < 2 || hdr[0] != 0x4D || hdr[1] != 0x5A)
                {
                    _logService.Warn("Downloaded file is not a valid executable.");
                    try { File.Delete(temp); } catch { }
                    return false;
                }
            }

            if (expectedSha256 != null)
            {
                string actualSha256;
                await using (var hs = File.OpenRead(temp))
                    actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(hs));
                if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logService.Warn("Update aborted: downloaded file failed SHA-256 verification.");
                    CrashLogger.WriteLine($"AutoUpdate SHA-256 mismatch: got {actualSha256}, expected {expectedSha256}");
                    try { File.Delete(temp); } catch { }
                    return false;
                }
                CrashLogger.WriteLine($"AutoUpdate SHA-256 verified: {actualSha256}");
            }
            else
            {
                CrashLogger.WriteLine("AutoUpdate: release asset has no digest; SHA-256 check skipped.");
            }

            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null)
            {
                _logService.Warn("Could not determine current exe path.");
                try { File.Delete(temp); } catch { }
                return false;
            }

            // Strip Mark-of-the-Web (Zone.Identifier) from the downloaded file so
            // SmartScreen on Win11 25H2 doesn't block the new exe at relaunch.
            try { File.Delete(temp + ":Zone.Identifier"); } catch { }

            // Record this attempt BEFORE we hand off to the swap script. If the swap
            // fails and the old build relaunches, the count climbs until the loop-guard
            // above lets the app simply open.
            RecordUpdateAttempt(remote);

            var pid = Environment.ProcessId;
            var bat = Path.Combine(Path.GetTempPath(), $"nf_update_{pid}.bat");
            var batLog = GetBatLogPath();

            // The swap script. Key fix vs the old version (which only waited for its OWN
            // pid and did a single move): it kills EVERY Sylnar instance first, so a
            // lingering sibling can't keep the exe file locked, then retries the
            // copy-overwrite and verifies the new size landed before relaunching. On
            // failure it still relaunches the current exe so the user is never left with
            // nothing (the loop-guard caps repeats). It logs each step to batLog.
            var script = $"""
                @echo off
                setlocal EnableDelayedExpansion
                set "LOG={batLog}"
                echo [%date% %time%] update start, target swap into "{exe}", waiting on pid {pid} >> "%LOG%"

                set WAIT=0
                :wait
                tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul || goto kill
                set /a WAIT+=1
                if !WAIT! GEQ 30 goto kill
                timeout /t 1 /nobreak >nul
                goto wait

                :kill
                echo [%date% %time%] killing all Sylnar instances to release file lock >> "%LOG%"
                taskkill /F /IM Sylnar.exe >nul 2>&1
                timeout /t 2 /nobreak >nul

                set TRY=0
                :replace
                set /a TRY+=1
                copy /y "{temp}" "{exe}" >nul 2>&1
                set SZ=0
                set DZ=-1
                for %%A in ("{temp}") do set SZ=%%~zA
                for %%B in ("{exe}") do set DZ=%%~zB
                if "!SZ!"=="!DZ!" goto done
                echo [%date% %time%] replace try !TRY! incomplete src=!SZ! dst=!DZ! >> "%LOG%"
                if !TRY! GEQ 20 goto fail
                timeout /t 1 /nobreak >nul
                goto replace

                :done
                echo [%date% %time%] replace OK size=!DZ! >> "%LOG%"
                del /f /q "{temp}" >nul 2>&1
                start "" "{exe}"
                del /f /q "{bat}" >nul 2>&1
                exit /b 0

                :fail
                echo [%date% %time%] update FAILED after !TRY! tries; relaunching current build >> "%LOG%"
                del /f /q "{temp}" >nul 2>&1
                start "" "{exe}"
                del /f /q "{bat}" >nul 2>&1
                exit /b 2
                """;

            File.WriteAllText(bat, script);

            _logService.Info("Update downloaded. Restarting to apply...");
            progress?.Report(new UpdateProgress { Stage = UpdateStage.Applying, Version = remote });
            CrashLogger.WriteLine($"AutoUpdate: launching update bat for v{remote} ({actualSize} bytes), attempt {attempts + 1}");

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();

            return true;
        }
        catch (HttpRequestException)
        {
            _logService.Info("Offline - skipping update check.");
            return false;
        }
        catch (TaskCanceledException)
        {
            _logService.Info("Update check timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _logService.Info($"Update check skipped: {ex.Message}");
            CrashLogger.WriteLine($"AutoUpdate exception: {ex}");
            return false;
        }
    }

    // ---- stuck-update bookkeeping (survives the relaunch loop) -----------------

    private static string StateDir =>
        string.IsNullOrEmpty(CrashLogger.LogDirectory) ? Path.GetTempPath() : CrashLogger.LogDirectory;

    private static string AttemptsPath => Path.Combine(StateDir, "update_attempts.txt");

    private static string GetBatLogPath() => Path.Combine(StateDir, "update_bat.log");

    // File format: "<targetVersion> <count>". Count is only meaningful for the
    // recorded target; a different remote target resets it to zero.
    private int GetUpdateAttempts(string target)
    {
        try
        {
            if (!File.Exists(AttemptsPath)) return 0;
            var parts = File.ReadAllText(AttemptsPath).Trim().Split(' ');
            if (parts.Length == 2 && parts[0] == target && int.TryParse(parts[1], out var n))
                return n;
        }
        catch { }
        return 0;
    }

    private void RecordUpdateAttempt(string target)
    {
        try
        {
            int next = GetUpdateAttempts(target) + 1;
            File.WriteAllText(AttemptsPath, $"{target} {next}");
        }
        catch { }
    }

    private void ClearUpdateAttempts()
    {
        try { if (File.Exists(AttemptsPath)) File.Delete(AttemptsPath); }
        catch { }
    }
}
