using System.Text.RegularExpressions;

namespace Trophyline;

/// Links people can put on their online profile. Only usernames are stored; the app builds
/// the web address itself, so a profile can never point somewhere unexpected.
public sealed record SocialNetwork(string Key, string Label, string Color, string? UrlFormat, string Placeholder, Regex Valid);

static class Social
{
    static Regex R(string pattern) => new($"^{pattern}$", RegexOptions.CultureInvariant);

    public static readonly SocialNetwork[] Networks =
    [
        new("discord", "Discord", "#5865F2", null, "username", R(@"[a-z0-9._]{2,32}")),
        new("instagram", "Instagram", "#E1306C", "https://www.instagram.com/{0}", "username", R(@"[A-Za-z0-9._]{1,30}")),
        new("tiktok", "TikTok", "#25F4EE", "https://www.tiktok.com/@{0}", "username", R(@"[A-Za-z0-9._]{2,24}")),
        new("youtube", "YouTube", "#FF3B30", "https://www.youtube.com/@{0}", "handle", R(@"[A-Za-z0-9._-]{3,30}")),
        new("twitch", "Twitch", "#9146FF", "https://www.twitch.tv/{0}", "username", R(@"[A-Za-z0-9_]{3,25}")),
        new("x", "X", "#E7E9EA", "https://x.com/{0}", "username", R(@"[A-Za-z0-9_]{1,15}")),
        new("steam", "Steam", "#66C0F4", "https://steamcommunity.com/{0}", "custom URL name or 17-digit ID", R(@"[A-Za-z0-9_-]{2,32}")),
    ];

    /// Cleans what someone typed: strips "@", spaces and pasted web addresses. Returns null if it isn't valid.
    public static string? Clean(SocialNetwork n, string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        // allow pasting a whole link: keep the part after the domain
        var m = Regex.Match(s, @"^(?:https?://)?(?:www\.)?[a-z0-9.-]+\.[a-z]{2,}/(?:id/|profiles/|@)?([^/?#]+)", RegexOptions.IgnoreCase);
        if (m.Success && n.Key != "discord") s = m.Groups[1].Value;
        s = s.TrimStart('@');
        if (n.Key == "discord") s = s.ToLowerInvariant();
        return n.Valid.IsMatch(s) ? s : null;
    }

    /// The web address for a username (null for Discord, which has no profile links).
    public static string? Url(SocialNetwork n, string handle)
    {
        if (n.UrlFormat == null || !n.Valid.IsMatch(handle)) return null;
        if (n.Key == "steam")
            return string.Format(n.UrlFormat, Regex.IsMatch(handle, @"^\d{17}$") ? "profiles/" + handle : "id/" + handle);
        return string.Format(n.UrlFormat, handle);
    }
}
