using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Trophyline;

public partial class App : Application
{
    public static string[] Args { get; set; } = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// Another start of Trophyline asked to see the window: show the one that's already open.
    public static void BringToFront()
    {
        if ((Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not Window w) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.LogError("ui: " + e.Exception);
                e.Handled = true;
            };
            Palette.Apply(Program.Settings().Accent);
            if (Args.Length > 1 && Args[0] == "--popup")
            {
                var items = Popup.Read(Args[1]);
                if (items.Count == 0) { desktop.Shutdown(); base.OnFrameworkInitializationCompleted(); return; }
                if (items.All(i => i.AppId != Popup.LevelUpId) && Popup.LevelUp() is PopupItem up) items.Insert(0, up);
                desktop.MainWindow = new PopupWindow(items);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var window = new MainWindow();
            if (Args.Length > 0 && Args[0] == "--snapshot") PrepareSnapshot(desktop, window);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// Dev check: --snapshot out.png [page] [arg] [height]  renders the window to a PNG off-screen.
    void PrepareSnapshot(IClassicDesktopStyleApplicationLifetime desktop, MainWindow w)
    {
        var a = Args;
        double height = a.Length > 4 && double.TryParse(a[4], out var h) ? h : 1000;
        w.WindowState = WindowState.Normal;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.IsHitTestVisible = false; // a stray click must not change the page being captured
        w.Position = new PixelPoint(-20000, -20000);
        w.MinHeight = 0;
        w.Width = 1280;
        w.Height = height;
        w.Opened += async (_, _) =>
        {
            try
            {
                await w.Ready;
                w.NavigateForSnapshot(a.Length > 2 ? a[2] : "overview", a.Length > 3 && a[3].Length > 0 ? a[3] : null);
                // let animations and web icons settle (TROPHYLINE_SNAPSHOT_WAIT=seconds for slower dev checks)
                await Task.Delay(int.TryParse(Environment.GetEnvironmentVariable("TROPHYLINE_SNAPSHOT_WAIT"), out var wait) ? wait * 1000 : 3500);
                var size = new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height);
                using (var rtb = new RenderTargetBitmap(size, new Vector(96, 96)))
                {
                    rtb.Render(w);
                    rtb.Save(a.Length > 1 ? a[1] : Path.Combine(Program.Home, "snapshot.png"));
                }
                File.WriteAllText(Path.Combine(Program.Home, "snapshot.txt"), w.DescribeView());
            }
            catch (Exception ex) { Program.LogError("snapshot: " + ex); }
            desktop.Shutdown();
        };
    }
}
