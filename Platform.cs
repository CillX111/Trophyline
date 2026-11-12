using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Trophyline;

/// Everything that differs between Windows, Linux and macOS.
static partial class Platform
{
    public static bool Windows => OperatingSystem.IsWindows();
    public static bool Mac => OperatingSystem.IsMacOS();
    public static bool Linux => OperatingSystem.IsLinux();

    static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // ---------- Steam ----------

    public static string? FindSteamFolder()
    {
        var candidates = new List<string>();
        if (Windows && Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string p)
            candidates.Add(p.Replace('/', '\\'));
        if (Windows)
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        if (Mac)
            candidates.Add(Path.Combine(HomeDir, "Library", "Application Support", "Steam"));
        if (Linux)
        {
            candidates.Add(Path.Combine(HomeDir, ".steam", "steam"));
            candidates.Add(Path.Combine(HomeDir, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(HomeDir, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")); // Flatpak
            candidates.Add(Path.Combine(HomeDir, "snap", "steam", "common", ".local", "share", "Steam"));          // Snap
        }
        return candidates.Select(c => Path.GetFullPath(c))
                         .FirstOrDefault(c => Directory.Exists(Path.Combine(c, "appcache")) || Directory.Exists(Path.Combine(c, "steamapps")));
    }

    // ---------- start with the computer ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string LinuxAutostart => Path.Combine(XdgConfig, "autostart", "trophyline.desktop");
    static string LinuxMenuEntry => Path.Combine(XdgData, "applications", "trophyline.desktop");
    static string MacAgent => Path.Combine(HomeDir, "Library", "LaunchAgents", "io.github.cillx111.trophyline.plist");
    static string WindowsShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Trophyline.lnk");
    static string XdgConfig => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(HomeDir, ".config");
    static string XdgData => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(HomeDir, ".local", "share");

    /// Registers the tracker to start at login and adds the app to the Start menu / app menu.
    public static void Install(string exe)
    {
        if (Windows)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(Program.AppName, $"\"{exe}\" --background");
            CreateWindowsShortcut(exe);
        }
        else if (Linux)
        {
            var icon = Path.Combine(Program.Home, "trophyline.png");
            WriteLogo(icon);
            Write(LinuxAutostart,
                "[Desktop Entry]\nType=Application\nName=Trophyline tracker\n" +
                $"Exec=\"{exe}\" --background\nIcon={icon}\nNoDisplay=true\nX-GNOME-Autostart-enabled=true\n");
            Write(LinuxMenuEntry,
                "[Desktop Entry]\nType=Application\nName=Trophyline\nComment=Your Steam achievements, playtime and library\n" +
                $"Exec=\"{exe}\"\nIcon={icon}\nTerminal=false\nCategories=Game;Utility;\n");
        }
        else if (Mac)
        {
            Write(MacAgent,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\"><dict>\n" +
                "  <key>Label</key><string>io.github.cillx111.trophyline</string>\n" +
                $"  <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>--background</string></array>\n" +
                "  <key>RunAtLoad</key><true/>\n" +
                "</dict></plist>\n");
        }
    }

    public static void Uninstall()
    {
        if (Windows)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                key?.DeleteValue(Program.AppName, throwOnMissingValue: false);
            Delete(WindowsShortcut);
        }
        else if (Linux) { Delete(LinuxAutostart); Delete(LinuxMenuEntry); }
        else if (Mac) Delete(MacAgent);
    }

    public static string InstallHint =>
        Windows ? "To pin the window: open Start, type \"Trophyline\", right-click it and choose \"Pin to taskbar\"."
        : Linux ? "Trophyline is now in your app menu."
        : "Keep Trophyline.app in your Applications folder; drag it to the Dock to pin it.";

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    static void WriteLogo(string path)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Trophyline.logo.png");
        if (s == null) return;
        using var f = File.Create(path);
        s.CopyTo(f);
    }

    static void CreateWindowsShortcut(string exe)
    {
        if (!Windows) return;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(WindowsShortcut);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Your Steam achievements, playtime and library";
            link.Save();
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    // ---------- messages & memory ----------

    public static void Message(string text)
    {
        if (Windows) MessageBoxW(IntPtr.Zero, text, Program.AppName, 0x40);
        else Console.WriteLine(text);
    }

    public static void TrimMemory()
    {
        // Aggressive = also hand freed heap memory back to the system, not just mark it free.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (Windows) EmptyWorkingSet(new IntPtr(-1));
    }

    public static void LowPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr process);
}
