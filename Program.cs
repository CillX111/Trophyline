using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace Trophyline;

static class Program
{
    public const string AppName = "Trophyline";

    public static readonly string Home =
        Environment.GetEnvironmentVariable("TROPHYLINE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    static string ConfigPath => Path.Combine(Home, "config.json");
    static string ErrorLog => Path.Combine(Home, "errors.log");
    static string PidFile => Path.Combine(Home, "tracker.pid");
    static string InstanceKey => $"{AppName}_{StableHash(Home.ToLowerInvariant()):x8}";

    const string Usage =
        "Trophyline - your Steam achievements, playtime and library.\n\n" +
        "Trophyline               open the window\n" +
        "Trophyline --install     start the tracker now and at every login, add to the app menu\n" +
        "Trophyline --uninstall   undo --install and stop the tracker\n" +
        "Trophyline --background  run the tracker (no window)\n" +
        "Trophyline --stop        stop the tracker\n" +
        "Trophyline --update      scan Steam once and update the journal";

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(Home);
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd.StartsWith("-psn_")) cmd = ""; // old macOS Finder launch argument
        try
        {
            return cmd switch
            {
                "" or "--open" => OpenWindow(args),
                "--snapshot" => OpenWindow(args), // dev check: render the window to a PNG without showing it
                "--popup" => OpenWindow(args),    // started by the tracker: one achievement pop-up, then exit
                "--selftest" => SelfTest(),       // dev check: summary of the Steam data the window would show
                "--background" => Run(),
                "--update" => UpdateOnce(),
                "--stop" => Stop(true),
                "--install" => Install(),
                "--uninstall" => Uninstall(),
                "--help" or "-h" or "/?" => Say(Usage),
                _ => Fail(Usage),
            };
        }
        catch (Exception ex)
        {
            LogError(ex.ToString());
            return Fail("Error: " + ex.Message);
        }
    }

    // ---------- the window ----------

    static int OpenWindow(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--snapshot" or "--popup")) EnsureTrackerRunning();
        App.Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError("unhandled: " + e.ExceptionObject);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();

    // ---------- the tracker ----------

    static int Run()
    {
        using var mutex = new Mutex(true, InstanceKey + "_run", out bool firstInstance);
        if (!firstInstance) return 0;
        File.WriteAllText(PidFile, Environment.ProcessId.ToString());
        Platform.LowPriority();

        var (cfg, statsFolder, steamFolder) = LoadSetup();
        using var watcher = new Watcher(cfg, statsFolder, steamFolder);
        watcher.Start(); // also catches up on anything unlocked while it wasn't running
        using var games = new GameMonitor(steamFolder);
        games.Start();   // plays a game's soundtrack when you launch it
        Platform.TrimMemory();

        Thread.Sleep(Timeout.Infinite); // sleeps here with no CPU use; --stop ends the process
        return 0;
    }

    static int UpdateOnce()
    {
        var (cfg, statsFolder, steamFolder) = LoadSetup();
        using var watcher = new Watcher(cfg, statsFolder, steamFolder);
        var (added, total) = watcher.ScanAll();
        return Say(added == 0
            ? $"No new achievements. The journal has {total}."
            : $"Added {added} achievement(s). The journal has {total}.");
    }

    static Process? TrackerProcess()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid)) return null;
            var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith(AppName, StringComparison.OrdinalIgnoreCase) ? p : null;
        }
        catch (Exception) { return null; }
    }

    internal static bool TrackerRunning() => TrackerProcess() != null;

    static void EnsureTrackerRunning()
    {
        if (TrackerRunning() || Environment.ProcessPath is not string exe) return;
        Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false });
    }

    static int Stop(bool report)
    {
        var p = TrackerProcess();
        if (p != null)
        {
            p.Kill();
            p.WaitForExit(3000);
            try { File.Delete(PidFile); } catch (IOException) { }
            return report ? Say("Trophyline tracker stopped.") : 0;
        }
        return report ? Say("Trophyline tracker is not running.") : 0;
    }

    static int Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find own path.");
        LoadSetup(); // fails early if Steam isn't found
        Platform.Install(exe);
        EnsureTrackerRunning();
        return Say("Trophyline is set up.\n\nThe tracker runs in the background and starts at every login.\n\n" + Platform.InstallHint);
    }

    static int Uninstall()
    {
        Platform.Uninstall();
        Stop(false);
        return Say("Trophyline removed from startup and stopped.\nYour journal was kept.");
    }

    static int SelfTest()
    {
        var cfg = LoadSetup().Item1;
        var sw = Stopwatch.StartNew();
        var data = SteamData.Load(cfg.Language);
        var load = sw.ElapsedMilliseconds;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
        {
            NameCache.FetchMissing(data.Games, http).GetAwaiter().GetResult();
            Rarity.Refresh(data.Games, http).GetAwaiter().GetResult();
        }
        var g = data.Games;
        var all = g.SelectMany(x => x.Achievements).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"os={Environment.OSVersion} steam={SteamFolder()}");
        sb.AppendLine($"persona={data.PersonaName} load={load}ms total={sw.ElapsedMilliseconds}ms");
        sb.AppendLine($"games={g.Count} installed={g.Count(x => x.Installed)} withAch={g.Count(x => x.Total > 0)} perfect={g.Count(x => x.Perfect)}");
        sb.AppendLine($"achievements unlocked={all.Count(a => a.Unlocked)} total={all.Count} withRarity={all.Count(a => a.Rarity != null)}");
        sb.AppendLine($"art: portrait={g.Count(x => x.Portrait != null)} hero={g.Count(x => x.Hero != null)} logo={g.Count(x => x.Logo != null)}");
        File.WriteAllText(Path.Combine(Home, "selftest.txt"), sb.ToString());
        Console.Write(sb.ToString());
        return 0;
    }

    // ---------- setup ----------

    static readonly ConfigJson Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    static Config ReadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), Json.Config) ?? new Config();
            var cfg = new Config();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json.Config));
            return cfg;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LogError("config: " + ex.Message);
            return new Config();
        }
    }

    internal static string ReadLanguage() => ReadConfig().Language;

    internal static Config Settings() => ReadConfig();

    internal static void SaveSettings(Config cfg)
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json.Config)); }
        catch (IOException ex) { LogError("config save: " + ex.Message); }
    }

    /// The Steam folder: from config.json if set, otherwise found automatically.
    internal static string? SteamFolder()
    {
        var cfg = ReadConfig();
        if (!string.IsNullOrWhiteSpace(cfg.SteamFolder) && Directory.Exists(cfg.SteamFolder)) return Path.GetFullPath(cfg.SteamFolder);
        return Platform.FindSteamFolder();
    }

    static (Config, string statsFolder, string? steamFolder) LoadSetup()
    {
        var cfg = ReadConfig();
        var steam = SteamFolder();
        var stats = !string.IsNullOrWhiteSpace(cfg.StatsFolder) ? cfg.StatsFolder
                  : steam != null ? Path.Combine(steam, "appcache", "stats")
                  : throw new DirectoryNotFoundException("Steam isn't installed (or was never started).");
        if (!Directory.Exists(stats))
            throw new DirectoryNotFoundException($"Steam stats folder not found: {stats}");
        return (cfg, stats, steam);
    }

    // ---------- helpers ----------

    public static void LogError(string msg)
    {
        try { File.AppendAllText(ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n"); } catch { }
    }

    /// Small diagnostics log for the window (kept short).
    internal static void LogWindow(string msg)
    {
        try
        {
            var path = Path.Combine(Home, "window.log");
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid={Environment.ProcessId}  {msg}\n");
        }
        catch { }
    }

    static int Fail(string msg) { Say(msg); return 1; }

    static int Say(string msg)
    {
        try { File.WriteAllText(Path.Combine(Home, "last-message.txt"), msg); } catch { }
        if (Environment.GetEnvironmentVariable("TROPHYLINE_QUIET") != "1") Platform.Message(msg);
        return 0;
    }

    public static void TrimMemory() => Platform.TrimMemory();

    static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}

public sealed class Config
{
    /// Steam language for achievement names, e.g. "english" or "german".
    public string Language { get; set; } = "english";
    /// Leave empty to find Steam automatically.
    public string SteamFolder { get; set; } = "";
    /// Leave empty to use <Steam>/appcache/stats.
    public string StatsFolder { get; set; } = "";
    /// Run Spotify as a background process with its window hidden (Windows).
    public bool HideSpotify { get; set; } = true;
    /// Pause a game's soundtrack when you quit that game.
    public bool PauseSoundtrackOnQuit { get; set; } = true;
    /// Show a pop-up in the corner of the screen when you unlock an achievement.
    public bool UnlockPopups { get; set; } = true;
    /// Online leaderboards (off until you switch them on).
    public bool OnlineEnabled { get; set; }
    public string OnlineName { get; set; } = "";
    public string OnlineId { get; set; } = "";
    public string OnlineSecret { get; set; } = "";
    /// Leave empty to use the public Trophyline server.
    public string Server { get; set; } = "";
    /// Accent colour of the window, e.g. "#66C0F4".
    public string Accent { get; set; } = Palette.Default;
}

[JsonSerializable(typeof(Config))]
internal partial class ConfigJson : JsonSerializerContext { }
