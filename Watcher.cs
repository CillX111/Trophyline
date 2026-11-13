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
    readonly object gate = new();  // guards `pending` and `fsw`
    readonly object work = new();  // one journal update at a time
    readonly Timer timer;
    readonly Timer rewatch;
    FileSystemWatcher? fsw;
    volatile bool disposed;

    /// More undated new unlocks than this in one file means old history Steam just synced (first play of an
    /// old game on this PC, another Steam account signing in): keep them undated and don't pop them up.
    const int MaxUndatedLive = 3;

    public Watcher(Config cfg, string statsFolder, string? steamFolder)
    {
        this.cfg = cfg;
        this.statsFolder = statsFolder;
        this.steamFolder = steamFolder;
        journal = new Journal(Program.Home);
        timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        rewatch = new Timer(_ => Rewatch(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        Watch();
        ScanAll();
    }

    /// (Re)creates the folder watcher. When Steam's stats folder is gone (appcache cleared), tries again every 30 s.
    void Watch()
    {
        lock (gate)
        {
            fsw?.Dispose();
            fsw = null;
            if (disposed) return;
            try
            {
                var w = new FileSystemWatcher(statsFolder, "*.bin")
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 16 * 1024,
                };
                w.Created += (_, e) => Queue(e.FullPath);
                w.Changed += (_, e) => Queue(e.FullPath);
                w.Renamed += (_, e) => Queue(e.FullPath);
                w.Error += (_, _) => Rewatch();
                w.EnableRaisingEvents = true;
                fsw = w;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or FileNotFoundException)
            {
                rewatch.Change(30_000, Timeout.Infinite);
            }
        }
    }

    /// The watcher overflowed or its folder vanished: watch again and re-read every game.
    void Rewatch()
    {
        try
        {
            Watch();
            if (fsw == null) return; // folder still missing: Watch() scheduled another try
            lock (gate) foreach (var f in StatsFiles()) pending.Add(f);
            Kick();
        }
        catch (Exception ex)
        {
            Program.LogError("watcher: " + ex.Message);
            if (!disposed) rewatch.Change(30_000, Timeout.Infinite);
        }
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
        var lang = Program.ReadLanguage(); // read now, so a language change in Settings applies without a restart
        foreach (var file in StatsFiles()) added += ProcessNew(file, lang, fillMissingTimeWithNow: !firstRun).Count;
        journal.WriteHtml(); // once per start: also fetches any icons that failed to download before
        return (added, journal.Count);
    }

    IEnumerable<string> StatsFiles() =>
        Directory.Exists(statsFolder) ? Directory.EnumerateFiles(statsFolder, "UserGameStats_*.bin") : [];

    void Queue(string path)
    {
        try
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
        catch (Exception ex) { Program.LogError("watcher: " + ex.Message); } // e.g. folder deleted meanwhile
    }

    // Wait 3 s after the last change (Steam may write in several steps), then process once.
    void Kick()
    {
        try { timer.Change(3000, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    void Flush()
    {
        string[] files;
        lock (gate) { files = pending.ToArray(); pending.Clear(); }
        var fresh = new List<Unlock>();
        try
        {
            lock (work)
            {
                var lang = Program.ReadLanguage();
                foreach (var f in files) fresh.AddRange(ProcessNew(f, lang, fillMissingTimeWithNow: true));
                if (fresh.Count == 0) return;
                IconCache.EnsureCached(fresh); // just the new icons, so the pop-up isn't held up
            }
            // Pop-up only for things unlocked just now (not for old history Steam syncs later).
            long recent = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 15 * 60;
            var live = fresh.Where(u => u.Time >= recent).ToList();
            if (live.Count > 0 && Program.Settings().UnlockPopups) Popup.Show(live);
            lock (work) journal.WriteHtml();
        }
        catch (Exception ex) { Program.LogError(ex.ToString()); }
        finally { Program.TrimMemory(); }
    }

    List<Unlock> ProcessNew(string statsFile, string language, bool fillMissingTimeWithNow)
    {
        try
        {
            // null = names not downloaded yet; the schema file's arrival will queue it again.
            var unlocks = Steam.ReadUnlocks(statsFile, language, steamFolder);
            if (unlocks == null) return [];
            if (fillMissingTimeWithNow && unlocks.Count(u => u.Time == null && !journal.Has(u.Key)) > MaxUndatedLive)
                fillMissingTimeWithNow = false;
            return journal.AddNew(unlocks, fillMissingTimeWithNow);
        }
        catch (Exception ex)
        {
            Program.LogError($"{Path.GetFileName(statsFile)}: {ex.Message}");
            return [];
        }
    }

    public void Dispose()
    {
        disposed = true;
        lock (gate) { fsw?.Dispose(); fsw = null; }
        timer.Dispose();
        rewatch.Dispose();
    }
}
