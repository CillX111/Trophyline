namespace Trophyline;

/// Levels and XP. XP is always worked out fresh from your Steam data and tracked sessions,
/// so it can't be counted twice and matches your history (including everything before Trophyline).
static class Xp
{
    // ---------- what things are worth ----------

    public const int PerPerfectGame = 500;
    public const int PerGameStarted = 50;   // first achievement in a game
    public const int PerHourPlayed = 10;
    public const int PerSession = 20;       // each tracked session of 15 minutes or more

    /// An achievement is worth more the fewer players have it.
    public static int ForAchievement(double? rarity) => rarity switch
    {
        null => 25,
        < 1 => 150,
        < 5 => 100,
        < 10 => 60,
        < 20 => 40,
        < 50 => 25,
        _ => 15,
    };

    public static readonly (string What, string Worth)[] Rules =
    [
        ("Unlock an achievement", "15 – 150 XP (rarer = more)"),
        ("Ultra rare achievement (under 1% of players)", "150 XP"),
        ("Rare achievement (under 10%)", "60 – 100 XP"),
        ("Get your first achievement in a game", $"{PerGameStarted} XP"),
        ("Complete a game 100%", $"{PerPerfectGame} XP"),
        ("Play for an hour", $"{PerHourPlayed} XP"),
        ("Play a session of 15+ minutes (tracked by Trophyline)", $"{PerSession} XP"),
        ("Your rank multiplies all of these (except hours played)", "×1.05 – ×2.00"),
    ];

    // ---------- levels ----------

    /// XP needed to go from `level` to `level + 1`.
    public static int Need(int level) => 200 + 75 * (level - 1);

    public static (int Level, int Into, int Need) LevelFor(long total)
    {
        int level = 1;
        long left = total;
        while (left >= Need(level)) { left -= Need(level); level++; }
        return (level, (int)left, Need(level));
    }

    /// Each rank multiplies the XP you earn while you hold it (achievements, new games, 100%s and sessions).
    public static readonly (int From, string Title, string Color, decimal Mult)[] Ranks =
    [
        (1, "Rookie", "#8B99AB", 1.00m),
        (5, "Explorer", "#5BD68A", 1.05m),
        (10, "Hunter", "#66C0F4", 1.10m),
        (20, "Veteran", "#B98CFF", 1.15m),
        (30, "Elite", "#FF8A4C", 1.20m),
        (45, "Master", "#FF6FAE", 1.30m),
        (60, "Legend", "#F5C451", 1.40m),
        (75, "Mythic", "#3DD6C6", 1.50m),
        (90, "Immortal", "#FF5C6C", 1.60m),
        (110, "Titan", "#7B8CFF", 1.75m),
        (130, "Celestial", "#C9D6E3", 1.90m),
        (150, "Eternal", "#FFE08A", 2.00m),
    ];

    public static (string Title, string Color) RankFor(int level) =>
        Ranks.Last(r => level >= r.From) is var r ? (r.Title, r.Color) : ("Rookie", "#8B99AB");

    public static decimal MultFor(int level) => Ranks.Last(r => level >= r.From).Mult;

    /// "×1.10" (or "" for ×1).
    public static string MultText(decimal m) => m == 1m ? "" : $"×{m:0.00}";

    /// XP worth after the multiplier of the rank at `level` (the server rounds the same way).
    public static int Boost(int amount, int level) => (int)Math.Round(amount * MultFor(level), MidpointRounding.AwayFromZero);

    /// Your level as of the last time XP was added up (1 before that).
    public static int CurrentLevel { get; private set; } = 1;

    /// What each unlocked achievement actually gave you (with the multiplier of your rank back then).
    static Dictionary<AchievementInfo, int> earned = new(ReferenceEqualityComparer.Instance);
    public static int? EarnedFor(AchievementInfo a) => earned.TryGetValue(a, out var x) ? x : null;

    public static (int Level, string Title)? NextRank(int level) =>
        Ranks.FirstOrDefault(r => r.From > level) is { Title: not null } r ? (r.From, r.Title) : null;

    // ---------- adding it up ----------

    public sealed record Gain(long Time, string Text, string Game, int Amount);

    public sealed record Result(long Total, int Level, int Into, int Need, List<(string Source, int Count, long Xp)> Breakdown, List<Gain> Recent, List<Gain> All, long Bonus)
    {
        /// XP earned with a date inside this season (achievements, new games, 100%s and sessions).
        public long SeasonXp(Season s) => All.Where(g => g.Time >= s.Start && g.Time < s.End).Sum(g => (long)g.Amount);
    }

