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

    public enum Command { PlayPause, Next, Previous, Play, Pause }

    public static void Send(Command c)
    {
        try
        {
            if (Platform.Windows)
            {
                if (c == Command.Play && Status().Playing) return;
                if (c == Command.Pause && !Status().Playing) return;
                MediaKey(c switch { Command.Next => 0xB0, Command.Previous => 0xB1, _ => 0xB3 });
            }
            else if (Platform.Mac)
                Run("osascript", "-e", "tell application \"Spotify\" to " + c switch
                {
                    Command.Next => "next track",
                    Command.Previous => "previous track",
                    Command.Play => "play",
                    Command.Pause => "pause",
                    _ => "playpause",
                });
            else if (Platform.Linux)
                Run("dbus-send", "--print-reply", "--dest=org.mpris.MediaPlayer2.spotify", "/org/mpris/MediaPlayer2",
                    "org.mpris.MediaPlayer2.Player." + c switch
                    {
                        Command.Next => "Next",
                        Command.Previous => "Previous",
                        Command.Play => "Play",
                        Command.Pause => "Pause",
                        _ => "PlayPause",
                    });
        }
        catch (Exception ex) { Program.LogError("spotify: " + ex.Message); }
    }

    /// Starts Spotify if needed and plays the playlist / album / artist.
    /// Returns false when Spotify (Windows) still isn't playing after a few tries.
    public static async Task<bool> PlayAsync(SavedPlaylist p)
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
                // A freshly started or busy Spotify sometimes ignores the first press: check and try again.
                for (int i = 0; i < 3; i++)
                {
                    Send(Command.Play);
                    if (await WaitUntilPlaying(TimeSpan.FromSeconds(2.5))) break;
                }
                if (tuck) await TuckAway(hide, TimeSpan.FromSeconds(1));
                if (!Status().Playing)
                {
                    Program.LogError("spotify play: Spotify didn't start playing " + uri);
                    return false;
                }
            }
        }
        catch (Exception ex) { Program.LogError("spotify play: " + ex.Message); }
        return true;
    }

    static async Task<bool> WaitUntilPlaying(TimeSpan max)
    {
        var until = DateTime.Now + max;
        while (DateTime.Now < until)
        {
            if (Status().Playing) return true;
            await Task.Delay(250);
        }
        return false;
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

    // ---------- the queue (Windows) ----------
    // Spotify's desktop app doesn't share its queue with other programs (and the Web API needs Premium).
    // So Trophyline reads it from Spotify's own window through Windows accessibility. Spotify only builds
    // that while its window is "shown", so the window is shown far off-screen without focus and without a
    // taskbar button for about a second, then put back exactly as it was. You never see Spotify open.

    static readonly SemaphoreSlim queueGate = new(1, 1);

    /// What's playing and what plays next, read from Spotify without showing it. Error text when it couldn't.
    public static async Task<(SpotifyQueue? Queue, string? Error)> ReadQueueAsync()
    {
        if (!Platform.Windows) return (null, "The queue can only be shown on Windows.");
        if (!await queueGate.WaitAsync(0)) return (null, null); // a read is already running
        try
        {
            var h = Win.FindMainWindow(SpotifyPids()).Handle;
            if (h == IntPtr.Zero) return (null, "Spotify isn't open.");
            bool visible = Win.IsWindowVisible(h), minimized = Win.IsIconic(h);
            bool onScreen = visible && !minimized; // shown normally: read it where it is
            var place = Win.Placement(h);
            int exStyle = Win.GetWindowLong(h, Win.GWL_EXSTYLE);
            try
            {
                if (!onScreen)
                {
                    // no taskbar button, then "show" it far off-screen without taking focus
                    Win.SetWindowLong(h, Win.GWL_EXSTYLE, (exStyle | Win.WS_EX_TOOLWINDOW) & ~Win.WS_EX_APPWINDOW);
                    var off = place;
                    int w = place.Normal.Right - place.Normal.Left, ht = place.Normal.Bottom - place.Normal.Top;
                    off.Normal = new Win.RECT { Left = -32000, Top = -32000, Right = -32000 + Math.Max(w, 900), Bottom = -32000 + Math.Max(ht, 700) };
                    off.ShowCmd = Win.SW_SHOWNOACTIVATE;
                    off.Flags = 0;
                    Win.SetPlacement(h, off);
                }
                var json = await Task.Run(() => RunQueueScript(h, closeAfter: true)); // leave Spotify's panels as they were
                return json == null ? (null, "Couldn't read Spotify's queue right now.") : (SpotifyQueue.Parse(json), null);
            }
            finally
            {
                if (!onScreen)
                {
                    // back exactly as it was: hidden stays hidden, minimized stays minimized, same normal size and place
                    var back = place;
                    back.ShowCmd = visible ? Win.SW_SHOWMINNOACTIVE : Win.SW_HIDE;
                    Win.SetPlacement(h, back);
                    Win.SetWindowLong(h, Win.GWL_EXSTYLE, exStyle);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError("spotify queue: " + ex.Message);
            return (null, "Couldn't read Spotify's queue right now.");
        }
        finally { queueGate.Release(); }
    }

    /// Spotify's "Queue" toggle in several languages (the button's accessible name follows Spotify's language).
    static readonly string[] QueueNames = ["Queue", "Warteschlange", "File d'attente", "Cola", "Coda", "Fila", "Wachtrij", "Kolejka", "Kö", "Kø", "Jono"];

    /// Opens Spotify's queue panel (closing it again afterwards if it was closed) and prints it as JSON:
    /// [{ "h": heading, "t": [[title, artists], ...] }, ...]. Null when it didn't work.
    static string? RunQueueScript(IntPtr hwnd, bool closeAfter)
    {
        var names = string.Join(",", QueueNames.Select(n => "'" + n.Replace("'", "''") + "'"));
        var script = $$"""
            Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
            $A = [Windows.Automation.AutomationElement]; $T = [Windows.Automation.ControlType]; $S = [Windows.Automation.TreeScope]
            $root = $A::FromHandle([IntPtr]{{hwnd.ToInt64()}})
            $names = @({{names}})
            $bc = New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $T::Button)
            $btn = $null
            for ($i = 0; $i -lt 30 -and -not $btn; $i++) {
              foreach ($b in $root.FindAll($S::Descendants, $bc)) {
                if ($names -contains $b.Current.Name) { try { $null = $b.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern); $btn = $b; break } catch {} }
              }
              if (-not $btn) { Start-Sleep -Milliseconds 150 }
            }
            if (-not $btn) { exit 2 }
            $tog = $btn.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
            $was = $tog.Current.ToggleState
            if ($was -ne [Windows.Automation.ToggleState]::On) { $tog.Toggle() }
            $pc = New-Object Windows.Automation.AndCondition((New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $T::Pane)), (New-Object Windows.Automation.PropertyCondition($A::NameProperty, $btn.Current.Name)))
            $gc = New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $T::DataGrid)
            $ic = New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $T::TreeItem)
            $lc = New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, $T::Hyperlink)
            $out = @()
            for ($i = 0; $i -lt 20; $i++) {
              $pane = $root.FindFirst($S::Descendants, $pc)
              if ($pane) {
                $w = [Windows.Automation.TreeWalker]::RawViewWalker
                $heading = ''
                $c = $w.GetFirstChild($pane)
                while ($c) {
                  if ($c.Current.ControlType -eq $T::Text) { $heading = $c.Current.Name }
                  elseif ($c.Current.ControlType -eq $T::DataGrid) {
                    $tracks = @()
                    foreach ($it in $c.FindAll($S::Children, $ic)) {
                      $artists = @($it.FindAll($S::Descendants, $lc) | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
                      $tracks += ,@($it.Current.Name, ($artists -join ', '))
                      if ($tracks.Count -ge 50) { break }
                    }
                    $out += [pscustomobject]@{ h = $heading; t = $tracks }
                  }
                  $c = $w.GetNextSibling($c)
                }
                if ($out.Count -gt 0) { break }
              }
              Start-Sleep -Milliseconds 150
            }
            if ({{(closeAfter ? "$true" : "$false")}} -and $was -ne [Windows.Automation.ToggleState]::On) { $tog.Toggle() }
            if ($out.Count -eq 0) { exit 3 }
            [Console]::OutputEncoding = [Text.Encoding]::UTF8
            ConvertTo-Json -InputObject @($out) -Depth 5 -Compress
            exit 0
            """;
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                                  Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return null;
        var stdout = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
        return p.ExitCode == 0 ? stdout.Result.Trim() : null;
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
        public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9, SW_SHOWNOACTIVATE = 4;
        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int Length, Flags, ShowCmd;
            public POINT MinPosition, MaxPosition;
            public RECT Normal;
        }

        public static WINDOWPLACEMENT Placement(IntPtr h)
        {
            var p = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(h, ref p);
            return p;
        }

        public static void SetPlacement(IntPtr h, WINDOWPLACEMENT p)
        {
            p.Length = Marshal.SizeOf<WINDOWPLACEMENT>();
            SetWindowPlacement(h, ref p);
        }

        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);

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

public sealed record QueueTrack(string Title, string Artists);
public sealed record QueueSection(string Heading, List<QueueTrack> Tracks);

/// Spotify's queue as its queue panel shows it: "Now playing", then e.g. "Next in queue" and "Next from: …".
public sealed class SpotifyQueue
{
    public List<QueueSection> Sections { get; } = [];

    public static SpotifyQueue Parse(string json)
    {
        var q = new SpotifyQueue();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        foreach (var s in list)
        {
            var heading = s.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
            var tracks = new List<QueueTrack>();
            if (s.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var tr in t.EnumerateArray())
                {
                    if (tr.ValueKind != JsonValueKind.Array) continue;
                    var parts = tr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList();
                    if (parts.Count > 0 && parts[0].Length > 0) tracks.Add(new QueueTrack(parts[0], parts.Count > 1 ? parts[1] : ""));
                }
            if (tracks.Count > 0) q.Sections.Add(new QueueSection(heading, tracks));
        }
        return q;
    }
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
