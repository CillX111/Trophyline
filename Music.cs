using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Trophyline;

public sealed record NowPlaying(bool Running, bool Playing, string Title, string Artist);

/// Controls the Spotify desktop app that's already on the computer (no Spotify login needed).
/// Windows: media keys + Spotify's window title. macOS: AppleScript. Linux: MPRIS over D-Bus.
static partial class Spotify
{
    // ---------- what's playing ----------

    public static NowPlaying Status()
    {
        try
        {
            if (Platform.Windows) return WindowsStatus();
            if (Platform.Mac) return MacStatus();
            if (Platform.Linux) return LinuxStatus();
        }
        catch (Exception) { }
        return new NowPlaying(false, false, "", "");
    }

    static NowPlaying WindowsStatus()
    {
        var pids = SpotifyPids();
        if (pids.Count == 0) return new NowPlaying(false, false, "", "");
        // While a song plays the window title is "Artist - Song"; when paused it's just "Spotify ...".
        // (Found directly, so it also works while the window is hidden.)
        var title = Win.FindMainWindow(pids).Title;
        if (title.Length == 0 || title.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase))
            return new NowPlaying(true, false, "", "");
        var cut = title.IndexOf(" - ", StringComparison.Ordinal);
        return cut > 0
            ? new NowPlaying(true, true, title[(cut + 3)..], title[..cut])
            : new NowPlaying(true, true, title, "");
    }

    static NowPlaying MacStatus()
    {
        if (Run("pgrep", "-x Spotify").Trim().Length == 0) return new NowPlaying(false, false, "", "");
        var r = Run("osascript", "-e", "tell application \"Spotify\" to (player state as string) & \"\n\" & (name of current track) & \"\n\" & (artist of current track)");
        var lines = r.Split('\n');
        return new NowPlaying(true, lines[0].Trim() == "playing",
            lines.Length > 1 ? lines[1].Trim() : "", lines.Length > 2 ? lines[2].Trim() : "");
    }

    static NowPlaying LinuxStatus()
    {
        var state = Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
            "org.freedesktop.DBus.Properties.Get", "string:org.mpris.MediaPlayer2.Player", "string:PlaybackStatus");
        if (!state.Contains("variant")) return new NowPlaying(false, false, "", "");
        var meta = Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
            "org.freedesktop.DBus.Properties.Get", "string:org.mpris.MediaPlayer2.Player", "string:Metadata");
        string Field(string key)
        {
            var m = Regex.Match(meta, "\"" + Regex.Escape(key) + "\"\\s*variant\\s*(?:array \\[\\s*)?string \"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        return new NowPlaying(true, state.Contains("\"Playing\""), Field("xesam:title"), Field("xesam:artist"));
    }

    // ---------- buttons ----------

    public enum Command { PlayPause, Next, Previous, Play }

    public static void Send(Command c)
    {
        try
        {
            if (Platform.Windows)
            {
                if (c == Command.Play && Status().Playing) return;
                MediaKey(c switch { Command.Next => 0xB0, Command.Previous => 0xB1, _ => 0xB3 });
            }
            else if (Platform.Mac)
                Run("osascript", "-e", "tell application \"Spotify\" to " + c switch
                {
                    Command.Next => "next track",
                    Command.Previous => "previous track",
                    Command.Play => "play",
                    _ => "playpause",
                });
            else if (Platform.Linux)
                Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
                    "org.mpris.MediaPlayer2.Player." + c switch
                    {
                        Command.Next => "Next",
                        Command.Previous => "Previous",
                        Command.Play => "Play",
                        _ => "PlayPause",
                    });
        }
        catch (Exception ex) { Program.LogError("spotify: " + ex.Message); }
    }

    /// Starts Spotify if needed and plays the playlist / album / artist.
    public static async Task PlayAsync(SavedPlaylist p)
    {
        try
        {
            var uri = $"spotify:{p.Kind}:{p.Id}";
            if (Platform.Mac)
            {
                if (!Status().Running)
                {
                    Run("open", "-g", "-j", "-a", "Spotify"); // -g: stay in background, -j: start hidden
                    await Task.Delay(3000);
                }
                Run("osascript", "-e", $"tell application \"Spotify\" to play track \"{uri}\"");
            }
            else if (Platform.Linux)
            {
                if (!Status().Running)
                {
                    Process.Start(new ProcessStartInfo("spotify") { UseShellExecute = false });
                    await Task.Delay(4000);
                }
                Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
                    "org.mpris.MediaPlayer2.Player.OpenUri", "string:" + uri);
            }
            else
            {
                // Windows: start Spotify in the background if needed, open the link (Spotify then jumps to
                // the front), tuck its window away again, and press play.
                bool hide = Program.Settings().HideSpotify;
                bool wasRunning = Status().Running;
                if (!wasRunning)
                {
                    StartMinimizedWindows();
                    await WaitUntilRunning(TimeSpan.FromSeconds(10));
                    await TuckAway(hide, TimeSpan.FromSeconds(2.5)); // let it finish loading
                }
                bool tuck = !wasRunning || WindowState() != SpotifyWindow.Showing;
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                if (tuck) await TuckAway(hide, TimeSpan.FromSeconds(4));
                else await Task.Delay(800);
                Send(Command.Play);
                if (tuck) await TuckAway(hide, TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex) { Program.LogError("spotify play: " + ex.Message); }
    }

    /// Starts Spotify as a background process: no window (or minimized, if hiding is turned off).
    public static async Task OpenAppAsync()
    {
        try
        {
            if (Platform.Mac) Run("open", "-g", "-j", "-a", "Spotify");
            else if (Platform.Linux) Process.Start(new ProcessStartInfo("spotify") { UseShellExecute = false });
            else
            {
                StartMinimizedWindows();
                await WaitUntilRunning(TimeSpan.FromSeconds(10));
                await TuckAway(Program.Settings().HideSpotify, TimeSpan.FromSeconds(4));
            }
        }
        catch (Exception ex) { Program.LogError("spotify open: " + ex.Message); }
    }

    // ---------- Spotify's window (Windows) ----------

    public enum SpotifyWindow { None, Hidden, Minimized, Showing }

    static HashSet<int> SpotifyPids()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("Spotify")) { ids.Add(p.Id); p.Dispose(); }
        return ids;
    }

    public static SpotifyWindow WindowState()
    {
        if (!Platform.Windows) return SpotifyWindow.None;
        var h = Win.FindMainWindow(SpotifyPids()).Handle;
        if (h == IntPtr.Zero) return SpotifyWindow.None;
        if (!Win.IsWindowVisible(h)) return SpotifyWindow.Hidden;
        return Win.IsIconic(h) ? SpotifyWindow.Minimized : SpotifyWindow.Showing;
    }

    /// Hides Spotify's window completely (it keeps playing as a background process).
    public static void HideWindow()
    {
        var h = Win.FindMainWindow(SpotifyPids()).Handle;
        if (h != IntPtr.Zero) Win.ShowWindow(h, Win.SW_HIDE);
    }

    /// Brings Spotify's window back.
    public static void ShowWindow()
    {
        var h = Win.FindMainWindow(SpotifyPids()).Handle;
        if (h == IntPtr.Zero) return;
        Win.ShowWindow(h, Win.SW_SHOW);
        Win.ShowWindow(h, Win.SW_RESTORE);
        Win.SetForegroundWindow(h);
    }

    /// Watches for Spotify's window popping up and hides it (or minimizes it), without taking focus.
    static async Task TuckAway(bool hide, TimeSpan watch)
    {
        var until = DateTime.Now + watch;
        while (DateTime.Now < until)
        {
            var h = Win.FindMainWindow(SpotifyPids()).Handle;
            if (h != IntPtr.Zero && Win.IsWindowVisible(h))
            {
                if (hide) Win.ShowWindow(h, Win.SW_HIDE);
                else if (!Win.IsIconic(h)) Win.ShowWindow(h, Win.SW_SHOWMINNOACTIVE);
            }
            await Task.Delay(150);
        }
    }

    /// Plain Win32 calls; EnumWindows also finds hidden windows (Process.MainWindowHandle doesn't).
    static class Win
    {
        public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;

        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        public static (IntPtr Handle, string Title) FindMainWindow(HashSet<int> pids)
        {
            if (pids.Count == 0) return (IntPtr.Zero, "");
            (IntPtr, string) found = (IntPtr.Zero, "");
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (!pids.Contains((int)pid)) return true;
                var cls = new System.Text.StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                var title = new System.Text.StringBuilder(len + 1);
                GetWindowText(h, title, title.Capacity);
                found = (h, title.ToString());
                return false; // first titled Spotify window is the main one
            }, IntPtr.Zero);
            return found;
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder s, int max);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    // ---------- starting Spotify (Windows) ----------

    /// Spotify's own "--minimized" start option, through the Store app's launcher or the classic install.
    static void StartMinimizedWindows()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var exe = new[]
        {
            Path.Combine(local, "Microsoft", "WindowsApps", "Spotify.exe"), // Microsoft Store version
            Path.Combine(roaming, "Spotify", "Spotify.exe"),                 // spotify.com installer
        }.FirstOrDefault(File.Exists);
        if (exe != null)
            Process.Start(new ProcessStartInfo(exe, "--minimized") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
        else
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
    }

    static async Task WaitUntilRunning(TimeSpan max)
    {
        var until = DateTime.Now + max;
        while (DateTime.Now < until && !Status().Running) await Task.Delay(300);
    }

    // ---------- helpers ----------

    static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return "";
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);
        return output;
    }

    static void MediaKey(byte vk)
    {
        const uint KEYUP = 0x0002, EXTENDED = 0x0001;
        keybd_event(vk, 0, EXTENDED, 0);
        keybd_event(vk, 0, EXTENDED | KEYUP, 0);
    }

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}

