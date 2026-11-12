using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Trophyline;

/// Online leaderboards, profiles and chat, on a Supabase project (supabase/trophyline.sql).
/// Off until you switch it on. What's shared: your display name, level, XP, season XP, game counts,
/// equipped icon, top games, latest unlocks and the chat messages you write. Never your Steam login.
static class Online
{
    /// The public Trophyline project. The key is Supabase's "publishable" key, which is made to ship
    /// inside apps: on its own it can only call the tl_* functions, not touch the tables.
    public const string DefaultServer = "https://cucjgtxcwjddlgisjmew.supabase.co";
    public const string DefaultKey = "sb_publishable_HfRFJj2P1YzL9GL8-DhlRQ_CuEVGbWM";

    /// TROPHYLINE_SERVER / TROPHYLINE_KEY override these (for testing), then config.json, then the defaults.
    public static string Server =>
        (Environment.GetEnvironmentVariable("TROPHYLINE_SERVER") is { Length: > 0 } env ? env
         : Program.Settings().Server is { Length: > 0 } cfg ? cfg : DefaultServer).TrimEnd('/');

    static string Key =>
        Environment.GetEnvironmentVariable("TROPHYLINE_KEY") is { Length: > 0 } env ? env
        : Program.Settings().ServerKey is { Length: > 0 } cfg ? cfg : DefaultKey;

    public static bool Available => Server.Length > 0 && Key.Length > 0;
    public static bool Enabled => Available && Program.Settings().OnlineEnabled;
    public static string MyId => Program.Settings().OnlineId;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    // ---------- calling the database functions ----------

