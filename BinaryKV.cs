using System.Text;

namespace Trophyline;

/// Reader for Valve's binary KeyValues format (used by Steam's appcache files).
static class BinaryKV
{
    public static Dictionary<string, object> Parse(byte[] bytes)
    {
        int pos = 0;
        return ParseObject(bytes, ref pos);
    }

    static Dictionary<string, object> ParseObject(byte[] b, ref int pos)
    {
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (pos < b.Length)
        {
            byte type = b[pos++];
            if (type == 8 || type == 11) break; // end of object
            string key = ReadString(b, ref pos);
            object value;
            switch (type)
            {
                case 0: value = ParseObject(b, ref pos); break;
                case 1: value = ReadString(b, ref pos); break;
                case 2: value = BitConverter.ToInt32(b, pos); pos += 4; break;
                case 3: value = BitConverter.ToSingle(b, pos); pos += 4; break;
                case 4: value = BitConverter.ToInt32(b, pos); pos += 4; break; // pointer
                case 6: value = BitConverter.ToInt32(b, pos); pos += 4; break; // color
                case 7: case 10: value = BitConverter.ToInt64(b, pos); pos += 8; break;
                default: throw new InvalidDataException($"Unknown KeyValues type {type} at {pos}");
            }
            d[key] = value;
        }
        return d;
    }

    static string ReadString(byte[] b, ref int pos)
    {
        int start = pos;
        while (pos < b.Length && b[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(b, start, pos - start);
        pos++;
        return s;
    }

    // ----- lookup helpers -----

    public static Dictionary<string, object>? Obj(this Dictionary<string, object>? d, string key) =>
        d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    public static string? Str(this Dictionary<string, object>? d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v is not Dictionary<string, object> ? v.ToString() : null;

    public static long? Num(this Dictionary<string, object>? d, string key) =>
        d != null && d.TryGetValue(key, out var v)
            ? v switch
            {
                int i => i,
                long l => l,
                string s when long.TryParse(s, out var x) => x,
                _ => null,
            }
            : null;
}
