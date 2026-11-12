using System.Net;
using System.Text;

namespace Trophyline;

/// The journal: a tab-separated data file plus a generated HTML page to read it.
sealed class Journal
{
    readonly string dataPath;
    readonly string htmlPath;
    readonly HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

    public bool IsNew { get; }

    public Journal(string home)
    {
        dataPath = Path.Combine(home, "journal.tsv");
        htmlPath = Path.Combine(home, "journal.html");
        IsNew = !File.Exists(dataPath);
        if (!IsNew)
            foreach (var e in ReadAll()) keys.Add(e.Key);
    }

    public string HtmlPath => htmlPath;
    public int Count => keys.Count;

    /// Adds unlocks that aren't in the journal yet. Returns how many were new.
    public int Add(IEnumerable<Unlock> unlocks, bool fillMissingTimeWithNow)
    {
        var sb = new StringBuilder();
        int added = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var u in unlocks)
        {
            if (!keys.Add(u.Key)) continue;
            long? t = u.Time ?? (fillMissingTimeWithNow ? now : null);
            sb.Append(t?.ToString() ?? "").Append('\t')
              .Append(Clean(u.Key)).Append('\t')
              .Append(Clean(u.AppId)).Append('\t')
              .Append(Clean(u.Game)).Append('\t')
              .Append(Clean(u.Name)).Append('\t')
              .Append(Clean(u.Description)).Append('\t')
              .Append(Clean(u.Icon)).Append('\n');
            added++;
        }
        if (added > 0) File.AppendAllText(dataPath, sb.ToString(), Encoding.UTF8);
        return added;
    }

    static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    List<Unlock> ReadAll() => Load(dataPath);

    public static string DataPath(string home) => Path.Combine(home, "journal.tsv");

    /// Reads the journal; safe to call while the background app is writing to it.
    public static List<Unlock> Load(string dataPath)
    {
        var list = new List<Unlock>();
        if (!File.Exists(dataPath)) return list;
        string text;
        using (var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
            text = reader.ReadToEnd();
        foreach (var line in text.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length < 7) continue;
            list.Add(new Unlock(long.TryParse(p[0], out var t) ? t : null, p[1], p[2], p[3], p[4], p[5], p[6]));
        }
        return list;
    }

    // ---------- HTML page ----------

    public void WriteHtml()
    {
        var all = ReadAll();
        IconCache.EnsureCached(all);
        var dated = all.Where(u => u.Time != null).OrderByDescending(u => u.Time).ToList();
        var undated = all.Where(u => u.Time == null).OrderBy(u => u.Game).ThenBy(u => u.Name).ToList();
        int games = all.Select(u => u.AppId).Distinct().Count();

        var thisYear = DateTime.Now.Year;
        int yearCount = dated.Count(u => Local(u.Time!.Value).Year == thisYear);
        var top = all.GroupBy(u => u.Game).OrderByDescending(g => g.Count()).Take(5)
                     .Select(g => $"{H(g.Key)} <b>{g.Count()}</b>");

        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Trophyline</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#667085;--line:#e4e7ec;--accent:#3b6fd8}
@media (prefers-color-scheme:dark){:root{--bg:#111418;--card:#1a1f26;--text:#e6e9ee;--muted:#98a2b3;--line:#2a313b;--accent:#7aa2ff}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.45 system-ui,"Segoe UI",sans-serif}
main{max-width:860px;margin:0 auto;padding:24px 16px 48px}
h1{margin:0 0 4px;font-size:26px}
.sub{color:var(--muted);margin-bottom:16px}
.stats{display:flex;flex-wrap:wrap;gap:10px;margin-bottom:16px}
.stat{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px}
.stat b{display:block;font-size:22px}
.top{color:var(--muted);margin-bottom:16px}
.top span{margin-right:14px;white-space:nowrap}
input{width:100%;padding:10px 12px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--text);font:inherit;margin-bottom:8px}
h2{font-size:15px;color:var(--muted);text-transform:uppercase;letter-spacing:.05em;margin:24px 0 8px}
.e{display:flex;gap:12px;align-items:center;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px;margin-bottom:6px}
.e img{width:48px;height:48px;border-radius:6px;flex:none;background:var(--line)}
.e .t{flex:1;min-width:0}
.e .n{font-weight:600}
.e .d{color:var(--muted);font-size:13px}
.e .g{color:var(--accent);font-size:13px}
.e time{color:var(--muted);font-size:13px;white-space:nowrap}
.hide{display:none}
</style></head><body><main>
<h1>Trophyline</h1>
""");
        sb.Append($"<div class=\"sub\">Updated {DateTime.Now:dd.MM.yyyy HH:mm}</div>\n");
        sb.Append("<div class=\"stats\">");
        sb.Append($"<div class=\"stat\"><b>{all.Count}</b>achievements</div>");
        sb.Append($"<div class=\"stat\"><b>{games}</b>games</div>");
        sb.Append($"<div class=\"stat\"><b>{yearCount}</b>in {thisYear}</div>");
        sb.Append("</div>\n");
        if (all.Count > 0) sb.Append("<div class=\"top\">Most: ").Append(string.Join("", top.Select(t => $"<span>{t}</span>"))).Append("</div>\n");
        sb.Append("<input id=\"q\" type=\"search\" placeholder=\"Search game or achievement…\">\n");

        string? month = null;
        foreach (var u in dated)
        {
            var local = Local(u.Time!.Value);
            var m = local.ToString("MMMM yyyy");
            if (m != month) { if (month != null) sb.Append("</section>\n"); sb.Append($"<section><h2>{m}</h2>\n"); month = m; }
            Entry(sb, u, local.ToString("dd.MM.yyyy HH:mm"));
        }
        if (month != null) sb.Append("</section>\n");

        if (undated.Count > 0)
        {
            sb.Append("<section><h2>Earlier (Steam has no date)</h2>\n");
            foreach (var u in undated) Entry(sb, u, "");
            sb.Append("</section>\n");
        }
        if (all.Count == 0) sb.Append("<p class=\"sub\">No achievements yet. They appear here as you unlock them.</p>");

        sb.Append("""
</main><script>
const q=document.getElementById('q');
q.addEventListener('input',()=>{const s=q.value.toLowerCase();
document.querySelectorAll('section').forEach(sec=>{let any=false;
sec.querySelectorAll('.e').forEach(e=>{const ok=e.dataset.s.includes(s);e.classList.toggle('hide',!ok);any||=ok});
sec.classList.toggle('hide',!any)})});
</script></body></html>
""");
        var tmp = htmlPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
        File.Move(tmp, htmlPath, overwrite: true);
    }

    static void Entry(StringBuilder sb, Unlock u, string when)
    {
        var search = H($"{u.Game} {u.Name} {u.Description}".ToLowerInvariant());
        var src = IconCache.DataUri(u) ?? (u.Icon.Length > 0 ? IconCache.RemoteUrl(u) : null);
        var img = src != null ? $"<img alt=\"\" src=\"{H(src)}\">" : "<img alt=\"\">";
        sb.Append($"<div class=\"e\" data-s=\"{search}\">{img}<div class=\"t\"><div class=\"n\">{H(u.Name)}</div>");
        if (u.Description.Length > 0) sb.Append($"<div class=\"d\">{H(u.Description)}</div>");
        sb.Append($"<div class=\"g\">{H(u.Game)}</div></div>");
        if (when.Length > 0) sb.Append($"<time>{when}</time>");
        sb.Append("</div>\n");
    }

    static DateTime Local(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
    static string H(string s) => WebUtility.HtmlEncode(s);
}
