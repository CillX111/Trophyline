using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Trophyline;

/// Which saved playlist belongs to which game (music/soundtracks.json: { "appid": "kind:id" }).
static class SoundtrackStore
{
    static string FilePath => Path.Combine(Program.Home, "music", "soundtracks.json");

    public static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        catch (Exception ex) { Program.LogError("soundtracks: " + ex.Message); return []; }
    }

    public static void Set(string appId, SavedPlaylist? p)
    {
        var map = Load();
        if (p == null) map.Remove(appId);
        else map[appId] = $"{p.Kind}:{p.Id}";
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (k, v) in map) w.WriteString(k, v);
            w.WriteEndObject();
        }
        File.WriteAllBytes(FilePath, ms.ToArray());
    }

    public static SavedPlaylist? For(string appId)
    {
        if (!Load().TryGetValue(appId, out var key)) return null;
        return PlaylistStore.Load().FirstOrDefault(p => $"{p.Kind}:{p.Id}" == key);
    }
}

/// Part of the background tracker: notices when a Steam game starts or quits and plays / pauses
/// that game's soundtrack. Windows waits on Steam's registry key (no polling, no CPU while idle);
/// Linux and macOS watch Steam's registry.vdf file.
sealed class GameMonitor : IDisposable
{
    readonly string? steamFolder;
    string current = "0";
    string? playingFor;          // the game whose soundtrack we started
    volatile bool stopped;
    Thread? thread;
    FileSystemWatcher? fsw;
    Timer? debounce;

    public GameMonitor(string? steamFolder) => this.steamFolder = steamFolder;

    public void Start()
    {
        current = RunningAppId();
        SessionLog.Resume(current);
        if (Platform.Windows)
        {
            thread = new Thread(WaitOnRegistry) { IsBackground = true, Name = "game monitor" };
            thread.Start();
        }
        else if (RegistryVdf() is string vdf && File.Exists(vdf))
        {
            debounce = new Timer(_ => Check(), null, Timeout.Infinite, Timeout.Infinite);
            fsw = new FileSystemWatcher(Path.GetDirectoryName(vdf)!, Path.GetFileName(vdf))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler h = (_, _) => debounce.Change(1000, Timeout.Infinite);
            fsw.Changed += h;
            fsw.Created += h;
            fsw.EnableRaisingEvents = true;
        }
    }

    void WaitOnRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam");
            while (!stopped)
            {
                // Blocks until a value under the key changes.
                int err = RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), false, REG_NOTIFY_CHANGE_LAST_SET, IntPtr.Zero, false);
                if (err != 0) { Thread.Sleep(30_000); continue; }
                Check();
            }
        }
        catch (Exception ex) { Program.LogError("game monitor: " + ex.Message); }
    }

    void Check()
    {
        var now = RunningAppId();
        if (now == current) return;
        var before = current;
        current = now;
        SessionLog.Changed(before, now);
        try { OnChange(before, now).GetAwaiter().GetResult(); }
        catch (Exception ex) { Program.LogError("soundtrack: " + ex.Message); }
        Program.TrimMemory();
    }

    async Task OnChange(string before, string now)
    {
        if (now != "0" && SoundtrackStore.For(now) is SavedPlaylist p)
        {
            Program.LogWindow($"game {now} started: playing soundtrack {p.Title}");
            playingFor = now;
            await WithNormalPriority(() => Spotify.PlayAsync(p));
        }
        else if (now == "0" && before == playingFor)
        {
            playingFor = null;
            if (Program.Settings().PauseSoundtrackOnQuit)
            {
                Program.LogWindow($"game {before} closed: pausing soundtrack");
                Spotify.Send(Spotify.Command.Pause);
            }
        }
    }

    /// The tracker runs at idle priority and Windows hands that to programs it starts.
    /// Spotify must not inherit it (music would stutter while gaming), so start it at normal priority.
    static async Task WithNormalPriority(Func<Task> action)
    {
        var me = System.Diagnostics.Process.GetCurrentProcess();
        try { me.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch { }
        try { await action(); }
        finally { Platform.LowPriority(); }
    }

    public string RunningAppId()
    {
        try
        {
            if (Platform.Windows)
                return Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("RunningAppID") is int id ? id.ToString() : "0";
            if (RegistryVdf() is string vdf && File.Exists(vdf))
            {
                var steam = Vdf.Path(Vdf.ParseFile(vdf), "Registry", "HKCU", "Software", "Valve", "Steam");
                return steam != null && steam.TryGetValue("RunningAppID", out var v) && v is string s && s.Length > 0 ? s : "0";
            }
        }
        catch (Exception) { }
        return "0";
    }

    string? RegistryVdf()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Platform.Linux)
        {
            // normal Steam, Flatpak Steam, Snap Steam
            string[] candidates =
            [
                Path.Combine(home, ".steam", "registry.vdf"),
                Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "registry.vdf"),
                Path.Combine(home, "snap", "steam", "common", ".steam", "registry.vdf"),
            ];
            if (candidates.FirstOrDefault(File.Exists) is string found) return found;
        }
        return steamFolder != null ? Path.Combine(steamFolder, "registry.vdf") : null;
    }

    public void Dispose()
    {
        stopped = true;
        fsw?.Dispose();
        debounce?.Dispose();
    }

    const int REG_NOTIFY_CHANGE_LAST_SET = 4;

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, int filter, IntPtr hEvent, bool asynchronous);
}
