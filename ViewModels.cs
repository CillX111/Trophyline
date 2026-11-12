using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Trophyline;

/// Loads pictures once, small and frozen, so the window stays light.
static class Img
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? File(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = path + "|" + decodeWidth;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? img = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = decodeWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
        }
        catch (Exception) { img = null; }
        return Cache[key] = img;
    }

    /// From the web (downloads in the background; shows up when ready).
    public static ImageSource? Url(string url, int decodeWidth)
    {
        var key = url + "|" + decodeWidth;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? img = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(url);
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            img = bmp;
        }
        catch (Exception) { img = null; }
        return Cache[key] = img;
    }
}

static class Fmt
{
    public static string Hours(int minutes) =>
        minutes <= 0 ? "never played"
        : minutes < 60 ? $"{minutes} min"
        : $"{minutes / 60.0:#,0.#} h";

    public static string HoursShort(int minutes) =>
        minutes < 60 ? $"{minutes}m" : $"{minutes / 60.0:#,0.#}h";

    public static string Ago(long unix)
    {
        if (unix <= 0) return "never";
        var when = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 60) return "just now";
        if (when.Date == DateTime.Today) return "today";
        if (when.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        if (span.TotalDays < 7) { int d = Math.Max(2, (int)Math.Round(span.TotalDays)); return $"{d} days ago"; }
        if (span.TotalDays < 30) { int w = (int)(span.TotalDays / 7); return w == 1 ? "a week ago" : $"{w} weeks ago"; }
        if (span.TotalDays < 365) { int m = (int)(span.TotalDays / 30.4); return m <= 1 ? "a month ago" : $"{m} months ago"; }
        int y = (int)(span.TotalDays / 365.25);
        return y <= 1 ? "a year ago" : $"{y} years ago";
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : bytes > 0 ? "< 1 MB" : "";

    public static DateTime Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

    public static readonly Brush Gold = Frozen("#F5C451");
    public static readonly Brush Purple = Frozen("#B98CFF");
    public static readonly Brush Blue = Frozen("#66C0F4");
    public static readonly Brush Grey = Frozen("#8B99AB");
    public static readonly Brush None = Frozen("#00000000");

    public static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Brush Tint(Brush b, byte alpha)
    {
        var c = ((SolidColorBrush)b).Color;
        var t = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        t.Freeze();
        return t;
    }
}

public sealed class AchVM
{
    public AchVM(AchievementInfo a, GameInfo g) { A = a; G = g; }
    public AchievementInfo A { get; }
    public GameInfo G { get; }

    public string Name => A.Name;
    public string Description => A.Hidden && !A.Unlocked && A.Description.Length > 0
        ? "Hidden achievement: keep playing to find out." : A.Description;
    public Visibility DescriptionVisibility => Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Game => G.Name;
    public Visibility GameVisibility { get; set; } = Visibility.Visible;
    public string When => A.Time is long t ? Fmt.Local(t).ToString("dd.MM.yyyy  HH:mm") : A.Unlocked ? "unlocked" : "";
    public string WhenShort => A.Time is long t ? Fmt.Ago(t) : "";
    public string Group { get; set; } = "";
    public double RowOpacity => A.Unlocked ? 1 : 0.62;

    // ----- rarity -----
    public Brush PillBrush => A.Rarity switch
    {
        null => Fmt.Grey,
        < 5 => Fmt.Gold,
        < 15 => Fmt.Purple,
        < 40 => Fmt.Blue,
        _ => Fmt.Grey,
    };
    public Brush PillBackground => Fmt.Tint(PillBrush, 0x2A);
    public string PillText => A.Rarity is double r
        ? (r < 5 ? "ULTRA RARE  " : r < 15 ? "RARE  " : "") + (r < 10 ? $"{r:0.0}%" : $"{r:0}%")
        : "";
    public Visibility PillVisibility => A.Rarity != null ? Visibility.Visible : Visibility.Collapsed;
    public string? RarityTip => A.Rarity is double r ? $"{r:0.0}% of all players have this" : null;
    public Brush IconBorder => A.Unlocked && A.Rarity is < 5 ? Fmt.Gold : Fmt.None;

    ImageSource? icon;
    bool iconLoaded;
    public ImageSource? Icon
    {
        get
        {
            if (iconLoaded) return icon;
            iconLoaded = true;
            if (A.Unlocked)
            {
                var u = new Unlock(A.Time, "", A.AppId, "", "", "", A.Icon);
                icon = Img.File(IconCache.LocalPath(u), 112)
                       ?? (A.Icon.Length > 0 ? Img.Url(IconCache.RemoteUrl(u), 112) : null);
            }
            else if (A.IconGray.Length > 0)
                icon = Img.Url($"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{A.AppId}/{A.IconGray}", 112);
            return icon;
        }
    }

    public string SearchText => searchText ??= $"{G.Name} {A.Name} {A.Description}".ToLowerInvariant();
    string? searchText;
}

public sealed class GameVM
{
    public GameVM(GameInfo g) { G = g; }
    public GameInfo G { get; }

    public string Name => string.IsNullOrEmpty(G.Name) ? $"App {G.AppId}" : G.Name;
    public double Completion => G.Completion;
    public string CountText => $"{G.UnlockedCount} / {G.Total}";
    public string PercentText => $"{G.Completion * 100:0}%";
    public Visibility AchVisibility => G.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PerfectVisibility => G.Perfect ? Visibility.Visible : Visibility.Collapsed;
    public Brush ProgressBrush => G.Perfect ? Fmt.Gold : Fmt.Blue;
    public Brush CardBorder => G.Perfect ? Fmt.Gold : Fmt.None;
    public string CardSub => G.PlaytimeMinutes > 0
        ? $"{Fmt.Hours(G.PlaytimeMinutes)}  ·  {Fmt.Ago(G.LastPlayed)}"
        : G.Installed ? "installed, not played yet" : Fmt.Ago(G.LastPlayed);

    ImageSource? portrait; bool portraitLoaded;
    public ImageSource? Portrait
    {
        get
        {
            if (!portraitLoaded) { portraitLoaded = true; portrait = Img.File(G.Portrait ?? G.Header ?? G.Hero, 260); }
            return portrait;
        }
    }
    public ImageSource? HeaderImage => Img.File(G.Header ?? G.Hero ?? G.Portrait, 300);

    // storage page
    public double SizeFraction { get; set; }
    public string SizeText => Fmt.Size(G.SizeOnDisk);
    public string StorageSub => G.PlaytimeMinutes > 0
        ? $"{Fmt.Hours(G.PlaytimeMinutes)} played  ·  last played {Fmt.Ago(G.LastPlayed)}"
        : G.LastPlayed > 0 ? $"barely played  ·  last opened {Fmt.Ago(G.LastPlayed)}" : "never played";
    bool Stale => G.LastPlayed == 0 || (DateTime.Now - Fmt.Local(G.LastPlayed)).TotalDays > 180;
    public Visibility StaleVisibility => Stale ? Visibility.Visible : Visibility.Collapsed;
    public string StaleText => G.LastPlayed == 0 ? "NEVER PLAYED" : "NOT PLAYED IN 6+ MONTHS";
}
