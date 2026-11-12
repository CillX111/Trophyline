using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Trophyline;

public sealed record PopupItem(string AppId, string Game, string Name, string Description, string Icon, double? Rarity);

/// Achievement pop-ups. The tracker stays tiny: for each unlock it starts a short-lived
/// "Trophyline --popup" process that shows the card for a few seconds and then exits.
static class Popup
{
    static string Folder => Path.Combine(Program.Home, "popups");

    public static void Show(IEnumerable<Unlock> unlocks)
    {
        var items = unlocks.Select(u =>
        {
            var api = u.Key[(u.Key.LastIndexOf('/') + 1)..];
            var icon = u.Icon.Length > 0 && File.Exists(IconCache.LocalPath(u)) ? IconCache.LocalPath(u) : "";
            return new PopupItem(u.AppId, u.Game, u.Name, u.Description, icon, Rarity.Lookup(u.AppId, api));
        }).ToList();
        Show(items);
    }

    public static void Show(List<PopupItem> items)
    {
        if (items.Count == 0 || Environment.ProcessPath is not string exe) return;
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var old in Directory.EnumerateFiles(Folder, "*.json"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromMinutes(5)) File.Delete(old);
            var file = Path.Combine(Folder, $"{DateTime.UtcNow.Ticks}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(items, PopupJson.Default.ListPopupItem));
            var p = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--popup", file }, UseShellExecute = false });
            // The tracker runs at idle priority; the pop-up should animate smoothly even while a game runs.
            try { if (p != null) p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        }
        catch (Exception ex) { Program.LogError("popup: " + ex.Message); }
    }

    /// If these unlocks took you to a new level, a "level up" card for the top of the pop-up.
    public static PopupItem? LevelUp()
    {
        try
        {
            int seen = Xp.LastSeenLevel();
            if (seen == 0 && !Online.Enabled) return null; // the window sets the starting level the first time it opens
            var data = SteamData.Load(Program.ReadLanguage());
            var r = Xp.Compute(data.Games, SessionLog.Load());
            PendingUpload = Task.Run(() => Online.Upload(data.Games, r, force: true)); // keep the leaderboards current
            if (seen == 0 || r.Level <= seen) return null;
            Xp.MarkSeen(r.Level);
            return new PopupItem(LevelUpId, Xp.RankFor(r.Level).Title, $"Level {r.Level}", $"{r.Total:N0} XP in total", "", null);
        }
        catch (Exception ex) { Program.LogError("popup level: " + ex.Message); return null; }
    }

    public const string LevelUpId = "levelup";

    /// The leaderboard upload started for this pop-up; the pop-up waits for it before exiting.
    public static Task? PendingUpload;

    public static List<PopupItem> Read(string file)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), PopupJson.Default.ListPopupItem) ?? []; }
        catch (Exception ex) { Program.LogError("popup read: " + ex.Message); return []; }
        finally { try { File.Delete(file); } catch { } }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(List<PopupItem>))]
internal partial class PopupJson : System.Text.Json.Serialization.JsonSerializerContext { }

/// The pop-up itself: bottom-right of the screen, on top of everything, never takes focus.
public sealed class PopupWindow : Window
{
    const double CardWidth = 380;
    static readonly TimeSpan Stay = TimeSpan.FromSeconds(6);

