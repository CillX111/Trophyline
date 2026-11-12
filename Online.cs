using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Trophyline;

/// The online leaderboards (server/ in this repo). Off until you switch it on in Settings.
/// What's shared: your display name, level, XP, season XP, game counts, your equipped icon,
/// your top games and latest unlocks. Nothing else, and never your Steam login.
static class Online
{
    /// The public Trophyline server. TROPHYLINE_SERVER overrides it (for testing).
    public const string DefaultServer = "";

    public static string Server =>
        (Environment.GetEnvironmentVariable("TROPHYLINE_SERVER") is { Length: > 0 } env ? env
         : Program.Settings().Server is { Length: > 0 } cfg ? cfg : DefaultServer).TrimEnd('/');

    public static bool Available => Server.Length > 0;
    public static bool Enabled => Available && Program.Settings().OnlineEnabled;
    public static string MyId => Program.Settings().OnlineId;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    static string Random(int length)
    {
        const string abc = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Concat(Enumerable.Range(0, length).Select(_ => abc[RandomNumberGenerator.GetInt32(abc.Length)]));
    }

    /// Turns online on with a display name (creates this computer's id and secret the first time).
    public static void Enable(string name)
    {
        var cfg = Program.Settings();
        if (cfg.OnlineId.Length == 0) cfg.OnlineId = Random(24);
        if (cfg.OnlineSecret.Length == 0) cfg.OnlineSecret = Random(48);
        cfg.OnlineName = name.Trim();
        cfg.OnlineEnabled = true;
        Program.SaveSettings(cfg);
    }

    /// Turns online off and removes you from the leaderboards.
    public static async Task<string?> Disable()
    {
        var cfg = Program.Settings();
        cfg.OnlineEnabled = false;
        Program.SaveSettings(cfg);
        if (!Available || cfg.OnlineId.Length == 0) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Server}/v1/players/{cfg.OnlineId}");
            req.Headers.Add("x-trophyline-secret", cfg.OnlineSecret);
            using var res = await Http.SendAsync(req);
            return res.IsSuccessStatusCode ? null : $"Server said {(int)res.StatusCode}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    static DateTime lastUpload;

