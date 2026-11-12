namespace Trophyline;

/// Watches Steam's stats folder; when a game's stats file changes, records new unlocks.
/// The timer only runs for a moment after a change, so the app is idle otherwise.
sealed class Watcher : IDisposable
{
    readonly Config cfg;
    readonly string statsFolder;
    readonly string? steamFolder;
    readonly Journal journal;
    readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly object gate = new();  // guards `pending`
    readonly object work = new();  // one journal update at a time
    readonly Timer timer;
    FileSystemWatcher? fsw;

    public Watcher(Config cfg, string statsFolder, string? steamFolder)
    {
        this.cfg = cfg;
        this.statsFolder = statsFolder;
        this.steamFolder = steamFolder;
        journal = new Journal(Program.Home);
        timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        fsw = new FileSystemWatcher(statsFolder, "*.bin")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 16 * 1024,
        };
        fsw.Created += (_, e) => Queue(e.FullPath);
        fsw.Changed += (_, e) => Queue(e.FullPath);
        fsw.Renamed += (_, e) => Queue(e.FullPath);
        fsw.Error += (_, _) => { lock (gate) foreach (var f in StatsFiles()) pending.Add(f); Kick(); };
        fsw.EnableRaisingEvents = true;
        ScanAll();
    }

    /// Reads every game once. On the very first run this imports your existing history.
    public (int added, int total) ScanAll()
    {
        lock (work) return ScanAllLocked();
    }

    (int added, int total) ScanAllLocked()
    {
        bool firstRun = journal.IsNew && journal.Count == 0; // import history with original dates
        int added = 0;
        foreach (var file in StatsFiles()) added += Process(file, fillMissingTimeWithNow: !firstRun);
        journal.WriteHtml(); // once per start: also fetches any icons that failed to download before
        return (added, journal.Count);
    }

    IEnumerable<string> StatsFiles() => Directory.EnumerateFiles(statsFolder, "UserGameStats_*.bin");

    void Queue(string path)
    {
        var name = Path.GetFileName(path);
        lock (gate)
        {
            if (name.StartsWith("UserGameStatsSchema_", StringComparison.OrdinalIgnoreCase))
            {
                // A game's names arrived or changed: re-read that game's stats.
                var appId = Path.GetFileNameWithoutExtension(name)["UserGameStatsSchema_".Length..];
                foreach (var f in Directory.EnumerateFiles(statsFolder, $"UserGameStats_*_{appId}.bin"))
                    pending.Add(f);
            }
            else if (name.StartsWith("UserGameStats_", StringComparison.OrdinalIgnoreCase))
                pending.Add(path);
            else return;
        }
        Kick();
    }

    // Wait 3 s after the last change (Steam may write in several steps), then process once.
    void Kick() => timer.Change(3000, Timeout.Infinite);

    void Flush()
    {
        string[] files;
        lock (gate) { files = pending.ToArray(); pending.Clear(); }
        var fresh = new List<Unlock>();
        try
        {
            lock (work)
            {
                foreach (var f in files) fresh.AddRange(ProcessNew(f, fillMissingTimeWithNow: true));
                if (fresh.Count > 0) journal.WriteHtml(); // also downloads the new icons
            }
            // Pop-up only for things unlocked just now (not for old history Steam syncs later).
            long recent = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 15 * 60;
            var live = fresh.Where(u => u.Time >= recent).ToList();
            if (live.Count > 0 && Program.Settings().UnlockPopups) Popup.Show(live);
        }
        catch (Exception ex) { Program.LogError(ex.ToString()); }
        Program.TrimMemory();
    }

    int Process(string statsFile, bool fillMissingTimeWithNow) => ProcessNew(statsFile, fillMissingTimeWithNow).Count;

    List<Unlock> ProcessNew(string statsFile, bool fillMissingTimeWithNow)
    {
        try
        {
            // null = names not downloaded yet; the schema file's arrival will queue it again.
            var unlocks = Steam.ReadUnlocks(statsFile, cfg.Language, steamFolder);
            return unlocks == null ? [] : journal.AddNew(unlocks, fillMissingTimeWithNow);
        }
        catch (Exception ex)
        {
            Program.LogError($"{Path.GetFileName(statsFile)}: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        fsw?.Dispose();
        timer.Dispose();
    }
}