    public PopupWindow(List<PopupItem> items)
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Trophyline";

        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        foreach (var it in items.Take(3)) stack.Children.Add(Card(it));
        if (items.Count > 3)
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8), Background = Res("Card"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock { Text = $"+{items.Count - 3} more achievements", Foreground = Res("Muted"), FontSize = 12 },
            });
        Content = stack;

        // Click a card: open Trophyline.
        PointerPressed += (_, _) =>
        {
            try { if (Environment.ProcessPath is string exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); } catch { }
            Close();
        };

        // Slide in from the right and fade in; the same in reverse before closing.
        stack.Opacity = 0;
        stack.RenderTransform = TransformOperations.Parse("translateX(48px)");
        stack.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = new CubicEaseOut() },
        ];

        Opened += async (_, _) =>
        {
            try
            {
                PlaceBottomRight();
                await Task.Delay(50);
                stack.Opacity = 1;
                stack.RenderTransform = TransformOperations.Parse("translateX(0px)");
                await Task.Delay(400);
                if (Environment.GetEnvironmentVariable("TROPHYLINE_POPUP_PNG") is { Length: > 0 } png) // dev check
                {
                    using var rtb = new RenderTargetBitmap(PixelSize.FromSize(Bounds.Size, 1), new Vector(96, 96));
                    rtb.Render(this);
                    rtb.Save(png);
                }
                await Task.Delay(Stay);
                stack.Opacity = 0;
                stack.RenderTransform = TransformOperations.Parse("translateX(48px)");
                await Task.Delay(350);
            }
            catch (Exception ex) { Program.LogError("popup: " + ex); }
            Hide();
            if (Popup.PendingUpload is Task up) await Task.WhenAny(up, Task.Delay(12000));
            Close();
        };
    }

    void PlaceBottomRight()
    {
        var screen = Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(ClientSize, screen.Scaling);
        Position = new PixelPoint(area.Right - size.Width - 8, area.Bottom - size.Height - 8);
    }

    static Control LevelCard(PopupItem it)
    {
        var (_, hex) = Xp.RankFor(int.TryParse(it.Name.Replace("Level ", ""), out var l) ? l : 1);
        var rank = Fmt.Brush(hex);
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "LEVEL UP!", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Fmt.Gold });
        text.Children.Add(new TextBlock { Text = $"{it.Name}  ·  {it.Game}", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Res("Text") });
        text.Children.Add(new TextBlock { Text = it.Description, FontSize = 12, Foreground = Res("Muted") });
        var badge = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Fmt.Tint(rank, 0x30),
            BorderBrush = rank, BorderThickness = new Thickness(3),
            Child = new PathIcon { Data = Geo("IconShield"), Width = 28, Height = 28, Foreground = rank },
        };
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(text);
        return new Border
        {
            Width = CardWidth, Padding = new Thickness(14), CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.Parse("#F2151C25")),
            BorderBrush = Fmt.Gold, BorderThickness = new Thickness(2),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 26, Color = Color.FromArgb(0x70, 0xF5, 0xC4, 0x51) }),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };
    }

    static Control Card(PopupItem it)
    {
        if (it.AppId == Popup.LevelUpId) return LevelCard(it);
        bool rare = it.Rarity is < 10;
        var iconImg = new Image { Stretch = Stretch.UniformToFill };
        try { if (it.Icon.Length > 0 && File.Exists(it.Icon)) iconImg.Source = new Bitmap(it.Icon); } catch { }
        var icon = new Border
        {
            Width = 64, Height = 64, CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Background = Res("Line"), BorderThickness = new Thickness(2),
            BorderBrush = rare ? Fmt.Gold : Palette.Brush,
            Child = iconImg.Source != null ? iconImg : new PathIcon { Data = Geo("IconStar"), Width = 26, Height = 26, Foreground = Palette.Brush },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new PathIcon { Data = Geo("IconStar"), Width = 11, Height = 11, Foreground = rare ? Fmt.Gold : Palette.Brush });
        head.Children.Add(new TextBlock
        {
            Text = rare ? "RARE ACHIEVEMENT UNLOCKED" : "ACHIEVEMENT UNLOCKED",
            FontSize = 10, FontWeight = FontWeight.Bold, Foreground = rare ? Fmt.Gold : Palette.Brush,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var xpPill = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1), Background = Fmt.Tint(rare ? Fmt.Gold : Palette.Brush, 0x30),
            Child = new TextBlock { Text = $"+{Xp.ForAchievement(it.Rarity)} XP", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = rare ? Fmt.Gold : Palette.Brush },
        };
        var headRow = new DockPanel();
        DockPanel.SetDock(xpPill, Dock.Right);
        headRow.Children.Add(xpPill);
        headRow.Children.Add(head);
        text.Children.Add(headRow);
        text.Children.Add(new TextBlock { Text = it.Name, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (it.Description.Length > 0)
            text.Children.Add(new TextBlock { Text = it.Description, FontSize = 12, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis });
        var game = it.Game + (it.Rarity is double r ? $"  ·  {(r < 10 ? r.ToString("0.0") : r.ToString("0"))}% of players" : "");
        text.Children.Add(new TextBlock { Text = game, FontSize = 12, Foreground = rare ? Fmt.Gold : Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        return new Border
        {
            Width = CardWidth,
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.Parse("#F2151C25")),
            BorderBrush = rare ? Fmt.Tint(Fmt.Gold, 0x99) : Fmt.Tint(Palette.Brush, 0x88),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 22, OffsetY = 6, Color = Color.FromArgb(0x90, 0, 0, 0) }),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = row,
        };
    }

    static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var v) && v is IBrush b ? b : Brushes.Gray;

    static Geometry Geo(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var v) && v is Geometry g ? g : new StreamGeometry();
}