    // a dated gain before the multiplier: Kind 0 achievement, 1 first achievement in a game, 2 100%, 4 session
    sealed record Raw(long Time, string Text, string Game, int Amount, int Kind, AchievementInfo? Ach);

    /// Adds everything up. XP without a date (hours played, achievements Steam has no time for) counts as it is;
    /// then every dated gain, oldest first, is multiplied by the rank you had at that moment.
    /// The server (tl_walk_xp) works it out the same way, without tracked sessions.
    public static Result Compute(IEnumerable<GameInfo> games, IEnumerable<Session> sessions)
    {
        var list = games.ToList();
        var raw = new List<Raw>();
        var plain = new long[5];   // XP per kind that has no date (not multiplied)
        var counts = new int[5];

        foreach (var g in list)
        {
            var got = g.Achievements.Where(a => a.Unlocked).ToList();
            foreach (var a in got)
            {
                int x = ForAchievement(a.Rarity);
                counts[0]++;
                if (a.Time is long t && t > 0) raw.Add(new Raw(t, a.Name, g.Name, x, 0, a));
                else plain[0] += x;
            }
            if (got.Count > 0)
            {
                counts[1]++;
                if (got.Where(a => a.Time > 0).MinBy(a => a.Time) is { Time: long first })
                    raw.Add(new Raw(first, "First achievement in this game", g.Name, PerGameStarted, 1, null));
                else plain[1] += PerGameStarted;
            }
            if (g.Perfect)
            {
                counts[2]++;
                if (got.Where(a => a.Time > 0).Max(a => a.Time) is long last)
                    raw.Add(new Raw(last, "100% complete!", g.Name, PerPerfectGame, 2, null));
                else plain[2] += PerPerfectGame;
            }
        }

        counts[3] = list.Sum(g => g.PlaytimeMinutes) / 60;
        plain[3] = (long)counts[3] * PerHourPlayed;

        var names = list.GroupBy(g => g.AppId).ToDictionary(g => g.Key, g => g.First().Name);
        foreach (var s in sessions.Where(s => s.Minutes >= 15))
        {
            counts[4]++;
            raw.Add(new Raw(s.End, $"Played for {Fmt.Hours(s.Minutes)}", names.GetValueOrDefault(s.AppId) is { Length: > 0 } n ? n : $"App {s.AppId}", PerSession, 4, null));
        }

        // the walk: oldest first (same order as the server: time, then amount)
        long total = plain.Sum();
        var byKind = (long[])plain.Clone();
        var (lvl, into, _) = LevelFor(total);
        long left = into, bonus = 0;
        var gains = new List<Gain>(raw.Count);
        var newEarned = new Dictionary<AchievementInfo, int>(ReferenceEqualityComparer.Instance);
        foreach (var e in raw.OrderBy(e => e.Time).ThenBy(e => e.Amount))
        {
            int x = Boost(e.Amount, lvl);
            total += x; left += x; bonus += x - e.Amount; byKind[e.Kind] += x;
            while (left >= Need(lvl)) { left -= Need(lvl); lvl++; }
            gains.Add(new Gain(e.Time, e.Text, e.Game, x));
            if (e.Ach != null) newEarned[e.Ach] = x;
        }
        earned = newEarned;
        CurrentLevel = lvl;

        var breakdown = new List<(string, int, long)>
        {
            ("Achievements", counts[0], byKind[0]),
            ("New games", counts[1], byKind[1]),
            ("Perfect games", counts[2], byKind[2]),
            ("Hours played", counts[3], byKind[3]),
            ("Sessions", counts[4], byKind[4]),
        };
        gains.Reverse(); // newest first
        return new Result(total, lvl, (int)left, Need(lvl), breakdown, gains.Take(40).ToList(), gains, bonus);
    }

    // ---------- level-ups ----------

    static string SeenPath => Path.Combine(Program.Home, "level.txt");

    /// The level you were last shown (0 if never).
    public static int LastSeenLevel()
    {
        try { return File.Exists(SeenPath) && int.TryParse(File.ReadAllText(SeenPath).Trim(), out var l) ? l : 0; }
        catch (IOException) { return 0; }
    }

    public static void MarkSeen(int level)
    {
        try { File.WriteAllText(SeenPath, level.ToString()); } catch (IOException) { }
    }
}
