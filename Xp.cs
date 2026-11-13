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

    public static readonly (int From, string Title, string Color)[] Ranks =
    [
        (1, "Rookie", "#8B99AB"),
        (5, "Explorer", "#5BD68A"),
        (10, "Hunter", "#66C0F4"),
        (20, "Veteran", "#B98CFF"),
        (30, "Elite", "#FF8A4C"),
        (45, "Master", "#FF6FAE"),
        (60, "Legend", "#F5C451"),
        (75, "Mythic", "#3DD6C6"),
        (90, "Immortal", "#FF5C6C"),
        (110, "Titan", "#7B8CFF"),
        (130, "Celestial", "#C9D6E3"),
        (150, "Eternal", "#FFE08A"),
    ];

    public static (string Title, string Color) RankFor(int level) =>
        Ranks.Last(r => level >= r.From) is var r ? (r.Title, r.Color) : ("Rookie", "#8B99AB");

    public static (int Level, string Title)? NextRank(int level) =>
        Ranks.FirstOrDefault(r => r.From > level) is { Title: not null } r ? (r.From, r.Title) : null;

    // ---------- adding it up ----------

    public sealed record Gain(long Time, string Text, string Game, int Amount);

    public sealed record Result(long Total, int Level, int Into, int Need, List<(string Source, int Count, long Xp)> Breakdown, List<Gain> Recent, List<Gain> All)
    {
        /// XP earned with a date inside this season (achievements, new games, 100%s and sessions).
        public long SeasonXp(Season s) => All.Where(g => g.Time >= s.Start && g.Time < s.End).Sum(g => (long)g.Amount);
    }

    public static Result Compute(IEnumerable<GameInfo> games, IEnumerable<Session> sessions)
    {
        var list = games.ToList();
        var gains = new List<Gain>();
        long ach = 0, starts = 0, perfect = 0;
        int achCount = 0, startCount = 0, perfectCount = 0;

        foreach (var g in list)
        {
            var got = g.Achievements.Where(a => a.Unlocked).ToList();
            foreach (var a in got)
            {
                int x = ForAchievement(a.Rarity);
                ach += x; achCount++;
                if (a.Time is long t) gains.Add(new Gain(t, a.Name, g.Name, x));
            }
            if (got.Count > 0)
            {
                starts += PerGameStarted; startCount++;
                if (got.Where(a => a.Time != null).MinBy(a => a.Time) is { Time: long first })
                    gains.Add(new Gain(first, "First achievement in this game", g.Name, PerGameStarted));
            }
            if (g.Perfect)
            {
                perfect += PerPerfectGame; perfectCount++;
                if (got.Max(a => a.Time) is long last)
                    gains.Add(new Gain(last, "100% complete!", g.Name, PerPerfectGame));
            }
        }

        int hours = list.Sum(g => g.PlaytimeMinutes) / 60;
        long play = (long)hours * PerHourPlayed;

        var names = list.GroupBy(g => g.AppId).ToDictionary(g => g.Key, g => g.First().Name);
        var counted = sessions.Where(s => s.Minutes >= 15).ToList();
        long sess = (long)counted.Count * PerSession;
        foreach (var s in counted)
            gains.Add(new Gain(s.End, $"Played for {Fmt.Hours(s.Minutes)}", names.GetValueOrDefault(s.AppId) is { Length: > 0 } n ? n : $"App {s.AppId}", PerSession));

        long total = ach + starts + perfect + play + sess;
        var (level, into, need) = LevelFor(total);
        var breakdown = new List<(string, int, long)>
        {
            ("Achievements", achCount, ach),
            ("New games", startCount, starts),
            ("Perfect games", perfectCount, perfect),
            ("Hours played", hours, play),
            ("Sessions", counted.Count, sess),
        };
        var sorted = gains.OrderByDescending(g => g.Time).ToList();
        return new Result(total, level, into, need, breakdown, sorted.Take(40).ToList(), sorted);
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