    /// Calls one tl_* function. Returns (result, null) or (null, error text).
    static async Task<(JsonNode? Result, string? Error)> Rpc(string fn, JsonObject args)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Server}/rest/v1/rpc/{fn}")
            {
                Content = new StringContent(args.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("apikey", Key);
            req.Headers.Add("Authorization", "Bearer " + Key);
            using var res = await Http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode) return (JsonNode.Parse(text), null);
            string? msg = null;
            try { msg = JsonNode.Parse(text)?["message"]?.GetValue<string>(); } catch { }
            return (null, msg ?? $"Server said {(int)res.StatusCode}");
        }
        catch (Exception ex) { return (null, "Couldn't reach the server: " + ex.Message); }
    }

    static async Task<JsonNode?> Read(string fn, JsonObject args)
    {
        var (r, err) = await Rpc(fn, args);
        if (err != null) Program.LogError($"online {fn}: {err}");
        return r;
    }

    static JsonObject Me()
    {
        var cfg = Program.Settings();
        return new JsonObject { ["p_id"] = cfg.OnlineId, ["p_secret"] = cfg.OnlineSecret };
    }

    // ---------- joining ----------

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
        if (cfg.OnlineSteamAccount.Length == 0) cfg.OnlineSteamAccount = SteamAccounts.Current()?.Id64 ?? "";
        Program.SaveSettings(cfg);
    }

    /// Turns online off and deletes your entry (and your chat messages) from the server.
    public static async Task<string?> Disable()
    {
        var cfg = Program.Settings();
        cfg.OnlineEnabled = false;
        Program.SaveSettings(cfg);
        if (!Available || cfg.OnlineId.Length == 0) return null;
        return (await Rpc("tl_delete", Me())).Error;
    }

    /// Deletes your account from the server (profile, scores, picture, messages) and forgets this
    /// computer's id and secret, so joining again starts a new player. Returns an error text or null.
    public static async Task<string?> DeleteAccount()
    {
        var cfg = Program.Settings();
        if (cfg.OnlineId.Length == 0) return null;
        if (Available)
        {
            var (_, err) = await Rpc("tl_delete", Me());
            if (err != null) return err;
        }
        cfg = Program.Settings();
        cfg.OnlineEnabled = false;
        cfg.OnlineId = "";
        cfg.OnlineSecret = "";
        cfg.OnlineSteamAccount = "";
        cfg.AvatarHash = "";
        Program.SaveSettings(cfg);
        return null;
    }

    static DateTime lastUpload;

    /// Sends your current numbers. Returns an error text, or null when it worked (or was skipped).
    public static async Task<string?> Upload(IEnumerable<GameInfo> gameList, Xp.Result xp, bool force = false)
    {
        if (!Enabled) return null;
        if (!force && DateTime.UtcNow - lastUpload < TimeSpan.FromMinutes(2)) return null;
        var cfg = Program.Settings();
        // one online profile belongs to one Steam account: another account's numbers never overwrite it
        var steamId = SteamAccounts.Current()?.Id64 ?? "";
        if (steamId.Length > 0 && cfg.OnlineSteamAccount.Length == 0) { cfg.OnlineSteamAccount = steamId; Program.SaveSettings(cfg); }
        else if (steamId.Length > 0 && cfg.OnlineSteamAccount != steamId)
            return "Your online profile belongs to a different Steam account, so nothing was uploaded. Switch back to that account, or delete the online account (bottom of Settings) and join again with this one.";
        lastUpload = DateTime.UtcNow;
        var all = gameList.ToList();
        var games = all.Where(g => g.Total > 0).ToList();
        var season = Season.Current;
        var shop = Shop.Load();

        var top = games.Where(g => g.UnlockedCount > 0)
            .OrderByDescending(g => g.Perfect).ThenByDescending(g => g.Completion).ThenByDescending(g => g.UnlockedCount)
            .Take(12)
            .Select(g => (JsonNode)new JsonObject { ["appId"] = g.AppId, ["name"] = g.Name, ["unlocked"] = g.UnlockedCount, ["total"] = g.Total });
        var recent = games.SelectMany(g => g.Achievements.Where(a => a.Unlocked && a.Time != null).Select(a => (g, a)))
            .OrderByDescending(x => x.a.Time).Take(10)
            .Select(x => (JsonNode)new JsonObject
            {
                ["appId"] = x.g.AppId, ["game"] = x.g.Name, ["name"] = x.a.Name, ["time"] = x.a.Time,
                ["rarity"] = x.a.Rarity is double r ? Math.Round(r, 1) : null, ["icon"] = x.a.Icon,
            });

        var p = new JsonObject
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
            ["hours"] = all.Sum(g => g.PlaytimeMinutes) / 60,
            ["seasons"] = new JsonArray(
                new JsonObject { ["id"] = season.Id, ["xp"] = xp.SeasonXp(season) },
                new JsonObject { ["id"] = season.Previous.Id, ["xp"] = xp.SeasonXp(season.Previous) }),
            ["profile"] = new JsonObject
            {
                ["icon"] = shop.Owned.Contains(shop.Equipped) ? shop.Equipped : "",
                ["links"] = Links(cfg),
                ["topGames"] = new JsonArray(top.ToArray()),
                ["recent"] = new JsonArray(recent.ToArray()),
            },
        };
        var (_, err) = await Rpc("tl_upsert", new JsonObject { ["p"] = p });
        return err == "too often" ? null : err;
    }

    static JsonObject Links(Config cfg)
    {
        var o = new JsonObject();
        foreach (var n in Social.Networks)
            if (cfg.Links.TryGetValue(n.Key, out var h) && Social.Clean(n, h) is string clean) o[n.Key] = clean;
        return o;
    }

    // ---------- reading ----------

    public static Task<JsonNode?> Leaderboard(string board, string? season) =>
        Read("tl_leaderboard", new JsonObject { ["p_board"] = board, ["p_season"] = season, ["p_me"] = MyId.Length > 0 ? MyId : null });

    public static Task<JsonNode?> Player(string id) => Read("tl_player", new JsonObject { ["p_id"] = id });

    public static Task<JsonNode?> Seasons() => Read("tl_seasons", []);

    // ---------- profile pictures ----------

    /// Uploads your picture (null removes it). Returns an error text or null.
    public static async Task<string?> SetAvatar(byte[]? jpeg)
    {
        if (!Enabled) return null;
        var args = Me();
        args["p_data"] = jpeg == null ? "" : Convert.ToBase64String(jpeg);
        return (await Rpc("tl_set_avatar", args)).Error;
    }

    public static Task<JsonNode?> Avatar(string id) => Read("tl_avatar", new JsonObject { ["p_id"] = id });

    // ---------- chat ----------

    /// Sends a chat message to the lobby (to = null) or to one player. Returns an error text or null.
    public static async Task<string?> SendChat(string text, string? to)
    {
        var cfg = Program.Settings();
        var p = new JsonObject { ["id"] = cfg.OnlineId, ["secret"] = cfg.OnlineSecret, ["text"] = text, ["to"] = to };
        return (await Rpc("tl_chat_send", new JsonObject { ["p"] = p })).Error;
    }

    public static Task<JsonNode?> Lobby(long after)
    {
        var args = Enabled ? Me() : [];
        args["p_after"] = after;
        return Read("tl_chat_lobby", args);
    }

    public static Task<JsonNode?> Dm(string with, long after)
    {
        var args = Me();
        args["p_with"] = with;
        args["p_after"] = after;
        return Read("tl_chat_dm", args);
    }

    public static Task<JsonNode?> Inbox() => Read("tl_chat_inbox", Me());

    public static async Task<string?> Block(string who, bool block)
    {
        var args = Me();
        args["p_who"] = who;
        args["p_block"] = block;
        return (await Rpc("tl_chat_block", args)).Error;
    }
}
