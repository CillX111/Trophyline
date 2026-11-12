using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Trophyline;

public sealed record SteamAccount(string Id64, string AccountId, string Login, string Persona, bool MostRecent, long LastLogin, bool HasFiles)
{
    public string Display => Persona.Length > 0 ? Persona : Login.Length > 0 ? Login : $"Account {AccountId}";
}

/// Which Steam account Trophyline shows. Three ways to choose (Settings → Steam account):
/// automatic (whoever is signed in to Steam right now), one of the accounts on this PC, or
/// "Sign in with Steam" (Steam's own login page tells Trophyline which account is yours).
/// The numbers always come from that account's Steam files on this computer.
static class SteamAccounts
{
    const ulong Base = 76561197960265728UL;

    public static string ToAccountId(string id64) => ulong.TryParse(id64, out var v) && v > Base ? (v - Base).ToString() : "";
    public static string ToId64(string accountId) => ulong.TryParse(accountId, out var v) ? (v + Base).ToString() : "";

    /// Every account that has signed in to Steam on this computer.
    public static List<SteamAccount> List()
    {
        var list = new List<SteamAccount>();
        var steam = Program.SteamFolder();
        if (steam == null) return list;
        var userData = Path.Combine(steam, "userdata");
        bool Files(string accountId) => accountId.Length > 0 && Directory.Exists(Path.Combine(userData, accountId));
        try
        {
            var users = Vdf.ParseFile(Path.Combine(steam, "config", "loginusers.vdf")).Obj("users");
            foreach (var (id64, v) in users ?? [])
            {
                if (v is not Dictionary<string, object> u) continue;
                var acc = ToAccountId(id64);
                if (acc.Length == 0) continue;
                list.Add(new SteamAccount(id64, acc, u.Str("AccountName") ?? "", u.Str("PersonaName") ?? "",
                                          u.Str("MostRecent") == "1", u.Num("Timestamp") ?? 0, Files(acc)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        // folders Steam keeps for accounts that aren't in the login list any more
        if (Directory.Exists(userData))
            foreach (var dir in Directory.EnumerateDirectories(userData))
            {
                var acc = Path.GetFileName(dir);
                if (acc == "0" || !ulong.TryParse(acc, out _) || list.Any(a => a.AccountId == acc)) continue;
                list.Add(new SteamAccount(ToId64(acc), acc, "", "", false, 0, true));
            }
        return list.OrderByDescending(a => a.MostRecent).ThenByDescending(a => a.LastLogin).ToList();
    }

    /// The account signed in to the running Steam (Windows knows this live; elsewhere the most recent login).
    public static SteamAccount? SignedInNow(List<SteamAccount> all)
    {
        if (Platform.Windows)
        {
            try
            {
                if (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess")?.GetValue("ActiveUser") is int active && active != 0
                    && all.FirstOrDefault(a => a.AccountId == ((uint)active).ToString()) is SteamAccount live)
                    return live;
            }
            catch (Exception) { }
        }
        return all.FirstOrDefault(a => a.MostRecent) ?? all.FirstOrDefault(a => a.LastLogin > 0);
    }

    /// The account to show: the one picked in Settings if it still exists here, otherwise automatic.
    public static SteamAccount? Current()
    {
        var all = List();
        var picked = Program.Settings().SteamAccount;
        if (picked.Length > 0 && all.FirstOrDefault(a => a.Id64 == picked) is SteamAccount chosen) return chosen;
        return SignedInNow(all) ?? all.FirstOrDefault(a => a.HasFiles) ?? all.FirstOrDefault();
    }

    // ---------- Sign in with Steam (OpenID) ----------
    // Steam's own website asks for the login; Trophyline only receives "this is account 7656…" and
    // checks with Steam that the answer is genuine. It never sees a password.

    public sealed record SignInResult(string? Id64, string? Error);

    const string Blocked = "Steam's website (steamcommunity.com) can't be reached from this network, so signing in isn't possible here. "
                         + "Some school and work networks block it. Pick your account from the list instead, or try again on another network.";

    public static async Task<SignInResult> SignIn(CancellationToken cancel)
    {
        // no point opening a login page that can't load
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var res = await probe.GetAsync("https://steamcommunity.com/openid/login", HttpCompletionOption.ResponseHeadersRead, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return new SignInResult(null, "Sign-in cancelled."); }
        catch (Exception) { return new SignInResult(null, Blocked); }

        HttpListener? listener = null;
        string root = "";
        for (int attempt = 0; attempt < 5 && listener == null; attempt++)
        {
            int port = Random.Shared.Next(49200, 65000);
            root = $"http://127.0.0.1:{port}/";
            var l = new HttpListener();
            l.Prefixes.Add(root);
            try { l.Start(); listener = l; } catch (Exception) { l.Close(); }
        }
        if (listener == null) return new SignInResult(null, "Couldn't open a local port for the sign-in.");

        try
        {
            var returnTo = root + "steam";
            var url = "https://steamcommunity.com/openid/login"
                + "?openid.ns=" + Uri.EscapeDataString("http://specs.openid.net/auth/2.0")
                + "&openid.mode=checkid_setup"
                + "&openid.return_to=" + Uri.EscapeDataString(returnTo)
                + "&openid.realm=" + Uri.EscapeDataString(root)
                + "&openid.identity=" + Uri.EscapeDataString("http://specs.openid.net/auth/2.0/identifier_select")
                + "&openid.claimed_id=" + Uri.EscapeDataString("http://specs.openid.net/auth/2.0/identifier_select");
            if (Environment.GetEnvironmentVariable("TROPHYLINE_NO_BROWSER") == "1") // dev check
                File.WriteAllText(Path.Combine(Program.Home, "signin-url.txt"), url);
            else Platform.OpenUrl(url);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var ctxTask = listener.GetContextAsync();
                var done = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, timeout.Token));
                if (done != ctxTask) return new SignInResult(null, cancel.IsCancellationRequested ? "Sign-in cancelled." : "Sign-in timed out. Try again.");
                var ctx = await ctxTask;
                if (ctx.Request.Url?.AbsolutePath != "/steam") { Reply(ctx, 404, "Not found"); continue; }

                var q = ctx.Request.QueryString;
                SignInResult result;
                try { result = await Verify(q, returnTo); }
                catch (Exception) { result = new SignInResult(null, "Couldn't reach Steam to confirm the sign-in."); }
                Reply(ctx, 200, result.Id64 != null
                    ? "<h2>You're signed in.</h2><p>You can close this tab and go back to Trophyline.</p>"
                    : $"<h2>That didn't work.</h2><p>{WebUtility.HtmlEncode(result.Error)}</p><p>Go back to Trophyline and try again.</p>");
                return result;
            }
        }
        catch (Exception ex) { return new SignInResult(null, ex.Message); }
        finally { try { listener.Stop(); listener.Close(); } catch { } }
    }

    static void Reply(HttpListenerContext ctx, int status, string body)
    {
        try
        {
            var html = "<!doctype html><meta charset=utf-8><title>Trophyline</title>"
                     + "<body style=\"font-family:system-ui,sans-serif;background:#0B1016;color:#E9EEF4;text-align:center;padding-top:15vh\">"
                     + body;
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch (Exception) { }
    }

    /// Asks Steam whether the answer the browser brought back is genuine, and reads the account from it.
    static async Task<SignInResult> Verify(System.Collections.Specialized.NameValueCollection q, string returnTo)
    {
        if (q["openid.mode"] != "id_res") return new SignInResult(null, "The sign-in was cancelled.");
        if (q["openid.return_to"] != returnTo) return new SignInResult(null, "The answer wasn't meant for this sign-in.");
        if (q["openid.op_endpoint"] != "https://steamcommunity.com/openid/login") return new SignInResult(null, "The answer didn't come from Steam.");
        var m = Regex.Match(q["openid.claimed_id"] ?? "", @"^https://steamcommunity\.com/openid/id/(\d{17})$");
        if (!m.Success) return new SignInResult(null, "Steam didn't say which account this is.");

        var form = new Dictionary<string, string>();
        foreach (var key in q.AllKeys)
            if (key != null && key.StartsWith("openid.", StringComparison.Ordinal)) form[key] = q[key] ?? "";
        form["openid.mode"] = "check_authentication";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var res = await http.PostAsync("https://steamcommunity.com/openid/login", new FormUrlEncodedContent(form));
        var text = await res.Content.ReadAsStringAsync();
        return text.Contains("is_valid:true")
            ? new SignInResult(m.Groups[1].Value, null)
            : new SignInResult(null, "Steam couldn't confirm the sign-in.");
    }

    /// The public profile name of an account (for accounts this PC has no files for).
    public static async Task<string?> PublicName(string id64)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var xml = await http.GetStringAsync($"https://steamcommunity.com/profiles/{id64}/?xml=1");
            var m = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.Singleline);
            return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
        }
        catch (Exception) { return null; }
    }
}
