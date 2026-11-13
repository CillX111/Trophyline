using Avalonia.Media.Imaging;

namespace Trophyline;

/// Profile pictures are Steam profile pictures: the server copies each player's from Steam at every
/// Steam check. Yours is kept in avatars/mine.img; other players' are cached in avatars/.
static class Avatars
{
    static string CacheFolder => Path.Combine(Program.Home, "avatars");
    public static string MinePath => Path.Combine(CacheFolder, "mine.img");

    public static bool HasMine => File.Exists(MinePath);

    /// Your picture as the server has it (null = none / logged out).
    public static void SaveMine(byte[]? picture)
    {
        try
        {
            if (picture == null || picture.Length == 0) { if (File.Exists(MinePath)) File.Delete(MinePath); }
            else
            {
                if (HasMine && File.ReadAllBytes(MinePath).AsSpan().SequenceEqual(picture)) return; // unchanged
                Directory.CreateDirectory(CacheFolder);
                File.WriteAllBytes(MinePath, picture);
            }
        }
        catch (IOException ex) { Program.LogError("avatar: " + ex.Message); }
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
