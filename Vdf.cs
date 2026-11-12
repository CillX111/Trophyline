using System.Text;

namespace Trophyline;

/// Reader for Valve's text KeyValues files (.vdf / .acf).
static class Vdf
{
    public static Dictionary<string, object> ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    public static Dictionary<string, object> Parse(string text)
    {
        int pos = 0;
        return ParseObject(text, ref pos);
    }

    static Dictionary<string, object> ParseObject(string s, ref int pos)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = NextToken(s, ref pos);
            if (key == null || key == "}") return d;
            var value = NextToken(s, ref pos);
            if (value == null) return d;
            d[key] = value == "{" ? ParseObject(s, ref pos) : value;
        }
    }

    // Returns a quoted/bare string, "{" or "}", or null at the end.
    static string? NextToken(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c == '{' || c == '}') { pos++; return c.ToString(); }
            if (c == '"')
            {
                var sb = new StringBuilder();
                pos++;
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length)
                    {
                        pos++;
                        sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', _ => s[pos] });
                    }
                    else sb.Append(s[pos]);
                    pos++;
                }
                pos++;
                return sb.ToString();
            }
            int start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}' && s[pos] != '"') pos++;
            return s[start..pos];
        }
        return null;
    }

    /// Walks nested keys, e.g. Path(d, "UserLocalConfigStore", "Software", "Valve").
    public static Dictionary<string, object>? Path(Dictionary<string, object>? d, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (d == null) return null;
            d = d.Obj(k);
        }
        return d;
    }
}
