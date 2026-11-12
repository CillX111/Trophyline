namespace Trophyline;

/// Keeps a local copy of each achievement icon so the journal works offline and in any viewer.
/// Icons are downloaded once, only when a new achievement needs one.
static class IconCache
{
    const string BaseUrl = "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps";

    static string Folder => Path.Combine(Program.Home, "icons");

    public static string RemoteUrl(Unlock u) => $"{BaseUrl}/{u.AppId}/{u.Icon}";

    public static string LocalPath(Unlock u) =>
        Path.Combine(Folder, $"{Safe(u.AppId)}_{Safe(u.Icon)}");

    /// Downloads any icons that aren't cached yet. Failures are skipped and retried next time.
    public static void EnsureCached(IEnumerable<Unlock> unlocks)
    {
        var missing = unlocks.Where(u => u.Icon.Length > 0)
                             .DistinctBy(LocalPath)
                             .Where(u => !File.Exists(LocalPath(u)))
                             .ToList();
        if (missing.Count == 0) return;

        Directory.CreateDirectory(Folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Trophyline/1.0");

        Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (u, ct) =>
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(RemoteUrl(u), ct);
                if (bytes.Length == 0) return;
                var path = LocalPath(u);
                await File.WriteAllBytesAsync(path + ".tmp", bytes, ct);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // offline or not found: the page falls back to the web link
            }
        }).GetAwaiter().GetResult();
    }

    /// The icon embedded in the page, or null if it isn't cached.
    public static string? DataUri(Unlock u)
    {
        var path = LocalPath(u);
        if (u.Icon.Length == 0 || !File.Exists(path)) return null;
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            _ => "image/jpeg",
        };
        return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }

    static string Safe(string s) => string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
}
