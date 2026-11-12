using System.Text.Json;

namespace Trophyline;

public sealed class AchievementInfo
{
    public required string AppId { get; init; }
    public required string ApiName { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Hidden { get; init; }
    public string Icon { get; init; } = "";
    public string IconGray { get; init; } = "";
    public bool Unlocked { get; init; }
    public long? Time { get; init; }
    /// Percent of all Steam players who have it (null until known).
    public double? Rarity { get; set; }
}

public sealed class GameInfo
{
    public required string AppId { get; init; }
    public string Name { get; set; } = "";
    public bool Installed { get; set; }
    public long SizeOnDisk { get; set; }
    public long LastPlayed { get; set; }
    public int PlaytimeMinutes { get; set; }
    public int Playtime2WeeksMinutes { get; set; }
    public string? Portrait { get; set; }
    public string? Header { get; set; }
    public string? Hero { get; set; }
    public string? Logo { get; set; }
    public List<AchievementInfo> Achievements { get; } = [];

    public int Total => Achievements.Count;
    public int UnlockedCount => Achievements.Count(a => a.Unlocked);
    public double Completion => Total == 0 ? 0 : (double)UnlockedCount / Total;
    public bool Perfect => Total > 0 && UnlockedCount == Total;
}

/// Everything the window shows, read from Steam's own files on this PC.
public sealed class SteamData
{
    public string? PersonaName { get; private set; }
    public List<GameInfo> Games { get; } = [];

    // Steam tools, not games.
    static readonly HashSet<string> NotGames = ["7", "228980", "1070560", "1391110", "1493710", "1628350", "1826330", "2180100"];

    public static SteamData Load(string language)
    {
        var data = new SteamData();
        var steam = Program.SteamFolder() ?? throw new DirectoryNotFoundException("Steam isn't installed (or was never started).");
        var games = new Dictionary<string, GameInfo>();
        GameInfo Get(string id) => games.TryGetValue(id, out var g) ? g : games[id] = new GameInfo { AppId = id };

        // 1. Who is logged in
        string? accountId = null;
        try
        {
            var users = Vdf.ParseFile(Path.Combine(steam, "config", "loginusers.vdf")).Obj("users");
            if (users != null)
            {
                var (id64, user) = users.Where(kv => kv.Value is Dictionary<string, object>)
                    .Select(kv => (kv.Key, (Dictionary<string, object>)kv.Value))
                    .OrderByDescending(u => u.Item2.Str("MostRecent") == "1")
                    .ThenByDescending(u => u.Item2.Num("Timestamp") ?? 0)
                    .FirstOrDefault();
                if (user != null)
                {
                    data.PersonaName = user.Str("PersonaName");
                    if (ulong.TryParse(id64, out var v)) accountId = (v - 76561197960265728UL).ToString();
                }
            }
        }
        catch (IOException) { }
        var userData = Path.Combine(steam, "userdata");
        if (accountId == null || !Directory.Exists(Path.Combine(userData, accountId)))
            accountId = Directory.Exists(userData)
                ? Directory.EnumerateDirectories(userData).Select(Path.GetFileName).FirstOrDefault(n => n != "0")
                : null;

        // 2. Playtime and last played
        if (accountId != null)
        {
            var lc = Path.Combine(userData, accountId, "config", "localconfig.vdf");
            if (File.Exists(lc))
            {
                var apps = Vdf.Path(Vdf.ParseFile(lc), "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                foreach (var (id, v) in apps ?? [])
                {
                    if (v is not Dictionary<string, object> app) continue;
                    var played = app.Num("LastPlayed") ?? 0;
                    var minutes = app.Num("Playtime") ?? 0;
                    if (played == 0 && minutes == 0) continue;
                    var g = Get(id);
                    g.LastPlayed = Math.Max(g.LastPlayed, played);
                    g.PlaytimeMinutes = (int)minutes;
                    g.Playtime2WeeksMinutes = (int)(app.Num("Playtime2wks") ?? 0);
                }
            }
        }

        // 3. Installed games, in every Steam library folder
        foreach (var lib in LibraryFolders(steam))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var st = Vdf.ParseFile(acf).Obj("AppState");
                    var id = st.Str("appid");
                    if (id == null) continue;
                    var g = Get(id);
                    g.Installed = true;
                    g.Name = st.Str("name") ?? g.Name;
                    g.SizeOnDisk = st.Num("SizeOnDisk") ?? 0;
                    g.LastPlayed = Math.Max(g.LastPlayed, st.Num("LastPlayed") ?? 0);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { }
            }
        }

        // 4. Achievements (all of them, unlocked or not)
        var stats = Path.Combine(steam, "appcache", "stats");
        if (Directory.Exists(stats))
        {
            foreach (var schemaFile in Directory.EnumerateFiles(stats, "UserGameStatsSchema_*.bin"))
            {
                var id = Path.GetFileNameWithoutExtension(schemaFile)["UserGameStatsSchema_".Length..];
                var statsFile = accountId != null ? Path.Combine(stats, $"UserGameStats_{accountId}_{id}.bin") : null;
                if (!games.ContainsKey(id) && (statsFile == null || !File.Exists(statsFile))) continue; // not yours
                try
                {
                    var (gameName, list) = Steam.ReadAchievements(schemaFile, statsFile, language);
                    if (list.Count == 0 && !games.ContainsKey(id)) continue;
                    var g = Get(id);
                    if (string.IsNullOrEmpty(g.Name) && gameName != null) g.Name = gameName;
                    g.Achievements.AddRange(list);
                }
                catch (Exception ex) { Program.LogError($"schema {id}: {ex.Message}"); }
            }
        }

