using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Trophyline;

/// Loads pictures small and once, so the window stays light. Web pictures are saved to disk the first time.
static class Img
{
    static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Trophyline/1.1");
        return h;
    }

    public static Bitmap? File(string? path, int width)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = path + "|" + width;
        if (Cache.TryGetValue(key, out var hit)) return hit;
        Bitmap? bmp = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                using var s = System.IO.File.OpenRead(path);
                bmp = Bitmap.DecodeToWidth(s, width, BitmapInterpolationMode.HighQuality);
            }
        }
        catch (Exception) { bmp = null; }
        return Cache[key] = bmp;
    }

    /// Local copy if there is one, otherwise downloads it once (in the background) and keeps it.
    public static async Task<Bitmap?> Cached(string url, string localPath, int width)
    {
        if (File(localPath, width) is Bitmap local) return local;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await System.IO.File.WriteAllBytesAsync(localPath, bytes);
            Cache.Remove(localPath + "|" + width);
            return File(localPath, width);
        }
        catch (Exception) { return null; }
    }
}

static class Fmt
{
    public static string Hours(int minutes) =>
        minutes <= 0 ? "never played"
        : minutes < 60 ? $"{minutes} min"
        : $"{minutes / 60.0:#,0.#} h";

    public static string Ago(long unix)
    {
        if (unix <= 0) return "never";
        var when = Local(unix);
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 60) return "just now";
        if (when.Date == DateTime.Today) return "today";
        if (when.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        if (span.TotalDays < 7) return $"{Math.Max(2, (int)Math.Round(span.TotalDays))} days ago";
        if (span.TotalDays < 30) { int w = (int)(span.TotalDays / 7); return w <= 1 ? "a week ago" : $"{w} weeks ago"; }
        if (span.TotalDays < 365) { int m = (int)(span.TotalDays / 30.4); return m <= 1 ? "a month ago" : $"{m} months ago"; }
        int y = (int)(span.TotalDays / 365.25);
        return y <= 1 ? "a year ago" : $"{y} years ago";
    }

    public static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
        : bytes > 0 ? "< 1 MB" : "";

    public static DateTime Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

    public static readonly IBrush Gold = Brush("#F5C451");
    public static readonly IBrush Purple = Brush("#B98CFF");
    public static readonly IBrush Blue = Brush("#66C0F4");
    public static readonly IBrush Grey = Brush("#8B99AB");
    public static readonly IBrush Green = Brush("#5BD68A");
    public static readonly IBrush Orange = Brush("#F59E4B");
    public static readonly IBrush None = Brush("#00000000");

    public static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    public static IBrush Tint(IBrush b, byte alpha)
    {
        var c = ((ISolidColorBrush)b).Color;
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}

public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// A heading inside a list ("November 2026", "Unlocked · 11").
public sealed class HeaderRow(string title)
{
    public string Title { get; } = title.ToUpperInvariant();
}

public sealed class AchVM(AchievementInfo a, GameInfo g) : Notify
{
    public AchievementInfo A { get; } = a;
    public GameInfo G { get; } = g;

    public string Name => A.Name;
    public string Description => A.Hidden && !A.Unlocked && A.Description.Length > 0
        ? "Hidden achievement: keep playing to find out." : A.Description;
    public bool HasDescription => Description.Length > 0;
    public string Game => G.Name;
    public bool ShowGame { get; set; } = true;
    public string When => A.Time is long t ? Fmt.Local(t).ToString("dd.MM.yyyy  HH:mm") : A.Unlocked ? "unlocked" : "";
    public string GameAndWhen => A.Time is long t ? $"{G.Name}  ·  {Fmt.Ago(t)}" : G.Name;
    public string Group { get; set; } = "";
    public double RowOpacity => A.Unlocked ? 1 : 0.62;

    // ----- rarity -----
    public IBrush PillBrush => A.Rarity switch
    {
        null => Fmt.Grey,
        < 5 => Fmt.Gold,
        < 15 => Fmt.Purple,
        < 40 => Fmt.Blue,
        _ => Fmt.Grey,
    };
    public IBrush PillBackground => Fmt.Tint(PillBrush, 0x2A);
    public string PillText => A.Rarity is double r
        ? (r < 5 ? "ULTRA RARE  " : r < 15 ? "RARE  " : "") + (r < 10 ? $"{r:0.0}%" : $"{r:0}%")
        : "";
    public bool HasRarity => A.Rarity != null;
    public string? RarityTip => A.Rarity is double r ? $"{r:0.0}% of all players have this" : null;
    public IBrush IconBorder => A.Unlocked && A.Rarity is < 5 ? Fmt.Gold : Fmt.None;