    /// Sends your current numbers. Returns an error text, or null when it worked (or was skipped).
    public static async Task<string?> Upload(IEnumerable<GameInfo> gameList, Xp.Result xp, bool force = false)
    {
        if (!Enabled) return null;
        if (!force && DateTime.UtcNow - lastUpload < TimeSpan.FromMinutes(2)) return null;
        lastUpload = DateTime.UtcNow;
        var cfg = Program.Settings();
        var games = gameList.Where(g => g.Total > 0).ToList();
        var season = Season.Current;
        var shop = Shop.Load();

        var top = games.Where(g => g.UnlockedCount > 0)
            .OrderByDescending(g => g.Perfect).ThenByDescending(g => g.Completion).ThenByDescending(g => g.UnlockedCount)
            .Take(12)
            .Select(g => new JsonObject { ["appId"] = g.AppId, ["name"] = g.Name, ["unlocked"] = g.UnlockedCount, ["total"] = g.Total });
        var recent = games.SelectMany(g => g.Achievements.Where(a => a.Unlocked && a.Time != null).Select(a => (g, a)))
            .OrderByDescending(x => x.a.Time).Take(10)
            .Select(x => new JsonObject
            {
                ["appId"] = x.g.AppId, ["game"] = x.g.Name, ["name"] = x.a.Name, ["time"] = x.a.Time,
                ["rarity"] = x.a.Rarity is double r ? Math.Round(r, 1) : null, ["icon"] = x.a.Icon,
            });

        var body = new JsonObject
        {
            ["id"] = cfg.OnlineId,
            ["secret"] = cfg.OnlineSecret,
            ["name"] = cfg.OnlineName,
            ["level"] = xp.Level,
            ["rank"] = Xp.RankFor(xp.Level).Title,
            ["xp"] = xp.Total,
            ["perfect"] = games.Count(g => g.Perfect),
            ["over75"] = games.Count(g => g.Completion >= 0.75),
            ["unlocked"] = games.Sum(g => g.UnlockedCount),
            ["games"] = games.Count(g => g.UnlockedCount > 0),
            ["hours"] = gameList.Sum(g => g.PlaytimeMinutes) / 60,
            ["seasons"] = new JsonArray(
                new JsonObject { ["id"] = season.Id, ["xp"] = xp.SeasonXp(season) },
                new JsonObject { ["id"] = season.Previous.Id, ["xp"] = xp.SeasonXp(season.Previous) }),
            ["profile"] = new JsonObject
            {
                ["icon"] = shop.Owned.Contains(shop.Equipped) ? shop.Equipped : "",
                ["topGames"] = new JsonArray(top.ToArray<JsonNode>()),
                ["recent"] = new JsonArray(recent.ToArray<JsonNode>()),
            },
        };
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.PostAsync($"{Server}/v1/players", content);
            if (res.IsSuccessStatusCode || (int)res.StatusCode == 429) return null;
            var text = await res.Content.ReadAsStringAsync();
            return $"Server said {(int)res.StatusCode}: {text}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static async Task<JsonNode?> Get(string path)
    {
        try
        {
            var text = await Http.GetStringAsync(Server + path);
            return JsonNode.Parse(text);
        }
        catch (Exception ex) { Program.LogError("online get " + path + ": " + ex.Message); return null; }
    }

    public static Task<JsonNode?> Leaderboard(string board, string? season) =>
        Get($"/v1/leaderboard?board={board}" + (season != null ? $"&season={season}" : "") + (MyId.Length > 0 ? $"&me={MyId}" : ""));

    public static Task<JsonNode?> Player(string id) => Get($"/v1/players/{Uri.EscapeDataString(id)}");

    public static Task<JsonNode?> Seasons() => Get("/v1/seasons");

    // ---------- chat ----------

    static async Task<JsonNode?> GetSigned(string path)
    {
        var cfg = Program.Settings();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Server + path);
            req.Headers.Add("x-trophyline-id", cfg.OnlineId);
            req.Headers.Add("x-trophyline-secret", cfg.OnlineSecret);
            using var res = await Http.SendAsync(req);
            return res.IsSuccessStatusCode ? JsonNode.Parse(await res.Content.ReadAsStringAsync()) : null;
        }
        catch (Exception ex) { Program.LogError("chat get: " + ex.Message); return null; }
    }

    static async Task<string?> PostSigned(string path, JsonObject body)
    {
        var cfg = Program.Settings();
        body["id"] = cfg.OnlineId;
        body["secret"] = cfg.OnlineSecret;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.PostAsync(Server + path, content);
            if (res.IsSuccessStatusCode) return null;
            var err = JsonNode.Parse(await res.Content.ReadAsStringAsync())?["error"]?.GetValue<string>();
            return err ?? $"Server said {(int)res.StatusCode}";
        }
        catch (Exception ex) { return "Couldn't reach the server: " + ex.Message; }
    }

    /// Sends a chat message to the lobby (to = null) or to one player. Returns an error text or null.
    public static Task<string?> SendChat(string text, string? to) =>
        PostSigned("/v1/chat", new JsonObject { ["text"] = text, ["to"] = to });

    public static Task<JsonNode?> Lobby(long after) => Get($"/v1/chat/lobby?after={after}");
    public static Task<JsonNode?> Dm(string with, long after) => GetSigned($"/v1/chat/dm?with={with}&after={after}");
    public static Task<JsonNode?> Inbox() => GetSigned("/v1/chat/inbox");
    public static Task<string?> Block(string who, bool block) =>
        PostSigned("/v1/chat/block", new JsonObject { ["who"] = who, ["block"] = block });
}
