using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Trophyline;

static partial class Program
{
    public const string AppName = "Trophyline";

    public static readonly string Home =
        Environment.GetEnvironmentVariable("TROPHYLINE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    static string ConfigPath => Path.Combine(Home, "config.json");
    static string ErrorLog => Path.Combine(Home, "errors.log");
    static string InstanceKey => $"Local\\{AppName}_{StableHash(Home.ToLowerInvariant()):x8}";

    const string Usage =
        "Trophyline - keeps a dated journal of your Steam achievements.\n\n" +
        "Trophyline.exe               open the journal window\n" +
        "Trophyline.exe --install     start the tracker now and at every login\n" +
        "Trophyline.exe --uninstall   remove from login and stop\n" +
        "Trophyline.exe --background  run the tracker (no window)\n" +
        "Trophyline.exe --stop        stop the tracker\n" +
        "Trophyline.exe --update      scan Steam once and update the journal";

    static readonly string ShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Trophyline.lnk");

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(Home);
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        try
        {
            return cmd switch
            {
                "" or "--open" => OpenWindow(),
                "--snapshot" => Snapshot(args), // dev check: render the window to a PNG without showing it
                "--selftest" => SelfTest(),     // dev check: summary of the Steam data the window would show
                "--background" => Run(),
                "--update" => UpdateOnce(),
                "--stop" => Stop(true),
                "--install" => Install(),
                "--uninstall" => Uninstall(),
                _ => Fail(Usage),
            };
        }
        catch (Exception ex)
        {
            LogError(ex.ToString());
            return Fail("Error: " + ex.Message);
        }
    }

    // ---------- commands ----------

