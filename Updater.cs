using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace Trophyline;

/// Automatic updates from the GitHub releases.
/// Windows and Linux: download the new program next to the current one, check its SHA-256 against the
/// release's SHA256SUMS.txt, then swap the files (a running program can be renamed, not overwritten).
/// macOS: only says that a new version exists (an app bundle can't be swapped safely from inside).
static class Updater
{
    const string Repo = "CillX111/Trophyline";

    public sealed record Result(bool Newer, Version Latest, bool Installed, string? Note);

    public static Version Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            return Version.TryParse(info.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
        }
    }

    static string? AssetName => Platform.Windows ? "Trophyline.exe" : Platform.Linux ? "Trophyline-linux-x64" : null;

    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    /// Off for development builds and when switched off in Settings.
    public static bool Allowed =>
        Program.Settings().AutoUpdate
        && Environment.GetEnvironmentVariable("TROPHYLINE_NO_UPDATE") != "1"
        && Environment.ProcessPath is string exe
        && !exe.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);

    static HttpClient NewHttp(bool redirects = true)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Trophyline-updater");
        return h;
    }

    /// The newest released version (from where github.com/…/releases/latest points; no API limits).
    public static async Task<Version?> Latest()
    {
        using var http = NewHttp(redirects: false);
        using var res = await http.GetAsync(ReleasesPage);
        var tag = res.Headers.Location?.ToString().Split('/').LastOrDefault() ?? "";
        return Version.TryParse(tag.TrimStart('v'), out var v) ? v : null;
    }

    /// Leftover from the last update: the previous program, renamed. Removed at the next start.
    public static void CleanUp()
    {
        if (Environment.ProcessPath is not string exe) return;
        try { if (File.Exists(exe + ".old")) File.Delete(exe + ".old"); } catch { }
        try { if (File.Exists(exe + ".new")) File.Delete(exe + ".new"); } catch { }
    }

    /// Checks for a newer version and (if this system allows it) installs it. Never throws.
    public static async Task<Result?> CheckAndInstall(bool install = true)
    {
        Version? latest;
        try { latest = await Latest(); }
        catch (Exception ex) { Program.LogError("update check: " + ex.Message); return null; }
        if (latest == null) return null;
        if (latest <= Current) return new Result(false, latest, false, null);
        if (!install || AssetName is not string asset || Environment.ProcessPath is not string exe)
            return new Result(true, latest, false, Platform.Mac ? "Download the new version from GitHub." : null);

        // one update at a time (window and tracker may both try): an exclusively opened lock file
        FileStream gate;
        try { gate = new FileStream(Path.Combine(Program.Home, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { return new Result(true, latest, false, "Another Trophyline is updating right now."); }
        using var _ = gate;
        try
        {
            using var http = NewHttp();
            var baseUrl = $"https://github.com/{Repo}/releases/download/v{latest}";
            var sums = await http.GetStringAsync($"{baseUrl}/SHA256SUMS.txt");
            var expected = sums.Split('\n').Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
                               .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*').Trim() == asset)?[0].ToLowerInvariant();
            if (expected is not { Length: 64 }) return new Result(true, latest, false, "The release has no fingerprint for this system, so it wasn't installed.");

            var tmp = exe + ".new";
            using (var res = await http.GetAsync($"{baseUrl}/{asset}", HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                await using var file = File.Create(tmp);
                await res.Content.CopyToAsync(file);
            }
            string actual;
            await using (var check = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(check)).ToLowerInvariant();
            if (actual != expected)
            {
                File.Delete(tmp);
                return new Result(true, latest, false, "The download didn't match the release's fingerprint, so it wasn't installed.");
            }
            if (!Platform.Windows)
                File.SetUnixFileMode(tmp, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            // swap: the running program can be renamed but not overwritten
            if (File.Exists(exe + ".old")) File.Delete(exe + ".old");
            File.Move(exe, exe + ".old");
            File.Move(tmp, exe);
            Program.LogWindow($"updated {Current} -> {latest}");
            return new Result(true, latest, true, null);
        }
        catch (Exception ex)
        {
            Program.LogError("update: " + ex.Message);
            try { if (File.Exists(exe + ".new")) File.Delete(exe + ".new"); } catch { }
            if (!File.Exists(exe) && File.Exists(exe + ".old")) File.Move(exe + ".old", exe); // put the old one back
            return new Result(true, latest, false, ex is UnauthorizedAccessException
                ? "Trophyline's folder isn't writable, so it can't update itself."
                : "The update couldn't be downloaded. It'll try again later.");
        }
    }

    /// For the background tracker: check now and then; after an update, hand over to the new program.
    public static Timer StartBackgroundChecks()
    {
        return new Timer(_ =>
        {
            if (!Allowed || !Program.Settings().WelcomeAccepted) return; // not before the notice about updates was shown
            var r = CheckAndInstall().GetAwaiter().GetResult();
            Program.TrimMemory();
            if (r is { Installed: true } && Environment.ProcessPath is string exe)
            {
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--background", "--after-update" }, UseShellExecute = false });
                Environment.Exit(0); // the new tracker takes over
            }
        }, null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(12));
    }
}