    // ----- icon (loaded when the row first shows) -----
    Bitmap? icon;
    bool iconStarted;
    public Bitmap? Icon
    {
        get
        {
            if (!iconStarted) { iconStarted = true; _ = LoadIcon(); }
            return icon;
        }
    }

    async Task LoadIcon()
    {
        var file = A.Unlocked ? A.Icon : A.IconGray;
        if (file.Length == 0) return;
        var url = $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{A.AppId}/{file}";
        var local = IconCache.LocalPath(new Unlock(null, "", A.AppId, "", "", "", file));
        var bmp = await Img.Cached(url, local, 112);
        if (bmp == null) return;
        await Dispatcher.UIThread.InvokeAsync(() => { icon = bmp; Raise(nameof(Icon)); });
    }

    public string SearchText => searchText ??= $"{G.Name} {A.Name} {A.Description}".ToLowerInvariant();
    string? searchText;
}

public sealed class PlaylistVM(SavedPlaylist p) : Notify
{
    public SavedPlaylist P { get; } = p;
    public string Title => P.Title;
    public string KindText => P.Kind switch { "album" => "Album", "artist" => "Artist", _ => "Playlist" };

    Bitmap? cover;
    bool started;
    public Bitmap? Cover
    {
        get
        {
            if (!started && P.Cover.Length > 0) { started = true; _ = Load(); }
            return cover;
        }
    }

    async Task Load()
    {
        var bmp = await Img.Cached(P.Cover, PlaylistStore.CoverPath(P), 360);
        if (bmp != null) await Dispatcher.UIThread.InvokeAsync(() => { cover = bmp; Raise(nameof(Cover)); });
    }
}

public sealed class GameVM(GameInfo g)
{
    public GameInfo G { get; } = g;

    public string Name => string.IsNullOrEmpty(G.Name) ? $"App {G.AppId}" : G.Name;
    public double Completion => G.Completion;
    public string CountText => $"{G.UnlockedCount} / {G.Total}";
    public string PercentText => $"{G.Completion * 100:0}%";
    public bool HasAchievements => G.Total > 0;
    public bool Perfect => G.Perfect;
    public IBrush ProgressBrush => G.Perfect ? Fmt.Gold : Palette.Brush;
    public IBrush CardBorder => G.Perfect ? Fmt.Gold : Fmt.None;
    public string CardSub => G.PlaytimeMinutes > 0
        ? $"{Fmt.Hours(G.PlaytimeMinutes)}  ·  {Fmt.Ago(G.LastPlayed)}"
        : G.Installed ? "installed, not played yet" : Fmt.Ago(G.LastPlayed);

    public Bitmap? Portrait => Img.File(G.Portrait ?? G.Header ?? G.Hero, 260);
    public Bitmap? HeaderImage => Img.File(G.Header ?? G.Hero ?? G.Portrait, 300);

    // storage page
    public double SizeFraction { get; set; }
    public string SizeText => Fmt.Size(G.SizeOnDisk);
    public string StorageSub => G.PlaytimeMinutes > 0
        ? $"{Fmt.Hours(G.PlaytimeMinutes)} played  ·  last played {Fmt.Ago(G.LastPlayed)}"
        : G.LastPlayed > 0 ? $"barely played  ·  last opened {Fmt.Ago(G.LastPlayed)}" : "never played";
    public bool Stale => G.LastPlayed == 0 || (DateTime.Now - Fmt.Local(G.LastPlayed)).TotalDays > 180;
    public string StaleText => G.LastPlayed == 0 ? "NEVER PLAYED" : "NOT PLAYED IN 6+ MONTHS";
}

/// One row in "Recent sessions".
public sealed class SessionVM(Session s, GameVM? game)
{
    public Session S { get; } = s;
    public GameVM? Game { get; } = game;
    public string Name => Game?.Name ?? $"App {S.AppId}";
    public Bitmap? Header => Game?.HeaderImage;
    public string Duration => Fmt.Hours(Math.Max(1, S.Minutes));
    public string When
    {
        get
        {
            var a = Fmt.Local(S.Start); var b = Fmt.Local(S.End);
            var day = a.Date == DateTime.Today ? "Today" : a.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : a.ToString("ddd dd.MM.yyyy");
            return $"{day}  ·  {a:HH:mm} – {b:HH:mm}";
        }
    }
}