    static int Run()
    {
        using var mutex = new Mutex(true, InstanceKey + "_run", out bool firstInstance);
        if (!firstInstance) return 0;

        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceKey + "_stop");
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }

        var (cfg, statsFolder, steamFolder) = LoadSetup();
        using var watcher = new Watcher(cfg, statsFolder, steamFolder);
        watcher.Start(); // also catches up on anything unlocked while it wasn't running
        TrimMemory();

        stop.WaitOne(); // sleeps here with no CPU use until --stop
        return 0;
    }

    static int UpdateOnce()
    {
        var (cfg, statsFolder, steamFolder) = LoadSetup();
        using var watcher = new Watcher(cfg, statsFolder, steamFolder);
        var (added, total) = watcher.ScanAll();
        Show(added == 0
            ? $"No new achievements. The journal has {total}."
            : $"Added {added} achievement(s). The journal has {total}.");
        return 0;
    }

    /// The window. It only reads the journal; the background tracker does all the writing.
    static int OpenWindow()
    {
        EnsureTrackerRunning();
        var app = NewApp(System.Windows.ShutdownMode.OnMainWindowClose);
        app.Run(new JournalWindow());
        return 0;
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
        sb.AppendLine($"persona={data.PersonaName} load={load}ms total={sw.ElapsedMilliseconds}ms");
        sb.AppendLine($"games={g.Count} installed={g.Count(x => x.Installed)} withAch={g.Count(x => x.Total > 0)} perfect={g.Count(x => x.Perfect)} unnamed={g.Count(x => x.Name == "")}");
        sb.AppendLine($"achievements unlocked={all.Count(a => a.Unlocked)} total={all.Count} withRarity={all.Count(a => a.Rarity != null)}");
        sb.AppendLine($"playtime={g.Sum(x => x.PlaytimeMinutes) / 60.0:F1}h 2wks={g.Sum(x => x.Playtime2WeeksMinutes) / 60.0:F1}h");
        sb.AppendLine($"art: portrait={g.Count(x => x.Portrait != null)} hero={g.Count(x => x.Hero != null)} logo={g.Count(x => x.Logo != null)} header={g.Count(x => x.Header != null)}");
        foreach (var x in g.OrderByDescending(x => x.LastPlayed).Take(8))
            sb.AppendLine($"  {x.AppId,-8} {x.Name,-40} {x.PlaytimeMinutes / 60.0,6:F1}h  ach {x.UnlockedCount}/{x.Total}  inst={x.Installed}");
        var rare = all.Where(a => a.Unlocked && a.Rarity != null).OrderBy(a => a.Rarity).Take(3);
        foreach (var a in rare) sb.AppendLine($"  rarest: {a.Name} {a.Rarity:F1}%");
        File.WriteAllText(Path.Combine(Home, "selftest.txt"), sb.ToString());
        return 0;
    }

    /// --snapshot out.png [page] [arg] [height]   page: overview | achievements | games | game | storage
    static int Snapshot(string[] args)
    {
        var app = NewApp(System.Windows.ShutdownMode.OnExplicitShutdown);
        double h = args.Length > 4 && double.TryParse(args[4], out var hh) ? hh : 1000;
        var w = new JournalWindow
        {
            WindowState = System.Windows.WindowState.Normal, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
            ShowActivated = false, Left = -10000, Top = -10000, MinHeight = 0, Width = 1280, Height = h,
        };
        w.ContentRendered += async (_, _) =>
        {
            await w.Ready;
            w.NavigateForSnapshot(args.Length > 2 ? args[2] : "overview", args.Length > 3 && args[3].Length > 0 ? args[3] : null);
            await Task.Delay(3000); // let animations, timers and web icons settle
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            var root = (System.Windows.FrameworkElement)w.Content;
            double width = root.ActualWidth + root.Margin.Left + root.Margin.Right;
            double height = root.ActualHeight + root.Margin.Top + root.Margin.Bottom;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(width * dpi.DpiScaleX), (int)(height * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Shapes.Rectangle { Width = width, Height = height, Fill = w.Background };
            bg.Measure(new System.Windows.Size(width, height));
            bg.Arrange(new System.Windows.Rect(0, 0, width, height));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(args.Length > 1 ? args[1] : Path.Combine(Home, "snapshot.png"))) enc.Save(fs);
            File.WriteAllText(Path.Combine(Home, "snapshot.txt"), w.DescribeView());
            app.Shutdown();
        };
        w.Show();
        app.Run();
        return 0;
    }

    static System.Windows.Application NewApp(System.Windows.ShutdownMode mode)
    {
        var app = new System.Windows.Application { ShutdownMode = mode };
        app.DispatcherUnhandledException += (_, e) =>
        {
            LogError(e.Exception.ToString());
            Show("Something went wrong: " + e.Exception.Message);
            e.Handled = true;
            app.Shutdown(1);
        };
        return app;
    }

    internal static bool TrackerRunning()
    {
        if (!Mutex.TryOpenExisting(InstanceKey + "_run", out var m)) return false;
        m.Dispose();
        return true;
    }

    internal static string ReadLanguage()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), Json.Config)?.Language ?? "english";
        }
        catch (Exception) { }
        return "english";
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

    static void EnsureTrackerRunning()
    {
        if (Mutex.TryOpenExisting(InstanceKey + "_run", out var m)) { m.Dispose(); return; }
        if (Environment.ProcessPath is string exe)
            Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false });
    }

    static void CreateShortcut(string exe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell missing.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Your Steam achievements, with dates";
            link.Save();
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static int Stop(bool report)
    {
        if (EventWaitHandle.TryOpenExisting(InstanceKey + "_stop", out var h))
        {
            using (h) h.Set();
            if (report) Show("Trophyline stopped.");
        }
        else if (report) Show("Trophyline is not running.");
        return 0;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static int Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find own path.");
        LoadSetup(); // fails early if Steam isn't found
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(AppName, $"\"{exe}\" --background");
        CreateShortcut(exe);
        EnsureTrackerRunning();
        Show("Trophyline is set up.\n\n" +
             "The tracker runs in the background and starts at every login.\n\n" +
             "To pin the window: open Start, type \"Trophyline\", " +
             "right-click it and choose \"Pin to taskbar\".");
        return 0;
    }

    static int Uninstall()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        Stop(false);
        Show("Trophyline removed from startup and stopped.\nYour journal was kept.");
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

    static (Config, string statsFolder, string? steamFolder) LoadSetup()
    {
        Config cfg;
        if (File.Exists(ConfigPath))
            cfg = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), Json.Config) ?? new Config();
        else
        {
            cfg = new Config();
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json.Config));
        }

        var steam = Steam.FindSteamFolder();
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

    static int Fail(string msg) { Show(msg); return 1; }

    static void Show(string msg)
    {
        try { File.WriteAllText(Path.Combine(Home, "last-message.txt"), msg); } catch { }
        if (Environment.GetEnvironmentVariable("TROPHYLINE_QUIET") == "1") return;
        MessageBoxW(IntPtr.Zero, msg, AppName, 0x40);
    }

    public static void TrimMemory()
    {
        // Aggressive = also return the freed heap memory to Windows, not just mark it free.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        EmptyWorkingSet(new IntPtr(-1));
    }

    static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr process);
}

public sealed class Config
{
    /// Steam language for achievement names, e.g. "english" or "german".
    public string Language { get; set; } = "english";
    /// Leave empty to find Steam automatically.
    public string StatsFolder { get; set; } = "";
}

[JsonSerializable(typeof(Config))]
internal partial class ConfigJson : JsonSerializerContext { }
