using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Trophyline;

/// Profile pictures: your own (my-avatar.jpg) and other players' (cached in avatars/).
static class Avatars
{
    public const int Size = 128;

    public static string MinePath => Path.Combine(Program.Home, "my-avatar.jpg");
    static string CacheFolder => Path.Combine(Program.Home, "avatars");

    /// Turns any picture file into a 128 x 128 JPEG (centre crop). Returns null if it can't be read.
    public static byte[]? Prepare(string file)
    {
        using var src = SKBitmap.Decode(file);
        if (src == null) return null;
        int side = Math.Min(src.Width, src.Height);
        using var square = new SKBitmap(side, side);
        if (!src.ExtractSubset(square, SKRectI.Create((src.Width - side) / 2, (src.Height - side) / 2, side, side))) return null;
        using var small = square.Resize(new SKImageInfo(Size, Size), SKFilterQuality.High);
        if (small == null) return null;
        using var img = SKImage.FromBitmap(small);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 86);
        return data?.ToArray();
    }

    public static bool HasMine => File.Exists(MinePath);

    public static void SaveMine(byte[]? jpeg)
    {
        if (jpeg == null) { try { File.Delete(MinePath); } catch (IOException) { } }
        else File.WriteAllBytes(MinePath, jpeg);
        mine = null;
    }

    static Bitmap? mine;
    public static Bitmap? Mine()
    {
        if (mine != null) return mine;
        try { return mine = HasMine ? new Bitmap(MinePath) : null; } catch { return null; }
    }

    // ----- other players' pictures -----

    static readonly Dictionary<string, Task<Bitmap?>> Loading = [];

    /// Someone's picture at a given version: from the cache, or downloaded once.
    public static Task<Bitmap?> Get(string playerId, long ver)
    {
        if ((playerId == "me" || (playerId.Length > 0 && playerId == Online.MyId)) && Mine() is Bitmap me)
            return Task.FromResult<Bitmap?>(me);
        if (ver <= 0 || playerId.Length == 0 || playerId == "me") return Task.FromResult<Bitmap?>(null);
        var key = $"{playerId}_{ver}";
        lock (Loading)
        {
            if (!Loading.TryGetValue(key, out var t)) Loading[key] = t = Load(playerId, ver, key);
            return t;
        }
    }

    static async Task<Bitmap?> Load(string playerId, long ver, string key)
    {
        try
        {
            var file = Path.Combine(CacheFolder, key + ".img");
            if (!File.Exists(file))
            {
                var res = await Online.Avatar(playerId);
                var data = res?["data"]?.GetValue<string>();
                if (string.IsNullOrEmpty(data)) return null;
                Directory.CreateDirectory(CacheFolder);
                foreach (var old in Directory.EnumerateFiles(CacheFolder, playerId + "_*.img")) File.Delete(old);
                await File.WriteAllBytesAsync(file, Convert.FromBase64String(data));
            }
            return new Bitmap(file);
        }
        catch (Exception ex) { Program.LogError("avatar: " + ex.Message); return null; }
    }
}
