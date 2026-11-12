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
        var procs = Process.GetProcessesByName("Spotify");
        if (procs.Length == 0) return new NowPlaying(false, false, "", "");
        // While a song plays the window title is "Artist - Song"; when paused it's just "Spotify ...".
        var title = procs.Select(p => { try { return p.MainWindowTitle; } catch { return ""; } })
                         .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        foreach (var p in procs) p.Dispose();
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
                Run("open", "-a", "Spotify");
                await Task.Delay(1500);
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
                // Windows: opening the link shows the playlist in Spotify; then press play.
                bool wasRunning = Status().Running;
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                await Task.Delay(wasRunning ? 1500 : 5000);
                Send(Command.Play);
            }
        }
        catch (Exception ex) { Program.LogError("spotify play: " + ex.Message); }
    }

    public static void OpenApp()
    {
        try
        {
            if (Platform.Mac) Run("open", "-a", "Spotify");
            else if (Platform.Linux) Process.Start(new ProcessStartInfo("spotify") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
        }
        catch (Exception ex) { Program.LogError("spotify open: " + ex.Message); }
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
