using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Trophyline;

public sealed record Unlock(long? Time, string Key, string AppId, string Game, string Name, string Description, string Icon);

/// Reads unlocked achievements from Steam's local cache files.
static partial class Steam
{
    public static string? FindSteamFolder()
    {
        var p = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string;
        if (p == null) return null;
        p = Path.GetFullPath(p.Replace('/', '\\'));
        return Directory.Exists(p) ? p : null;
    }

    /// Returns null when the file can't be read yet (e.g. schema not downloaded) so it is retried later.
    public static List<Unlock>? ReadUnlocks(string statsFile, string language, string? steamFolder)
    {
        // File name: UserGameStats_<account>_<appid>.bin
        var parts = Path.GetFileNameWithoutExtension(statsFile).Split('_');
        if (parts.Length != 3) return [];
        string account = parts[1], appId = parts[2];

        var dir = Path.GetDirectoryName(statsFile)!;
        var schemaFile = Path.Combine(dir, $"UserGameStatsSchema_{appId}.bin");
        if (!File.Exists(schemaFile) || !File.Exists(statsFile)) return null;

        var stats = BinaryKV.Parse(ReadShared(statsFile)).Obj("cache");
        var schemaRoot = BinaryKV.Parse(ReadShared(schemaFile));
        var schema = schemaRoot.Obj(appId)
                     ?? schemaRoot.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
        if (stats == null || schema == null) return [];

        string game = schema.Str("gamename") ?? GameNameFromManifest(steamFolder, appId) ?? $"App {appId}";
        var schemaStats = schema.Obj("stats");
        var result = new List<Unlock>();

        foreach (var (statId, value) in stats)
        {
            if (value is not Dictionary<string, object> stat) continue;
            var statSchema = schemaStats.Obj(statId);
            var type = statSchema.Str("type");
            if (type is not ("ACHIEVEMENTS" or "4")) continue; // plain number stats, not achievements

            long data = stat.Num("data") ?? 0;
            var times = stat.Obj("AchievementTimes");
            var bits = statSchema.Obj("bits");

            for (int bit = 0; bit < 32; bit++)
            {
                if (((data >> bit) & 1) == 0) continue;
                var bitSchema = bits.Obj(bit.ToString());
                var api = bitSchema.Str("name") ?? $"stat{statId}_bit{bit}";
                var display = bitSchema.Obj("display");
                result.Add(new Unlock(
                    Time: times.Num(bit.ToString()) is long t && t > 0 ? t : null,
                    Key: $"{account}/{appId}/{api}",
                    AppId: appId,
                    Game: game,
                    Name: Localized(display.Obj("name"), language) ?? api,
                    Description: Localized(display.Obj("desc"), language) ?? "",
                    Icon: display.Str("icon") ?? ""));
            }
        }
        return result;
    }

    /// Every achievement a game has (from its schema), marked unlocked or not from your stats file.
    public static (string? GameName, List<AchievementInfo> List) ReadAchievements(string schemaFile, string? statsFile, string language)
    {
        var appId = Path.GetFileNameWithoutExtension(schemaFile)["UserGameStatsSchema_".Length..];
        var schemaRoot = BinaryKV.Parse(ReadShared(schemaFile));
        var schema = schemaRoot.Obj(appId) ?? schemaRoot.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
        var stats = statsFile != null && File.Exists(statsFile) ? BinaryKV.Parse(ReadShared(statsFile)).Obj("cache") : null;
        var list = new List<AchievementInfo>();
        if (schema == null) return (null, list);

        foreach (var (statId, value) in schema.Obj("stats") ?? [])
        {
            if (value is not Dictionary<string, object> statSchema) continue;
            if (statSchema.Str("type") is not ("ACHIEVEMENTS" or "4")) continue;
            var mine = stats.Obj(statId);
            long data = mine.Num("data") ?? 0;
            var times = mine.Obj("AchievementTimes");

            foreach (var (bitKey, bv) in statSchema.Obj("bits") ?? [])
            {
                if (bv is not Dictionary<string, object> bitSchema || !int.TryParse(bitKey, out var bit) || bit is < 0 or > 31) continue;
                var display = bitSchema.Obj("display");
                bool unlocked = ((data >> bit) & 1) == 1;
                list.Add(new AchievementInfo
                {
                    AppId = appId,
                    ApiName = bitSchema.Str("name") ?? $"stat{statId}_bit{bit}",
                    Name = Localized(display.Obj("name"), language) ?? bitSchema.Str("name") ?? "?",
                    Description = Localized(display.Obj("desc"), language) ?? "",
                    Hidden = display.Str("hidden") == "1",
                    Icon = display.Str("icon") ?? "",
                    IconGray = display.Str("icon_gray") ?? "",
                    Unlocked = unlocked,
                    Time = unlocked && times.Num(bitKey) is long t && t > 0 ? t : null,
                });
            }
        }
        return (schema.Str("gamename"), list);
    }

    static string? Localized(Dictionary<string, object>? texts, string language)
    {
        if (texts == null) return null;
        return texts.Str(language) ?? texts.Str("english")
               ?? texts.Where(kv => kv.Key != "token").Select(kv => kv.Value as string).FirstOrDefault(s => s != null);
    }

    static string? GameNameFromManifest(string? steamFolder, string appId)
    {
        if (steamFolder == null) return null;
        try
        {
            var libraries = new List<string> { steamFolder };
            var vdf = Path.Combine(steamFolder, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in PathLine().Matches(File.ReadAllText(vdf)))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var m = NameLine().Match(File.ReadAllText(acf));
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch (IOException) { }
        return null;
    }

    /// Reads without locking Steam out of its own file; retries briefly if Steam is mid-write.
    static byte[] ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[fs.Length];
                fs.ReadExactly(buffer);
                return buffer;
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(300); }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex PathLine();

    [GeneratedRegex("\"name\"\\s+\"([^\"]+)\"")]
    private static partial Regex NameLine();
}