public sealed class SavedPlaylist
{
    public string Kind { get; set; } = "playlist"; // playlist, album or artist
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Cover { get; set; } = "";       // picture URL from Spotify
    public DateTime Added { get; set; } = DateTime.Now;
}

/// Your saved playlists (music/playlists.json in the data folder).
static class PlaylistStore
{
    static string Folder => Path.Combine(Program.Home, "music");
    static string FilePath => Path.Combine(Folder, "playlists.json");
    public static string CoverPath(SavedPlaylist p) => Path.Combine(Folder, $"{p.Kind}_{p.Id}.jpg");

    static readonly Regex Link = new(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?|spotify:)(playlist|album|artist)[/:]([A-Za-z0-9]{10,40})",
                                     RegexOptions.IgnoreCase);

    public static List<SavedPlaylist> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return doc.RootElement.EnumerateArray().Select(e => new SavedPlaylist
            {
                Kind = e.GetProperty("kind").GetString() ?? "playlist",
                Id = e.GetProperty("id").GetString() ?? "",
                Title = e.GetProperty("title").GetString() ?? "",
                Cover = e.TryGetProperty("cover", out var c) ? c.GetString() ?? "" : "",
                Added = e.TryGetProperty("added", out var a) && a.TryGetDateTime(out var d) ? d : DateTime.Now,
            }).Where(p => p.Id.Length > 0).ToList();
        }
        catch (Exception ex) { Program.LogError("playlists: " + ex.Message); return []; }
    }

    public static void Save(List<SavedPlaylist> list)
    {
        Directory.CreateDirectory(Folder);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartArray();
            foreach (var p in list)
            {
                w.WriteStartObject();
                w.WriteString("kind", p.Kind);
                w.WriteString("id", p.Id);
                w.WriteString("title", p.Title);
                w.WriteString("cover", p.Cover);
                w.WriteString("added", p.Added);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        File.WriteAllBytes(FilePath, ms.ToArray());
    }

    /// Turns a pasted Spotify link into a saved playlist with its name and cover (looked up on Spotify, no login).
    public static async Task<SavedPlaylist?> FromLink(string text)
    {
        var m = Link.Match(text.Trim());
        if (!m.Success) return null;
        var p = new SavedPlaylist { Kind = m.Groups[1].Value.ToLowerInvariant(), Id = m.Groups[2].Value };
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var url = Uri.EscapeDataString($"https://open.spotify.com/{p.Kind}/{p.Id}");
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://open.spotify.com/oembed?url={url}"));
            p.Title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            p.Cover = doc.RootElement.TryGetProperty("thumbnail_url", out var c) ? c.GetString() ?? "" : "";
        }
        catch (Exception ex) { Program.LogError("oembed: " + ex.Message); }
        if (p.Title.Length == 0) p.Title = $"Spotify {p.Kind}";
        return p;
    }
}
