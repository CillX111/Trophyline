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
        "Trophyline --update      scan Steam once and update the journal\n" +
        "Trophyline --self-update install the newest Trophyline now (Windows, Linux)";

    [STAThread]
    static int Main(string[] args)
    {
        // Trophyline is in English: month and day names must match (a German Windows showed "MÄRZ", "Freitag").
        // Dates themselves keep the dd.MM.yyyy patterns written in the code.
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = english;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = english;
        System.Globalization.CultureInfo.CurrentCulture = english;
        System.Globalization.CultureInfo.CurrentUICulture = english;
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
                "--self-update" => SelfUpdate(),
                "--steam-signin" => SteamSignIn(),
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
        // A development build (bin folder) opened by hand: open the installed Trophyline instead,
        // so the window you get is always the real, self-updating one.
        if (args.Length == 0 && Platform.Windows && Environment.GetEnvironmentVariable("TROPHYLINE_HOME") == null
            && Environment.ProcessPath is string self && self.Contains(@"\bin\"))
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName, AppName + ".exe");
            if (File.Exists(installed))
            {
                Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true });
                return 0;
            }
        }
        bool normalWindow = args.Length == 0 || args[0] is not ("--snapshot" or "--popup");
        if (normalWindow)
        {
            // Only one window: a second start (e.g. clicking an achievement pop-up) brings the open one to the front.
            var showName = InstanceKey + "_show";
            if (EventWaitHandle.TryOpenExisting(showName, out var existing))
            {
                using (existing) existing.Set();
                return 0;
            }
            var show = windowLock = new EventWaitHandle(false, EventResetMode.AutoReset, showName);
            new Thread(() =>
            {
                try { while (show.WaitOne()) Avalonia.Threading.Dispatcher.UIThread.Post(App.BringToFront); }
                catch (ObjectDisposedException) { } // released for a restart
            }) { IsBackground = true, Name = "show window" }.Start();
            EnsureTrackerRunning();
        }
        App.Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError("unhandled: " + e.ExceptionObject);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static EventWaitHandle? windowLock;

    /// Opens the installed (possibly just updated) Trophyline in a new window. Call right before closing this one:
    /// it first lets go of the "one window only" lock, or the new window would just bring this one forward.
    internal static void RestartWindow()
    {
        try { windowLock?.Dispose(); } catch { }
        windowLock = null;
        if (Updater.InstalledExe() is string exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
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
        if (!firstInstance)
        {
            // started by the old tracker right after an update: wait for it to exit and take over
            if (!Environment.GetCommandLineArgs().Contains("--after-update")) return 0;
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) return 0; }
            catch (AbandonedMutexException) { }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError("tracker crashed: " + e.ExceptionObject);
        Updater.CleanUp();
        File.WriteAllText(PidFile, Environment.ProcessId.ToString());
        Platform.LowPriority();

        var (cfg, statsFolder, steamFolder) = LoadSetup();
        using var watcher = new Watcher(cfg, statsFolder, steamFolder);
        watcher.Start(); // also catches up on anything unlocked while it wasn't running
        using var games = new GameMonitor(steamFolder);
        games.Start();   // plays a game's soundtrack when you launch it
        // No silent updates in the background: the window asks you when you open it (see MainWindow).
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

    /// Sign in with Steam from the command line (the window has a button for this in Settings).
    static int SteamSignIn()
    {
        var r = SteamAccounts.SignIn(CancellationToken.None).GetAwaiter().GetResult();
        if (r.Id64 == null) return Fail("Steam sign-in failed: " + r.Error);
        var cfg = ReadConfig();
        cfg.SteamAccount = r.Id64;
        cfg.SteamSignedIn = true;
        SaveSettings(cfg);
        return Say($"Signed in. Trophyline now shows Steam account {r.Id64}.");
    }

    static int SelfUpdate()
    {
        // Once the new program has replaced this file, parts of .NET that aren't loaded yet can't be loaded
        // any more (they live inside the file). So load what's needed afterwards first.
        bool trackerWasRunning = TrackerRunning();
        _ = typeof(Console).Assembly;
        var r = Updater.CheckAndInstall().GetAwaiter().GetResult();
        if (r == null) return Fail("Couldn't reach GitHub to check for updates.");
        if (!r.Newer) return Say($"Trophyline {Updater.Current.ToString(3)} is the newest version.");
        if (!r.Installed) return Fail($"Trophyline {r.Latest.ToString(3)} is out. {r.Note ?? ""}\n{Updater.ReleasesPage}");
        if (trackerWasRunning) RestartTracker();
        return Say($"Updated Trophyline {Updater.Current.ToString(3)} -> {r.Latest.ToString(3)}.");
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

    /// After an update: stop the old tracker and start the new program as the tracker.
    internal static void RestartTracker()
    {
        Stop(false);
        EnsureTrackerRunning();
    }

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

    /// Three processes (tracker, window, pop-ups) read and save config.json, so reads retry briefly
    /// and saves go through a temp file that replaces config.json in one step.
    static Config ReadConfig()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    var fresh = new Config();
                    SaveSettings(fresh);
                    return fresh;
                }
                string text;
                using (var fs = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs))
                    text = r.ReadToEnd();
                if (text.Trim().Length > 0 && JsonSerializer.Deserialize(text, Json.Config) is Config cfg) return cfg;
                last = new JsonException("config.json is empty");
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { last = ex; }
            Thread.Sleep(60);
        }
        LogError("config: " + last?.Message);
        return new Config { Unreadable = true };
    }

    internal static string ReadLanguage() => ReadConfig().Language;

    internal static Config Settings() => ReadConfig();

    internal static void SaveSettings(Config cfg)
    {
        if (cfg.Unreadable && File.Exists(ConfigPath)) { LogError("config save skipped: config.json couldn't be read"); return; }
        var tmp = ConfigPath + "." + Environment.ProcessId + ".tmp";
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json.Config));
                File.Move(tmp, ConfigPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5) LogError("config save: " + ex.Message);
                else Thread.Sleep(60);
            }
        }
        try { File.Delete(tmp); } catch { }
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
    /// Set when config.json couldn't be read: such a stand-in must never be saved over the real file.
    [JsonIgnore] public bool Unreadable { get; set; }

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
    /// Your Trophyline username (empty for a profile from before accounts existed).
    public string OnlineUser { get; set; } = "";
    public string OnlineId { get; set; } = "";
    public string OnlineSecret { get; set; } = "";
    /// Leave empty to use the public Trophyline server (a Supabase project URL and its publishable key).
    public string Server { get; set; } = "";
    public string ServerKey { get; set; } = "";
    /// Usernames shown on your online profile, e.g. { "discord": "name", "instagram": "name" }.
    public Dictionary<string, string> Links { get; set; } = new();
    /// Fingerprint of the profile picture last uploaded (so it's only sent when it changes).
    public string AvatarHash { get; set; } = "";
    /// Install new versions automatically (Windows and Linux).
    public bool AutoUpdate { get; set; } = true;
    /// The first-start notice was read and accepted.
    public bool WelcomeAccepted { get; set; }
    /// The Steam account to show (its 17-digit ID). Empty = whoever is signed in to Steam right now.
    public string SteamAccount { get; set; } = "";
    /// True when that account was chosen with "Sign in with Steam".
    public bool SteamSignedIn { get; set; }
    /// The Steam account your online profile belongs to (so another account's numbers never overwrite it).
    public string OnlineSteamAccount { get; set; } = "";
    /// The Steam account linked to the Trophyline account for verified scores (empty = not linked).
    public string VerifiedSteam { get; set; } = "";
    /// What was last sent to the server as your name, icon and links (so unchanged details aren't re-sent).
    public string UploadedProfile { get; set; } = "";
    /// When the server last checked your scores with Steam (Unix time).
    public long LastVerify { get; set; }
    /// Accent colour of the window, e.g. "#66C0F4".
    public string Accent { get; set; } = Palette.Default;
}

[JsonSerializable(typeof(Config))]
internal partial class ConfigJson : JsonSerializerContext { }
