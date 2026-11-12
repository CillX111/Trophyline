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

    // ---------- accounts ----------

    /// Logged in on this computer (with an account, or a profile from before accounts existed).
    public static bool LoggedIn => Program.Settings().OnlineId.Length > 0;

    /// A profile from before accounts existed: it still needs a username and password.
    public static bool NeedsLogin => LoggedIn && Program.Settings().OnlineUser.Length == 0;

    public static string Username => Program.Settings().OnlineUser;

    /// Runs tl_register / tl_login / tl_claim and remembers the session. Returns an error text or null.
    static async Task<string?> StartSession(string fn, JsonObject args)
    {
        if (!Available) return "The Trophyline server isn't set up.";
        var (r, err) = await Rpc(fn, args);
        if (err != null) return err;
        if (r?["error"]?.GetValue<string>() is string problem) return problem;
        var id = r?["id"]?.GetValue<string>();
        var secret = r?["secret"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) return "The server gave an unexpected answer.";
        var cfg = Program.Settings();
        bool sameProfile = cfg.OnlineId == id;
        cfg.OnlineId = id;
        cfg.OnlineSecret = secret;
        cfg.OnlineUser = r?["username"]?.GetValue<string>() ?? "";
        cfg.OnlineName = r?["name"]?.GetValue<string>() is { Length: > 0 } n ? n : cfg.OnlineUser;
        cfg.OnlineEnabled = true;
        if (!sameProfile) { cfg.OnlineSteamAccount = ""; cfg.AvatarHash = ""; cfg.VerifiedSteam = ""; cfg.LastVerify = 0; }
        Program.SaveSettings(cfg);
        lastUpload = default;
        return null;
    }

    public static Task<string?> Register(string username, string password) =>
        StartSession("tl_register", new JsonObject { ["p_username"] = username.Trim(), ["p_password"] = password });

    public static Task<string?> Login(string username, string password) =>
        StartSession("tl_login", new JsonObject { ["p_username"] = username.Trim(), ["p_password"] = password });

    /// Gives the profile already on this computer a username and password.
    public static Task<string?> Claim(string username, string password)
    {
        var args = Me();
        args["p_username"] = username.Trim();
        args["p_password"] = password;
        return StartSession("tl_claim", args);
    }

    /// Logs this computer out. The account and its place on the leaderboards stay.
    public static async Task Logout()
    {
        if (Available && LoggedIn) await Rpc("tl_logout", Me());
        var cfg = Program.Settings();
        cfg.OnlineEnabled = false;
        cfg.OnlineId = "";
        cfg.OnlineSecret = "";
        cfg.OnlineUser = "";
        cfg.OnlineSteamAccount = "";
        cfg.AvatarHash = "";
        cfg.VerifiedSteam = "";
        cfg.LastVerify = 0;
        Program.SaveSettings(cfg);
    }

    public static async Task<string?> ChangePassword(string oldPassword, string newPassword)
    {
        var args = Me();
        args["p_old"] = oldPassword;
        args["p_new"] = newPassword;
        var (r, err) = await Rpc("tl_change_password", args);
        return err ?? r?["error"]?.GetValue<string>();
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
        cfg.OnlineUser = "";
        cfg.OnlineSteamAccount = "";
        cfg.AvatarHash = "";
        cfg.VerifiedSteam = "";
        cfg.LastVerify = 0;
        Program.SaveSettings(cfg);
        return null;
    }

    static DateTime lastUpload;

    /// Sends what you choose yourself: display name, equipped icon and profile links. No numbers: your
    /// level, XP, games, best games and latest unlocks are read by the server from Steam (the Steam check),
    /// so nothing on this computer can change them. Returns an error text, or null when it worked (or was skipped).
    public static async Task<string?> Upload(bool force = false)
    {
        if (!Enabled) return null;
        if (!force && DateTime.UtcNow - lastUpload < TimeSpan.FromMinutes(2)) return null;
        lastUpload = DateTime.UtcNow;
        var cfg = Program.Settings();
        var shop = Shop.Load();
        var p = new JsonObject
        {
            ["id"] = cfg.OnlineId,
            ["secret"] = cfg.OnlineSecret,
            ["name"] = cfg.OnlineName,
            ["profile"] = new JsonObject
            {
                ["icon"] = shop.Owned.Contains(shop.Equipped) ? shop.Equipped : "",
                ["links"] = Links(cfg),
            },
        };
        var (_, err) = await Rpc("tl_upsert", new JsonObject { ["p"] = p });
        if (err == null) await AutoVerify();
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

    // ---------- Steam-verified scores (supabase/functions/trophyline) ----------
    // The server asks Steam itself which games and achievements your linked account has. Only those
    // numbers count on the leaderboards, so nothing on this computer can change them.

    static readonly HttpClient SlowHttp = new() { Timeout = TimeSpan.FromSeconds(150) };

    static string FunctionUrl =>
        Environment.GetEnvironmentVariable("TROPHYLINE_FUNCTION") is { Length: > 0 } env ? env : $"{Server}/functions/v1/trophyline";

    static async Task<(JsonNode? Result, string? Error)> CallFunction(JsonObject body)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, FunctionUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("apikey", Key);
            using var res = await SlowHttp.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            JsonNode? node = null;
            try { node = JsonNode.Parse(text); } catch { }
            if (node is JsonObject o && o["error"] is JsonNode e && e.GetValueKind() == System.Text.Json.JsonValueKind.String)
                return (node, e.GetValue<string>());
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, "The Steam check isn't set up on the Trophyline server yet.");
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (null, "The server's Steam check is set up with \"Verify JWT\" on. The server owner needs to turn it off.");
            if (!res.IsSuccessStatusCode) return (null, $"Server said {(int)res.StatusCode}");
            return (node, null);
        }
        catch (Exception ex) { return (null, "Couldn't reach the server: " + ex.Message); }
    }

    public static bool SteamLinked => Program.Settings().VerifiedSteam.Length > 0;

    /// Sign in with Steam, and the server links that account to your Trophyline account. Returns an error or null.
    public static async Task<string?> LinkSteam(CancellationToken cancel)
    {
        if (!LoggedIn || NeedsLogin) return "Log in to your Trophyline account first.";
        var s = await SteamAccounts.SignIn(cancel, forServer: true);
        if (s.Error != null || s.Params == null) return s.Error ?? "The sign-in didn't work.";
        var openid = new JsonObject();
        foreach (var (k, v) in s.Params) openid[k] = v;
        var cfg = Program.Settings();
        var (r, err) = await CallFunction(new JsonObject
        {
            ["action"] = "link", ["id"] = cfg.OnlineId, ["secret"] = cfg.OnlineSecret, ["openid"] = openid,
        });
        if (err != null) return err;
        cfg = Program.Settings();
        cfg.VerifiedSteam = r?["steam_id"]?.GetValue<string>() ?? s.Id64 ?? "";
        cfg.OnlineSteamAccount = cfg.VerifiedSteam; // your profile shows this account's games
        cfg.LastVerify = 0;
        Program.SaveSettings(cfg);
        return null;
    }

    /// Linking with a name code (for networks that block Steam's login page): returns the code to put in
    /// the Steam profile name of this account, or an error.
    public static async Task<(string? Code, string? Error)> StartSteamCode(string steamId64)
    {
        if (!LoggedIn || NeedsLogin) return (null, "Log in to your Trophyline account first.");
        var args = Me();
        args["p_steam_id"] = steamId64;
        var (r, err) = await Rpc("tl_code_begin", args);
        if (err != null) return (null, err);
        if (r?["error"]?.GetValue<string>() is string e) return (null, e);
        return (r?["code"]?.GetValue<string>(), null);
    }

    /// Asks the server to look for the code in the Steam profile name. (true, null) once linked;
    /// (false, null) while the name doesn't have it yet; (false, error) when something's wrong.
    public static async Task<(bool Linked, string? Error)> CheckSteamCode()
    {
        var cfg = Program.Settings();
        var (r, err) = await CallFunction(new JsonObject { ["action"] = "code", ["id"] = cfg.OnlineId, ["secret"] = cfg.OnlineSecret });
        if (err != null) return (false, err);
        if (r?["linked"]?.GetValue<bool>() != true) return (false, null);
        cfg = Program.Settings();
        cfg.VerifiedSteam = r["steam_id"]?.GetValue<string>() ?? "";
        cfg.OnlineSteamAccount = cfg.VerifiedSteam;
        cfg.LastVerify = 0;
        Program.SaveSettings(cfg);
        return (true, null);
    }

    static readonly SemaphoreSlim verifying = new(1, 1);

    /// Has the server check your scores with Steam now. A big library is checked in parts, one call each.
    public static async Task<(JsonNode? Result, string? Error)> Verify(Action<string>? progress = null)
    {
        if (!LoggedIn || NeedsLogin) return (null, "Log in to your Trophyline account first.");
        if (!await verifying.WaitAsync(0)) return (null, "A check is already running.");
        try
        {
            JsonNode? last = null;
            for (int part = 0; part < 40; part++)
            {
                var cfg = Program.Settings();
                var (r, err) = await CallFunction(new JsonObject { ["action"] = "verify", ["id"] = cfg.OnlineId, ["secret"] = cfg.OnlineSecret });
                if (err != null) return (r, err);
                last = r;
                if (r?["partial"]?.GetValue<bool>() != true) break;
                progress?.Invoke($"Checking your games with Steam… {r["left"]} games to go");
            }
            var c = Program.Settings();
            c.LastVerify = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Program.SaveSettings(c);
            return (last, null);
        }
        finally { verifying.Release(); }
    }

    /// Re-checks with Steam every 6 hours, from the tracker or the window.
    public static async Task AutoVerify()
    {
        var cfg = Program.Settings();
        if (!Enabled || NeedsLogin || cfg.VerifiedSteam.Length == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - cfg.LastVerify < 6 * 3600) return;
        cfg.LastVerify = now; // also when it fails, so a broken check isn't retried every few minutes
        Program.SaveSettings(cfg);
        var (_, err) = await Verify();
        if (err != null) Program.LogError("verify: " + err);
    }

    /// Linked account, last check, ranked or paused, and the verified numbers.
    public static async Task<JsonNode?> VerifyStatus()
    {
        if (!LoggedIn || NeedsLogin) return null;
        var r = await Read("tl_verify_status", Me());
        if (r?["linked"] is JsonNode linked)
        {
            var cfg = Program.Settings();
            var sid = linked.GetValue<bool>() ? r["steam_id"]?.GetValue<string>() ?? "" : "";
            if (cfg.VerifiedSteam != sid)
            {
                cfg.VerifiedSteam = sid; // e.g. linked on another computer
                if (sid.Length > 0) cfg.OnlineSteamAccount = sid;
                Program.SaveSettings(cfg);
            }
        }
        return r;
    }

    /// Reports a player to the server owner. Returns an error text or null.
    public static async Task<string?> Report(string who, string reason)
    {
        var args = Me();
        args["p_target"] = who;
        args["p_reason"] = reason;
        var (r, err) = await Rpc("tl_report", args);
        return err ?? r?["error"]?.GetValue<string>();
    }
}