        // 5. Artwork Steam already downloaded
        var art = Path.Combine(steam, "appcache", "librarycache");
        foreach (var g in games.Values)
        {
            var dir = Path.Combine(art, g.AppId);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                if (name.StartsWith("library_600x900")) g.Portrait ??= f;
                else if (name == "library_hero.jpg" || name == "library_hero.png") g.Hero ??= f;
                else if (name.StartsWith("logo")) g.Logo ??= f;
                else if (name.StartsWith("library_header") || name.StartsWith("header")) g.Header ??= f;
            }
        }

        // 6. Names we still don't know: cached store names
        var names = NameCache.Load();
        foreach (var g in games.Values)
            if (string.IsNullOrEmpty(g.Name) && names.TryGetValue(g.AppId, out var n)) g.Name = n;

        data.Games.AddRange(games.Values.Where(g => !NotGames.Contains(g.AppId)
            && !g.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
            && !g.Name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
            && !g.Name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)));

        Rarity.Apply(data.Games);
        return data;
    }

    static IEnumerable<string> LibraryFolders(string steam)
    {
        var result = new List<string> { steam };
        try
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (var (_, v) in Vdf.ParseFile(vdf).Obj("libraryfolders") ?? [])
                    if (v is Dictionary<string, object> lib && lib.Str("path") is string p) result.Add(p);
        }
        catch (IOException) { }
        return result.Select(p => Path.GetFullPath(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

/// Names of games that aren't installed and have no achievement file, from the Steam store (cached).
static class NameCache
{
    static string FilePath => Path.Combine(Program.Home, "names.json");

    public static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }
        catch (Exception) { return []; }
    }

    /// Looks up missing names online. Returns true if anything new was found.
    public static async Task<bool> FetchMissing(IEnumerable<GameInfo> games, HttpClient http)
    {
        var missing = games.Where(g => string.IsNullOrEmpty(g.Name)).ToList();
        if (missing.Count == 0) return false;
        var names = Load();
        bool found = false;
        foreach (var g in missing)
        {
            try
            {
                var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={g.AppId}&filters=basic");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(g.AppId, out var app) && app.TryGetProperty("data", out var d)
                    && d.TryGetProperty("name", out var n) && n.GetString() is string name)
                {
                    g.Name = name;
                    names[g.AppId] = name;
                    found = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        if (found) Save(names);
        return found;
    }

    static void Save(Dictionary<string, string> names)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (k, v) in names) w.WriteString(k, v);
            w.WriteEndObject();
        }
        File.WriteAllBytes(FilePath, ms.ToArray());
    }
}

/// Global achievement percentages from Steam's public API (no account needed), cached for a week.
static class Rarity
{
    static string Folder => Path.Combine(Program.Home, "rarity");
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static void Apply(IEnumerable<GameInfo> games)
    {
        foreach (var g in games)
        {
            if (g.Total == 0) continue;
            var map = Read(g.AppId);
            if (map == null) continue;
            foreach (var a in g.Achievements)
                if (map.TryGetValue(a.ApiName, out var p)) a.Rarity = p;
        }
    }

    /// Downloads percentages that are missing or older than a week. Returns true if anything changed.
    public static async Task<bool> Refresh(IEnumerable<GameInfo> games, HttpClient http)
    {
        Directory.CreateDirectory(Folder);
        var stale = games.Where(g => g.Total > 0).Where(g =>
        {
            var f = FileFor(g.AppId);
            return !File.Exists(f) || DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > MaxAge;
        }).ToList();
        if (stale.Count == 0) return false;

        int updated = 0;
        await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (g, ct) =>
        {
            try
            {
                var json = await http.GetStringAsync(
                    $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={g.AppId}", ct);
                await File.WriteAllTextAsync(FileFor(g.AppId), json, ct);
                Interlocked.Increment(ref updated);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { }
        });
        if (updated > 0) Apply(stale);
        return updated > 0;
    }

    static string FileFor(string appId) => Path.Combine(Folder, appId + ".json");

    static Dictionary<string, double>? Read(string appId)
    {
        try
        {
            var f = FileFor(appId);
            if (!File.Exists(f)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("achievementpercentages", out var ap) && ap.TryGetProperty("achievements", out var list))
                foreach (var a in list.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var pe = a.GetProperty("percent");
                    double p = pe.ValueKind == JsonValueKind.Number ? pe.GetDouble()
                             : double.TryParse(pe.GetString(), System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : double.NaN;
                    if (name != null && !double.IsNaN(p)) map[name] = p;
                }
            return map;
        }
        catch (Exception) { return null; }
    }
}
