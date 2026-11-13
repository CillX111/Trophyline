using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Trophyline;

/// Cover art for a song (by title and artist). Spotify's desktop app doesn't share covers, so this asks
/// Apple's free iTunes search (no account; only the song title and artist are sent) and keeps the picture
/// in music/art/ so each song is looked up once. Also works out the cover's main colour for the Music page.
static class SongArt
{
    public sealed record Art(Bitmap Picture, Color Tint);

    static string Folder => Path.Combine(Program.Home, "music", "art");
    static readonly ConcurrentDictionary<string, Task<Art?>> Cache = new();
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Trophyline/1.8");
        return h;
    }

    public static Task<Art?> For(string title, string artist)
    {
        title = title.Trim();
        artist = artist.Trim();
        if (title.Length == 0) return Task.FromResult<Art?>(null);
        var key = (artist + "|" + title).ToLowerInvariant();
        return Cache.GetOrAdd(key, _ => Load(key, title, artist));
    }

    static async Task<Art?> Load(string key, string title, string artist)
    {
        try
        {
            var file = Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20] + ".jpg");
            var none = file + ".none"; // looked up before and nothing was found (tried again after a week)
            if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < TimeSpan.FromDays(7)) return null;
            if (!File.Exists(file))
            {
                var url = await Lookup(title, artist);
                Directory.CreateDirectory(Folder);
                if (url == null) { await File.WriteAllTextAsync(none, ""); return null; }
                var bytes = await Http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(file, bytes);
            }
            return await Task.Run(() =>
            {
                using var s = File.OpenRead(file);
                var bmp = Bitmap.DecodeToWidth(s, 400, BitmapInterpolationMode.HighQuality);
                return new Art(bmp, MainColour(file));
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return null; // offline or a hiccup: the page falls back to the playlist cover
        }
        catch (Exception ex)
        {
            Program.LogError("song art: " + ex.Message);
            return null;
        }
    }

    /// The best match's cover URL (600 x 600), or null.
    static async Task<string?> Lookup(string title, string artist)
    {
        // "Song (feat. X) - 2001 Remix" → "Song", so the search finds it
        var clean = title.Split(" (feat.")[0].Split(" - ")[0].Trim();
        var firstArtist = artist.Split(',')[0].Trim();
        var term = Uri.EscapeDataString($"{firstArtist} {clean}".Trim());
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=5"));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
        string? best = null;
        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("artworkUrl100", out var a) || a.GetString() is not string art) continue;
            var name = r.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
            var who = r.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
            bool sameSong = name.StartsWith(clean, StringComparison.OrdinalIgnoreCase) || clean.StartsWith(name, StringComparison.OrdinalIgnoreCase);
            bool sameArtist = firstArtist.Length == 0 || who.Contains(firstArtist, StringComparison.OrdinalIgnoreCase)
                              || firstArtist.Contains(who, StringComparison.OrdinalIgnoreCase);
            var big = art.Replace("100x100bb", "600x600bb");
            if (sameSong && sameArtist) return big;
            best ??= sameArtist ? big : null;
        }
        return best;
    }

    /// A lively version of the picture's main colour (dull and very dark pixels count less).
    static Color MainColour(string file)
    {
        using var src = SKBitmap.Decode(file);
        if (src == null) return Color.FromRgb(0x1D, 0xB9, 0x54);
        using var small = src.Resize(new SKImageInfo(24, 24), SKFilterQuality.Low) ?? src;
        double r = 0, g = 0, b = 0, w = 0;
        for (int y = 0; y < small.Height; y++)
            for (int x = 0; x < small.Width; x++)
            {
                var c = small.GetPixel(x, y);
                double max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                double sat = max == 0 ? 0 : (max - min) / max, light = max / 255.0;
                double weight = 0.05 + sat * sat * light;
                r += c.Red * weight; g += c.Green * weight; b += c.Blue * weight; w += weight;
            }
        var col = new SKColor((byte)(r / w), (byte)(g / w), (byte)(b / w));
        col.ToHsl(out var hue, out var s, out var l);
        var lively = SKColor.FromHsl(hue, Math.Clamp(s * 1.25f, 35, 85), Math.Clamp(l, 38, 55));
        return Color.FromRgb(lively.Red, lively.Green, lively.Blue);
    }
}
