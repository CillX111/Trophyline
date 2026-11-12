using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Trophyline;

public partial class MainWindow : Window
{
    const string AllGames = "All games";
    static readonly string[] RarityOptions = ["Any rarity", "Rare (under 15%)", "Ultra rare (under 5%)"];
    static readonly string[] SortOptions = ["Recently played", "Most played", "Completion", "Most achievements", "Name"];

    readonly TaskCompletionSource ready = new();
    readonly DispatcherTimer reloadTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer achSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer gameSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    FileSystemWatcher? watcher;
    DateTime lastLoad;
    bool loading;
    bool syncingNav;

    SteamData? data;
    List<GameVM> games = [];
    List<AchVM> unlocked = [];
    List<AchVM> gameRows = [];
    GameVM? currentGame;
    Control? pageBeforeGame;

    readonly DispatcherTimer musicTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer nowTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    List<Session> sessions = [];
    List<PlaylistVM> playlists = [];
    bool musicBusy;
    string? lastTitle, lastArtist;

    /// Completes once the first load (including online extras) is done. Used by --snapshot.
    public Task Ready => ready.Task;

    public MainWindow()
    {
        InitializeComponent();

        // Opens maximized; if you un-maximize it, it still fits on the screen.
        if (Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(1280, area.Width * 0.9);
            Height = Math.Min(900, area.Height * 0.9);
        }

        NavOverview.IsCheckedChanged += (_, _) => { if (NavOverview.IsChecked == true && !syncingNav) ShowPage(OverviewPage); };
        NavAchievements.IsCheckedChanged += (_, _) => { if (NavAchievements.IsChecked == true && !syncingNav) ShowPage(AchievementsPage); };
        NavGames.IsCheckedChanged += (_, _) => { if (NavGames.IsChecked == true && !syncingNav) ShowPage(GamesPage); };
        NavStorage.IsCheckedChanged += (_, _) => { if (NavStorage.IsChecked == true && !syncingNav) ShowPage(StoragePage); };
        NavMusic.IsCheckedChanged += (_, _) => { if (NavMusic.IsChecked == true && !syncingNav) ShowPage(MusicPage); };
        NavLevel.IsCheckedChanged += (_, _) => { if (NavLevel.IsChecked == true && !syncingNav) { ShowPage(LevelPage); AnimateLevelBar(); } };
        NavPlaytime.IsCheckedChanged += (_, _) => { if (NavPlaytime.IsChecked == true && !syncingNav) { BuildPlaytime(); ShowPage(PlaytimePage); } };
        nowTimer.Tick += (_, _) => { if (PlaytimePage.IsVisible) UpdateNowPlaying(); };
        PopupsSwitch.IsCheckedChanged += (_, _) =>
        {
            if (fillingSettings) return;
            var cfg = Program.Settings();
            cfg.UnlockPopups = PopupsSwitch.IsChecked == true;
            Program.SaveSettings(cfg);
        };
        NavSettings.IsCheckedChanged += (_, _) => { if (NavSettings.IsChecked == true && !syncingNav) { BuildSettings(); ShowPage(SettingsPage); } };
        GameSoundtrack.SelectionChanged += (_, _) => SoundtrackChanged();
        PauseOnQuitSwitch.IsCheckedChanged += (_, _) =>
        {
            if (fillingSettings) return;
            var cfg = Program.Settings();
            cfg.PauseSoundtrackOnQuit = PauseOnQuitSwitch.IsChecked == true;
            Program.SaveSettings(cfg);
        };

        // Spotify: ask what's playing every 2 seconds while the window is open.
        musicTimer.Tick += async (_, _) => await RefreshMusic();
        PlaylistLink.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await AddPlaylistFromBox(); } };
        SpotifyWindowRow.IsVisible = Platform.Windows;
        HideSpotifySwitch.IsChecked = Program.Settings().HideSpotify;
        HideSpotifySwitch.IsCheckedChanged += async (_, _) =>
        {
            var cfg = Program.Settings();
            cfg.HideSpotify = HideSpotifySwitch.IsChecked == true;
            Program.SaveSettings(cfg);
            if (cfg.HideSpotify) await Task.Run(Spotify.HideWindow); // tuck it away right now
            await RefreshMusic();
        };
        FillPlaylists();

        AchRarity.ItemsSource = RarityOptions;
        AchRarity.SelectedIndex = 0;
        GameSort.ItemsSource = SortOptions;
        GameSort.SelectedIndex = 0;

        achSearchTimer.Tick += (_, _) => { achSearchTimer.Stop(); FillAchList(); };
        gameSearchTimer.Tick += (_, _) => { gameSearchTimer.Stop(); FillGamesGrid(); };
        AchSearch.TextChanged += (_, _) => { achSearchTimer.Stop(); achSearchTimer.Start(); };
        GameSearch.TextChanged += (_, _) => { gameSearchTimer.Stop(); gameSearchTimer.Start(); };
        AchGame.SelectionChanged += (_, _) => FillAchList();
        AchRarity.SelectionChanged += (_, _) => FillAchList();
        GameSort.SelectionChanged += (_, _) => FillGamesGrid();
        ChipAll.IsCheckedChanged += (_, _) => { if (ChipAll.IsChecked == true) FillGameAchList(); };
        ChipUnlocked.IsCheckedChanged += (_, _) => { if (ChipUnlocked.IsChecked == true) FillGameAchList(); };
        ChipLocked.IsCheckedChanged += (_, _) => { if (ChipLocked.IsChecked == true) FillGameAchList(); };

        reloadTimer.Tick += async (_, _) => { reloadTimer.Stop(); await LoadAsync(online: false); };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            WatchJournal();
            musicTimer.Start();
            nowTimer.Start();
            _ = RefreshMusic();
            await LoadAsync(online: true);
            ready.TrySetResult();
        };
        Activated += async (_, _) =>
        {
            if (!loading && lastLoad != default && DateTime.Now - lastLoad > TimeSpan.FromMinutes(2))
                await LoadAsync(online: false);
        };
        Closed += (_, _) => watcher?.Dispose();
    }

    static IBrush Res(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var v) && v is IBrush b ? b : Brushes.Gray;

    static Geometry Icon(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var v) && v is Geometry g ? g : new StreamGeometry();

    // =============== loading ===============

    async Task LoadAsync(bool online)
    {
        if (loading) return;
        loading = true;
        try
        {
            if (data == null) ShowOverlay("Reading your Steam library…");
            var language = Program.ReadLanguage();
            SteamData d;
            try { d = await Task.Run(() => SteamData.Load(language)); }
            catch (Exception ex)
            {
                Program.LogError("window load: " + ex);
                ShowOverlay("Couldn't read your Steam files.\n\n" + ex.Message +
                            "\n\nIf Steam is installed somewhere unusual, set \"steamFolder\" in\n" + System.IO.Path.Combine(Program.Home, "config.json"));
                return;
            }
            Apply(d);
            Overlay.IsVisible = false;
            lastLoad = DateTime.Now;
            Program.LogWindow($"loaded games={d.Games.Count} unlocked={unlocked.Count} persona={d.PersonaName != null} home={Program.Home}");

            if (online)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Trophyline/1.1");
                    bool names = await NameCache.FetchMissing(d.Games, http);
                    bool rarity = await Rarity.Refresh(d.Games, http);
                    if (names || rarity) Apply(d);
                }
                catch (Exception ex) { Program.LogError("online refresh: " + ex.Message); }
            }
        }
        finally { loading = false; }
    }

    void Apply(SteamData d)
    {
        data = d;
        games = d.Games.Where(g => !string.IsNullOrEmpty(g.Name) || g.Total > 0).Select(g => new GameVM(g)).ToList();
        unlocked = games.SelectMany(vm => vm.G.Achievements.Where(a => a.Unlocked).Select(a => new AchVM(a, vm.G)))
                        .OrderByDescending(a => a.A.Time ?? 0).ToList();
        foreach (var a in unlocked)
            a.Group = a.A.Time is long t ? Fmt.Local(t).ToString("MMMM yyyy") : "Earlier (Steam has no date)";

        BuildProfile();
        BuildOverview();
        BuildAchievements();
        BuildGames();
        BuildStorage();
        BuildPlaytime();
        BuildLevel(d);
        if (currentGame != null && games.FirstOrDefault(g => g.G.AppId == currentGame.G.AppId) is GameVM again)
            FillGamePage(again, animate: false);
    }

    void WatchJournal()
    {
        // The tracker writes the journal the moment you unlock something: reload then.
        try
        {
            // ...and when it records a game starting or stopping.
            watcher = new FileSystemWatcher(Program.Home)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler h = (_, e) =>
            {
                if (e.Name is not ("journal.tsv" or "sessions.tsv" or "open-session.txt")) return;
                Dispatcher.UIThread.Post(() => { reloadTimer.Stop(); reloadTimer.Start(); });
            };
            watcher.Changed += h;
            watcher.Created += h;
            watcher.Deleted += h;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Program.LogError("watch: " + ex.Message); }
    }

    void ShowOverlay(string text)
    {
        OverlayText.Text = text;
        Overlay.IsVisible = true;
    }

    // =============== sidebar ===============

    void BuildProfile()
    {
        var name = data?.PersonaName ?? "Steam player";
        ProfileName.Text = name;
        AvatarLetter.Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
        bool tracking = Program.TrackerRunning();
        TrackerDot.Fill = tracking ? Fmt.Green : Fmt.Orange;
        TrackerText.Text = tracking ? "Tracking live" : "Tracker off";
        ToolTip.SetTip(TrackerText, tracking
            ? "The background tracker is recording new achievements."
            : "The background tracker isn't running. Run Trophyline --install to start it with your computer.");
    }

    // =============== overview ===============

    void BuildOverview()
    {
        Persona.Text = data?.PersonaName ?? "Player";

        var recent = games.Where(g => g.G.LastPlayed > 0).OrderByDescending(g => g.G.LastPlayed).ToList();
        var heroGame = recent.FirstOrDefault(g => g.G.Hero != null) ?? recent.FirstOrDefault();
        HeroImage.Source = Img.File(heroGame?.G.Hero ?? heroGame?.G.Header, 1600);
        HeroLogo.Source = Img.File(heroGame?.G.Logo, 700);
        var two = games.Sum(g => g.G.Playtime2WeeksMinutes);
        HeroSub.Text = recent.FirstOrDefault() is GameVM last
            ? $"Last played {last.Name}  ·  {Fmt.Ago(last.G.LastPlayed)}" + (two > 0 ? $"  ·  {Fmt.Hours(two)} in the last 2 weeks" : "")
            : "Play a game on Steam and it shows up here.";

        Tiles.Children.Clear();
        var withAch = games.Where(g => g.G.Total > 0).ToList();
        var started = withAch.Where(g => g.G.UnlockedCount > 0).ToList();
        int totalAch = withAch.Sum(g => g.G.Total);
        int perfect = withAch.Count(g => g.G.Perfect);
        int half = withAch.Count(g => g.G.Completion >= 0.5);
        double avg = started.Count == 0 ? 0 : started.Average(g => g.G.Completion) * 100;
        int minutes = games.Sum(g => g.G.PlaytimeMinutes);
        var rarest = unlocked.Where(a => a.A.Rarity != null).OrderBy(a => a.A.Rarity).FirstOrDefault();

        Tiles.Children.Add(Tile("IconStar", unlocked.Count.ToString("N0"), "achievements unlocked", $"out of {totalAch:N0} in your games", Palette.Brush));
        Tiles.Children.Add(Tile("IconStar", perfect.ToString(), perfect == 1 ? "perfect game" : "perfect games", $"{half} games at 50% or more", Fmt.Gold));
        Tiles.Children.Add(Tile("IconChart", $"{avg:0}%", "average completion", $"across {started.Count} games you've started", Fmt.Purple));
        Tiles.Children.Add(Tile("IconClock", $"{minutes / 60.0:N0} h", "total playtime", two > 0 ? $"{Fmt.Hours(two)} in the last 2 weeks" : "nothing in the last 2 weeks", Fmt.Green));
        Tiles.Children.Add(Tile("IconGame", games.Count.ToString(), "games", $"{games.Count(g => g.G.Installed)} installed", Palette.Brush));
        Tiles.Children.Add(rarest != null
            ? Tile("IconBolt", rarest.A.Rarity < 10 ? $"{rarest.A.Rarity:0.0}%" : $"{rarest.A.Rarity:0}%", "rarest unlock", $"{rarest.Name} · {rarest.Game}", Fmt.Gold)
            : Tile("IconBolt", "–", "rarest unlock", "loading global stats…", Fmt.Gold));

        BuildHeatmap();

        RecentList.ItemsSource = unlocked.Take(6).ToList();
        RareList.ItemsSource = unlocked.Where(a => a.A.Rarity != null).OrderBy(a => a.A.Rarity).Take(6).ToList();
        MostPlayed.ItemsSource = games.Where(g => g.G.PlaytimeMinutes > 0).OrderByDescending(g => g.G.PlaytimeMinutes).Take(10).ToList();
    }

    static Border Tile(string icon, string value, string label, string sub, IBrush color)
    {
        var badge = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12),
            Background = Fmt.Tint(color, 0x26),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
            Child = new PathIcon { Data = Icon(icon), Width = 19, Height = 19, Foreground = color },
        };
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeight.Bold });
        text.Children.Add(new TextBlock { Text = label, FontSize = 13 });
        text.Children.Add(new TextBlock
        {
            Text = sub, Foreground = Res("Muted"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var row = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(text);
        var tile = new Border { Margin = new Thickness(8), Padding = new Thickness(18, 16, 18, 16), Child = row };
        tile.Classes.Add("card");
        return tile;
    }

    void BuildHeatmap()
    {
        Heatmap.Children.Clear();
        HeatLegend.Children.Clear();
        var counts = unlocked.Where(a => a.A.Time != null)
            .GroupBy(a => Fmt.Local(a.A.Time!.Value).Date)
            .ToDictionary(g => g.Key, g => g.Count());

        const int weeks = 53;
        const double cell = 14, step = 18, left = 34, top = 22;
        var today = DateTime.Today;
        var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).AddDays(-7 * (weeks - 1)); // a Monday
        IBrush[] levels = Palette.HeatLevels();
        var muted = Res("Muted");

        int lastMonth = -1;
        for (int w = 0; w < weeks; w++)
        {
            for (int d = 0; d < 7; d++)
            {
                var date = start.AddDays(w * 7 + d);
                if (date > today) break;
                counts.TryGetValue(date, out var n);
                int lvl = n == 0 ? 0 : n == 1 ? 1 : n <= 3 ? 2 : n <= 6 ? 3 : 4;
                var r = new Rectangle { Width = cell, Height = cell, RadiusX = 3, RadiusY = 3, Fill = levels[lvl] };
                ToolTip.SetTip(r, $"{date:ddd dd.MM.yyyy}:  {(n == 0 ? "no" : n.ToString())} achievement{(n == 1 ? "" : "s")}");
                Canvas.SetLeft(r, left + w * step);
                Canvas.SetTop(r, top + d * step);
                Heatmap.Children.Add(r);
                if (d == 0 && date.Month != lastMonth)
                {
                    lastMonth = date.Month;
                    if (w < weeks - 2)
                    {
                        var m = new TextBlock { Text = date.ToString("MMM"), FontSize = 11, Foreground = muted };
                        Canvas.SetLeft(m, left + w * step);
                        Canvas.SetTop(m, 0);
                        Heatmap.Children.Add(m);
                    }
                }
            }
        }
        foreach (var (d, label) in new[] { (0, "Mon"), (2, "Wed"), (4, "Fri") })
        {
            var t = new TextBlock { Text = label, FontSize = 11, Foreground = muted };
            Canvas.SetLeft(t, 0);
            Canvas.SetTop(t, top + d * step - 1);
            Heatmap.Children.Add(t);
        }
        Heatmap.Width = left + weeks * step;
        Heatmap.Height = top + 7 * step;

        HeatLegend.Children.Add(new TextBlock { Text = "Less", FontSize = 11, Foreground = muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var b in levels)
            HeatLegend.Children.Add(new Rectangle { Width = 11, Height = 11, RadiusX = 2, RadiusY = 2, Fill = b, Margin = new Thickness(2, 0, 2, 0) });
        HeatLegend.Children.Add(new TextBlock { Text = "More", FontSize = 11, Foreground = muted, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var yearAgo = today.AddDays(-365);
        var inYear = counts.Where(kv => kv.Key > yearAgo).ToList();
        int total = inYear.Sum(kv => kv.Value);
        int best = 0, run = 0;
        for (var day = yearAgo; day <= today; day = day.AddDays(1))
        {
            run = counts.ContainsKey(day) ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        var top1 = inYear.OrderByDescending(kv => kv.Value).FirstOrDefault();
        ActivitySub.Text = total == 0
            ? "No achievements in the last 12 months yet."
            : $"{total} unlocks in the last 12 months  ·  {inYear.Count} active days  ·  longest streak {best} day{(best == 1 ? "" : "s")}"
              + $"  ·  best day {top1.Key:dd.MM.yyyy} ({top1.Value})";
    }

    // =============== achievements page ===============

    void BuildAchievements()
    {
        var keep = AchGame.SelectedItem as string;
        var names = unlocked.Select(a => a.Game).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        AchGame.ItemsSource = new[] { AllGames }.Concat(names).ToList();
        AchGame.SelectedItem = keep != null && names.Contains(keep) ? keep : AllGames;
        AchSub.Text = $"{unlocked.Count:N0} unlocked across {names.Count} games";
        FillAchList();
    }

    void FillAchList()
    {
        var q = (AchSearch.Text ?? "").Trim().ToLowerInvariant();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var game = AchGame.SelectedItem as string;
        int rarity = AchRarity.SelectedIndex;
        var rows = new List<object>();
        string? group = null;
        foreach (var a in unlocked)
        {
            if (game != null && game != AllGames && a.Game != game) continue;
            if (rarity == 1 && a.A.Rarity is not < 15) continue;
            if (rarity == 2 && a.A.Rarity is not < 5) continue;
            if (words.Length > 0 && !words.All(a.SearchText.Contains)) continue;
            if (a.Group != group) { group = a.Group; rows.Add(new HeaderRow(group)); }
            rows.Add(a);
        }
        AchList.ItemsSource = rows;
        AchEmpty.IsVisible = rows.Count == 0;
        AchEmpty.Text = unlocked.Count == 0
            ? "No unlocked achievements found yet.\nThey show up here as soon as you unlock one in a Steam game."
            : "Nothing matches your search.";
    }

    // =============== games page ===============

    void BuildGames()
    {
        int perfect = games.Count(g => g.G.Perfect);
        GamesSub.Text = $"{games.Count} games  ·  {perfect} perfect  ·  {games.Sum(g => g.G.PlaytimeMinutes) / 60.0:N0} hours played";
        FillGamesGrid();
    }

    void FillGamesGrid()
    {
        var q = (GameSearch.Text ?? "").Trim().ToLowerInvariant();
        IEnumerable<GameVM> list = games.Where(g => q.Length == 0 || g.Name.ToLowerInvariant().Contains(q));
        list = GameSort.SelectedIndex switch
        {
            1 => list.OrderByDescending(g => g.G.PlaytimeMinutes),
            2 => list.OrderByDescending(g => g.G.Total > 0).ThenByDescending(g => g.G.Completion).ThenByDescending(g => g.G.UnlockedCount),
            3 => list.OrderByDescending(g => g.G.UnlockedCount),
            4 => list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderByDescending(g => g.G.LastPlayed),
        };
        GamesGrid.ItemsSource = list.ToList();
    }

    // =============== one game ===============

    void OpenGame_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is GameVM vm) OpenGame(vm);
    }

    void OpenGame(GameVM vm)
    {
        pageBeforeGame = VisiblePage() is Control p && p != GamePage ? p : pageBeforeGame ?? GamesPage;
        ChipAll.IsChecked = true;
        FillGamePage(vm, animate: true);
        ShowPage(GamePage);
    }

    void FillGamePage(GameVM vm, bool animate)
    {
        currentGame = vm;
        var g = vm.G;
        GameHero.Source = Img.File(g.Hero ?? g.Header ?? g.Portrait, 1800);
        GameLogo.Source = Img.File(g.Logo, 800);
        GameTitle.Text = vm.Name;
        GameTitle.IsVisible = GameLogo.Source == null;

        GameStats.Children.Clear();
        GameStats.Children.Add(MiniStat("Playtime", g.PlaytimeMinutes > 0 ? Fmt.Hours(g.PlaytimeMinutes) : "–"));
        GameStats.Children.Add(MiniStat("Last played", g.LastPlayed > 0 ? Fmt.Ago(g.LastPlayed) : "never"));
        if (g.Playtime2WeeksMinutes > 0) GameStats.Children.Add(MiniStat("Last 2 weeks", Fmt.Hours(g.Playtime2WeeksMinutes)));
        if (g.Total > 0)
        {
            GameStats.Children.Add(MiniStat("Achievements", $"{g.UnlockedCount} / {g.Total}"));
            GameStats.Children.Add(MiniStat("Completion", $"{g.Completion * 100:0}%", g.Perfect ? Fmt.Gold : null));
            var rare = g.Achievements.Where(a => a.Unlocked && a.Rarity != null).OrderBy(a => a.Rarity).FirstOrDefault();
            if (rare != null) GameStats.Children.Add(MiniStat("Rarest unlock", $"{rare.Rarity:0.0}%", Fmt.Gold));
        }
        if (g.Installed) GameStats.Children.Add(MiniStat("On disk", Fmt.Size(g.SizeOnDisk)));
        var mine = sessions.Where(s => s.AppId == g.AppId).ToList();
        if (mine.Count > 0)
        {
            GameStats.Children.Add(MiniStat("Sessions", mine.Count.ToString()));
            GameStats.Children.Add(MiniStat("Longest session", Fmt.Hours(Math.Max(1, mine.Max(s => s.Minutes)))));
        }

        bool hasAch = g.Total > 0;
        GameBar.IsVisible = GameChips.IsVisible = GameAchList.IsVisible = hasAch;
        GameNoAch.IsVisible = !hasAch;
        GameBar.Foreground = g.Perfect ? Fmt.Gold : Palette.Brush;
        GamePlayText.Text = g.Installed ? "Play" : "Install";
        GamePlayIcon.Data = Icon(g.Installed ? "IconPlay" : "IconDownload");
        ToolTip.SetTip(GamePlay, g.Installed ? $"Start {vm.Name} in Steam" : $"Install {vm.Name} with Steam");
        FillSoundtrackPicker(g.AppId);
        if (animate)
        {
            GameBar.Value = 0;
            Dispatcher.UIThread.Post(() => GameBar.Value = g.Completion, DispatcherPriority.Background);
        }
        else GameBar.Value = g.Completion;

        gameRows = g.Achievements
            .OrderByDescending(a => a.Unlocked)
            .ThenByDescending(a => a.Unlocked ? a.Time ?? 0 : 0)
            .ThenByDescending(a => a.Rarity ?? -1)
            .Select(a => new AchVM(a, g) { ShowGame = false })
            .ToList();
        FillGameAchList();
    }

    void FillGameAchList()
    {
        if (currentGame == null) return;
        bool showUnlocked = ChipLocked.IsChecked != true, showLocked = ChipUnlocked.IsChecked != true;
        var rows = new List<object>();
        var un = gameRows.Where(a => a.A.Unlocked).ToList();
        var lo = gameRows.Where(a => !a.A.Unlocked).ToList();
        if (showUnlocked && un.Count > 0) { rows.Add(new HeaderRow($"Unlocked  ·  {un.Count}")); rows.AddRange(un); }
        if (showLocked && lo.Count > 0) { rows.Add(new HeaderRow($"Still to get  ·  {lo.Count}  ·  easiest first")); rows.AddRange(lo); }
        GameAchList.ItemsSource = rows;
    }

    static Border MiniStat(string label, string value, IBrush? color = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Res("Muted") });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = color ?? Res("Text"), Margin = new Thickness(0, 2, 0, 0) });
        var b = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 10, 22, 12),
            Margin = new Thickness(0, 0, 10, 10),
            Child = sp,
        };
        b.Classes.Add("card");
        return b;
    }

    void Back_Click(object? sender, RoutedEventArgs e) => GoBack();

    void GoBack()
    {
        currentGame = null;
        ShowPage(pageBeforeGame ?? GamesPage);
    }

    void ShowAllGames_Click(object? sender, RoutedEventArgs e) => NavGames.IsChecked = true;

    // =============== storage ===============

    void BuildStorage()
    {
        var installed = games.Where(g => g.G.Installed && g.G.SizeOnDisk > 0).OrderByDescending(g => g.G.SizeOnDisk).ToList();
        long max = installed.Count > 0 ? installed[0].G.SizeOnDisk : 1;
        foreach (var g in installed) g.SizeFraction = (double)g.G.SizeOnDisk / max;
        long total = installed.Sum(g => g.G.SizeOnDisk);
        var stale = installed.Where(g => g.Stale).ToList();
        StorageSub.Text = $"{installed.Count} installed games use {Fmt.Size(total)}"
            + (stale.Count > 0 ? $"  ·  {stale.Count} not played in 6+ months could free {Fmt.Size(stale.Sum(g => g.G.SizeOnDisk))}" : "");
        StorageList.ItemsSource = installed;
    }

    // =============== music (Spotify) ===============

    async Task RefreshMusic()
    {
        if (musicBusy) return;
        musicBusy = true;
        try
        {
            var (status, window) = await Task.Run(() => (Spotify.Status(), Spotify.WindowState()));
            ApplyMusic(status);
            SpotifyWindowButton.IsVisible = window != Spotify.SpotifyWindow.None;
            SpotifyWindowButtonText.Text = window == Spotify.SpotifyWindow.Showing ? "Hide Spotify" : "Show Spotify";
        }
        catch (Exception ex) { Program.LogError("music: " + ex.Message); }
        finally { musicBusy = false; }
    }

    void ApplyMusic(NowPlaying s)
    {
        MiniButtons.IsVisible = BigButtons.IsVisible = s.Running;
        MiniOpen.IsVisible = BigOpen.IsVisible = !s.Running;
        var icon = Icon(s.Playing ? "IconPause" : "IconPlay");
        MiniPlayIcon.Data = icon;
        BigPlayIcon.Data = icon;

        if (!s.Running)
        {
            MiniTitle.Text = "Spotify";
            MiniArtist.Text = "not open";
            BigState.Text = "SPOTIFY ISN'T OPEN";
            BigTitle.Text = "Open Spotify to play music";
            BigArtist.Text = "or click one of your playlists below";
            return;
        }
        if (s.Playing && s.Title.Length > 0) { lastTitle = s.Title; lastArtist = s.Artist; }
        MiniTitle.Text = lastTitle ?? (s.Playing ? "Playing" : "Paused");
        MiniArtist.Text = s.Playing ? lastArtist ?? "" : "paused";
        BigState.Text = s.Playing ? "NOW PLAYING" : "PAUSED";
        BigTitle.Text = lastTitle ?? "Spotify is ready";
        BigArtist.Text = lastArtist ?? "press play or pick a playlist";
    }

    async void SendMusic(Spotify.Command c)
    {
        await Task.Run(() => Spotify.Send(c));
        await Task.Delay(700);
        await RefreshMusic();
    }

    void PlayPause_Click(object? sender, RoutedEventArgs e) => SendMusic(Spotify.Command.PlayPause);
    void Next_Click(object? sender, RoutedEventArgs e) => SendMusic(Spotify.Command.Next);
    void Prev_Click(object? sender, RoutedEventArgs e) => SendMusic(Spotify.Command.Previous);

    async void OpenSpotify_Click(object? sender, RoutedEventArgs e)
    {
        MiniTitle.Text = "Starting Spotify…";
        BigTitle.Text = "Starting Spotify in the background…";
        await Spotify.OpenAppAsync();
        Activate();
        await RefreshMusic();
    }

    async void ToggleSpotifyWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (Spotify.WindowState() == Spotify.SpotifyWindow.Showing) Spotify.HideWindow();
        else Spotify.ShowWindow();
        await Task.Delay(300);
        await RefreshMusic();
    }

    void FillPlaylists()
    {
        playlists = PlaylistStore.Load().OrderByDescending(p => p.Added).Select(p => new PlaylistVM(p)).ToList();
        PlaylistGrid.ItemsSource = playlists;
        MusicEmpty.IsVisible = playlists.Count == 0;
    }

    async void AddPlaylist_Click(object? sender, RoutedEventArgs e) => await AddPlaylistFromBox();

    async Task AddPlaylistFromBox()
    {
        var text = PlaylistLink.Text ?? "";
        if (text.Trim().Length == 0) return;
        await AddPlaylist(text);
    }

    async Task AddPlaylist(string text)
    {
        AddHint.Text = "Looking it up on Spotify…";
        var p = await PlaylistStore.FromLink(text);
        if (p == null)
        {
            AddHint.Text = "That doesn't look like a Spotify link. In Spotify: right-click a playlist → Share → Copy link to playlist.";
            return;
        }
        var list = PlaylistStore.Load();
        list.RemoveAll(x => x.Kind == p.Kind && x.Id == p.Id);
        list.Add(p);
        PlaylistStore.Save(list);
        PlaylistLink.Text = "";
        AddHint.Text = $"Added \"{p.Title}\". Click it to play.";
        FillPlaylists();
    }

    async void PlayPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PlaylistVM vm) return;
        AddHint.Text = $"Starting \"{vm.Title}\" in Spotify…";
        await Spotify.PlayAsync(vm.P);
        Activate(); // keep Trophyline in front; Spotify stays minimized
        await RefreshMusic();
        AddHint.Text = Platform.Windows
            ? $"Playing \"{vm.Title}\" (Spotify stays minimized). If it kept playing something else, press play once."
            : $"Playing \"{vm.Title}\".";
    }

    void RemovePlaylist_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true; // don't also start it
        if ((sender as Control)?.DataContext is not PlaylistVM vm) return;
        var list = PlaylistStore.Load();
        list.RemoveAll(x => x.Kind == vm.P.Kind && x.Id == vm.P.Id);
        PlaylistStore.Save(list);
        try { File.Delete(PlaylistStore.CoverPath(vm.P)); } catch (IOException) { }
        AddHint.Text = $"Removed \"{vm.Title}\".";
        FillPlaylists();
    }

    // =============== play a game & its soundtrack ===============

    void PlayGame_Click(object? sender, RoutedEventArgs e)
    {
        if (currentGame == null) return;
        var g = currentGame.G;
        try { Platform.OpenUrl(g.Installed ? $"steam://rungameid/{g.AppId}" : $"steam://install/{g.AppId}"); }
        catch (Exception ex)
        {
            Program.LogError("play game: " + ex.Message);
            ToolTip.SetTip(GamePlay, "Couldn't reach Steam. Is it installed?");
        }
    }

    List<SavedPlaylist?> soundtrackChoices = [];
    string? soundtrackFor;
    bool fillingSoundtrack;

    void FillSoundtrackPicker(string appId)
    {
        fillingSoundtrack = true;
        try
        {
            soundtrackFor = appId;
            var saved = PlaylistStore.Load().OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
            soundtrackChoices = [null, .. saved];
            GameSoundtrack.ItemsSource = soundtrackChoices.Select(p => p == null ? "No soundtrack" : p.Title).ToList();
            var current = SoundtrackStore.Load().GetValueOrDefault(appId);
            int index = soundtrackChoices.FindIndex(p => p != null && $"{p.Kind}:{p.Id}" == current);
            GameSoundtrack.SelectedIndex = Math.Max(0, index);
            GameSoundtrack.IsEnabled = saved.Count > 0;
            SoundtrackHint.Text = saved.Count == 0
                ? "Add a playlist on the Music page first, then pick it here."
                : index > 0
                    ? (Program.TrackerRunning()
                        ? "Starts in the background whenever you launch this game."
                        : "Starts when you launch this game (needs the background tracker, which isn't running).")
                    : "Pick a playlist and it starts in the background whenever you launch this game.";
        }
        finally { fillingSoundtrack = false; }
    }

    void SoundtrackChanged()
    {
        if (fillingSoundtrack || soundtrackFor == null) return;
        int i = GameSoundtrack.SelectedIndex;
        if (i < 0 || i >= soundtrackChoices.Count) return;
        try { SoundtrackStore.Set(soundtrackFor, soundtrackChoices[i]); }
        catch (Exception ex) { Program.LogError("soundtrack save: " + ex.Message); }
        FillSoundtrackPicker(soundtrackFor);
    }

    // =============== settings ===============

    bool fillingSettings;

    void BuildSettings()
    {
        fillingSettings = true;
        try
        {
            var cfg = Program.Settings();
            PauseOnQuitSwitch.IsChecked = cfg.PauseSoundtrackOnQuit;
            PopupsSwitch.IsChecked = cfg.UnlockPopups;
            int n = SoundtrackStore.Load().Count;
            SoundtrackCount.Text = (n == 0 ? "No game has a soundtrack yet." : $"{n} game{(n == 1 ? " has" : "s have")} a soundtrack.")
                + (Program.TrackerRunning() ? "" : "  The background tracker isn't running, so soundtracks won't start.");

            if (AccentPicker.Children.Count == 0)
            {
                foreach (var (name, hex) in Palette.Accents)
                {
                    var rb = new RadioButton { Content = name, Tag = hex, GroupName = "accent", Background = Fmt.Brush(hex) };
                    rb.Classes.Add("swatch");
                    rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true && !fillingSettings) PickAccent((string)rb.Tag!); };
                    AccentPicker.Children.Add(rb);
                }
            }
            var currentHex = Palette.Accents.Any(a => a.Hex.Equals(cfg.Accent, StringComparison.OrdinalIgnoreCase)) ? cfg.Accent : Palette.Default;
            foreach (var rb in AccentPicker.Children.OfType<RadioButton>())
                rb.IsChecked = ((string)rb.Tag!).Equals(currentHex, StringComparison.OrdinalIgnoreCase);
        }
        finally { fillingSettings = false; }
    }

    void PickAccent(string hex)
    {
        var cfg = Program.Settings();
        cfg.Accent = hex;
        Program.SaveSettings(cfg);
        Palette.Apply(hex);
        if (data != null) Apply(data); // rebuild tiles, bars and the calendar in the new colour
    }

    void TestPopup_Click(object? sender, RoutedEventArgs e)
    {
        // Your latest unlock (or your rarest one) as a sample.
        var a = unlocked.FirstOrDefault(x => x.A.Rarity is < 10) ?? unlocked.FirstOrDefault();
        var item = a == null
            ? new PopupItem("0", "Trophyline", "Pop-ups are on", "This is how an unlock will look.", "", null)
            : new PopupItem(a.A.AppId, a.Game, a.Name, a.A.Description, IconFor(a), a.A.Rarity);
        Popup.Show([item]);
    }

    static string IconFor(AchVM a)
    {
        var u = new Unlock(null, "", a.A.AppId, a.Game, a.Name, "", a.A.Icon);
        return a.A.Icon.Length > 0 && File.Exists(IconCache.LocalPath(u)) ? IconCache.LocalPath(u) : "";
    }

    // =============== playtime (sessions) ===============

    void BuildPlaytime()
    {
        sessions = SessionLog.Load();
        var byId = games.GroupBy(g => g.G.AppId).ToDictionary(g => g.Key, g => g.First());
        var all = sessions.ToList();
        if (SessionLog.Current() is (string app, long start))
            all.Add(new Session(app, start, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int MinutesBetween(DateTime from, DateTime to)
        {
            long a = new DateTimeOffset(from).ToUnixTimeSeconds(), b = new DateTimeOffset(to).ToUnixTimeSeconds();
            return (int)(all.Sum(s => Math.Max(0, Math.Min(s.End, b) - Math.Max(s.Start, a))) / 60);
        }
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        int todayMin = MinutesBetween(today, today.AddDays(1));
        int weekMin = MinutesBetween(monday, today.AddDays(1));
        int monthMin = MinutesBetween(today.AddDays(-29), today.AddDays(1));
        var longest = all.OrderByDescending(s => s.End - s.Start).FirstOrDefault();

        PlaytimeSub.Text = sessions.Count == 0 && all.Count == 0
            ? "Trophyline writes down every play session from now on: when you start a game and when you stop."
            : $"Tracked since {Fmt.Local(all.Min(s => s.Start)):dd.MM.yyyy}  ·  {all.Count} sessions  ·  {Fmt.Hours(all.Sum(s => s.Minutes))} in total";

        PlaytimeTiles.Children.Clear();
        PlaytimeTiles.Children.Add(Tile("IconClock", todayMin > 0 ? Fmt.Hours(todayMin) : "0 h", "today", today.ToString("dddd"), Palette.Brush));
        PlaytimeTiles.Children.Add(Tile("IconChart", weekMin > 0 ? Fmt.Hours(weekMin) : "0 h", "this week", $"since Monday {monday:dd.MM}", Fmt.Purple));
        PlaytimeTiles.Children.Add(Tile("IconGame", monthMin > 0 ? Fmt.Hours(monthMin) : "0 h", "last 30 days",
            monthMin > 0 ? $"about {Fmt.Hours(monthMin / 30)} a day" : "nothing yet", Fmt.Green));
        PlaytimeTiles.Children.Add(longest != null
            ? Tile("IconBolt", Fmt.Hours(Math.Max(1, longest.Minutes)), "longest session",
                   $"{(byId.TryGetValue(longest.AppId, out var lg) ? lg.Name : "App " + longest.AppId)} · {Fmt.Local(longest.Start):dd.MM}", Fmt.Gold)
            : Tile("IconBolt", "–", "longest session", "play something!", Fmt.Gold));

        // bar chart: minutes per day, last 14 days
        PlayChart.Children.Clear();
        const int days = 14;
        const double barW = 34, gap = 22, chartH = 150, top = 20;
        var perDay = Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1))
                               .Select(d => (Day: d, Min: MinutesBetween(d, d.AddDays(1)))).ToList();
        int max = Math.Max(60, perDay.Max(p => p.Min));
        var muted = Res("Muted");
        for (int i = 0; i < days; i++)
        {
            var (day, min) = perDay[i];
            double x = i * (barW + gap);
            double h = min == 0 ? 3 : Math.Max(6, chartH * min / max);
            var bar = new Border
            {
                Width = barW, Height = h, CornerRadius = new CornerRadius(6, 6, 3, 3),
                Background = min == 0 ? Res("Line") : day == today ? Palette.Brush : Fmt.Tint(Palette.Brush, 0xB0),
            };
            ToolTip.SetTip(bar, $"{day:dddd dd.MM}:  {(min == 0 ? "no play" : Fmt.Hours(min))}");
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, top + chartH - h);
            PlayChart.Children.Add(bar);
            if (min > 0)
            {
                var v = new TextBlock { Text = min >= 60 ? $"{min / 60.0:0.#}h" : $"{min}m", FontSize = 11, Foreground = muted, Width = barW, TextAlignment = TextAlignment.Center };
                Canvas.SetLeft(v, x);
                Canvas.SetTop(v, top + chartH - h - 16);
                PlayChart.Children.Add(v);
            }
            var label = new TextBlock
            {
                Text = day == today ? "Today" : day.ToString("ddd"), FontSize = 11, Width = barW + gap, TextAlignment = TextAlignment.Center,
                Foreground = day == today ? Res("Text") : muted, FontWeight = day == today ? FontWeight.SemiBold : FontWeight.Normal,
            };
            Canvas.SetLeft(label, x - gap / 2);
            Canvas.SetTop(label, top + chartH + 6);
            PlayChart.Children.Add(label);
        }
        PlayChart.Width = days * (barW + gap);
        ChartTotal.Text = $"{Fmt.Hours(perDay.Sum(p => p.Min))} in 14 days";
        if (perDay.Sum(p => p.Min) == 0) ChartTotal.Text = "nothing yet";

        SessionList.ItemsSource = sessions.OrderByDescending(s => s.Start).Take(30)
            .Select(s => new SessionVM(s, byId.GetValueOrDefault(s.AppId))).ToList();
        SessionsEmpty.IsVisible = sessions.Count == 0;
        UpdateNowPlaying();
    }

    void UpdateNowPlaying()
    {
        if (SessionLog.Current() is not (string app, long start)) { NowPlayingCard.IsVisible = false; return; }
        var g = games.FirstOrDefault(x => x.G.AppId == app);
        NowPlayingCard.IsVisible = true;
        NowPlayingName.Text = g?.Name ?? $"App {app}";
        NowPlayingImage.Source = g?.HeaderImage;
        int min = (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - start) / 60);
        NowPlayingSub.Text = $"since {Fmt.Local(start):HH:mm}  ·  {(min < 1 ? "just started" : Fmt.Hours(min))} so far";
    }

    void OpenSessionGame_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is SessionVM { Game: GameVM g }) OpenGame(g);
    }

    // =============== level & XP ===============

    Xp.Result? xp;

    void OpenLevel_Click(object? sender, RoutedEventArgs e) => NavLevel.IsChecked = true;

    void BuildLevel(SteamData d)
    {
        var r = xp = Xp.Compute(d.Games, sessions);
        var (title, color) = Xp.RankFor(r.Level);
        var rankBrush = Fmt.Brush(color);
        double frac = (double)r.Into / r.Need;

        // sidebar
        LevelBadgeText.Text = r.Level.ToString();
        LevelBadge.Background = rankBrush;
        SideLevel.Text = $"Level {r.Level}  ·  {title}";
        SideXp.Text = $"{r.Into:N0} / {r.Need:N0} XP";
        SideXpBar.Value = frac;

        // Level page
        BigLevel.Text = r.Level.ToString();
        BigRing.Stroke = rankBrush;
        RankTitle.Text = title;
        RankTitle.Foreground = rankBrush;
        TotalXp.Text = $"{r.Total:N0} XP in total";
        XpToNext.Text = $"{r.Into:N0} / {r.Need:N0} XP  ·  {r.Need - r.Into:N0} to level {r.Level + 1}";
        NextRankText.Text = Xp.NextRank(r.Level) is (int lv, string next) ? $"Next rank: {next} at level {lv}" : "Highest rank reached!";
        if (LevelPage.IsVisible) BigXpBar.Value = frac;

        XpTiles.Children.Clear();
        string[] icons = ["IconStar", "IconGame", "IconStar", "IconClock", "IconChart"];
        IBrush[] colors = [Palette.Brush, Fmt.Green, Fmt.Gold, Fmt.Purple, Fmt.Orange];
        string[] units = ["unlocked", "games", "at 100%", "hours", "sessions"];
        for (int i = 0; i < r.Breakdown.Count; i++)
        {
            var (source, count, amount) = r.Breakdown[i];
            XpTiles.Children.Add(Tile(icons[i], $"{amount:N0}", source, $"{count:N0} {units[i]}", colors[i]));
        }

        RankRoad.Children.Clear();
        foreach (var (from, name, hex) in Xp.Ranks)
        {
            bool reached = r.Level >= from, current = Xp.RankFor(r.Level).Title == name;
            var c = Fmt.Brush(hex);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            sp.Children.Add(new PathIcon { Data = Icon("IconShield"), Width = 20, Height = 20, Foreground = reached ? c : Res("Line") });
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = reached ? Res("Text") : Res("Muted") });
            t.Children.Add(new TextBlock { Text = $"level {from}+", FontSize = 11, Foreground = Res("Muted") });
            sp.Children.Add(t);
            var chip = new Border
            {
                Child = sp, Padding = new Thickness(14, 9), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12),
                Background = current ? Fmt.Tint(c, 0x26) : Res("Card"),
                BorderBrush = current ? c : Res("Line"), BorderThickness = new Thickness(current ? 2 : 1),
                Opacity = reached ? 1 : 0.6,
            };
            RankRoad.Children.Add(chip);
        }

        RulesList.ItemsSource = Xp.Rules.Select(rule => XpRow(rule.What, null, rule.Worth, false)).ToList();
        RecentXpList.ItemsSource = r.Recent.Count == 0
            ? [XpRow("Nothing yet", "Unlock an achievement or play a game", "", false)]
            : r.Recent.Take(12).Select(g => XpRow(g.Text, $"{g.Game}  ·  {Fmt.Ago(g.Time)}", $"+{g.Amount} XP", true)).ToList();

        CheckLevelUp(r.Level, title);
    }

    Control XpRow(string text, string? sub, string worth, bool gain)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (sub != null) left.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        var right = new TextBlock
        {
            Text = worth, FontWeight = FontWeight.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = gain ? Palette.Brush : Res("Muted"), Margin = new Thickness(12, 0, 0, 0), TextAlignment = TextAlignment.Right,
        };
        var row = new DockPanel { Margin = new Thickness(12, 9) };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);
        return row;
    }

    void AnimateLevelBar()
    {
        if (xp == null) return;
        BigXpBar.Value = 0;
        Dispatcher.UIThread.Post(() => BigXpBar.Value = (double)xp.Into / xp.Need, DispatcherPriority.Background);
    }

    async void CheckLevelUp(int level, string title)
    {
        int seen = Xp.LastSeenLevel();
        if (seen == level) return;
        Xp.MarkSeen(level);
        if (seen == 0 || level < seen) return; // first time: no fanfare
        LevelUpText.Text = $"You reached level {level}  ·  {title}";
        LevelUpBanner.IsVisible = true;
        await FadeIn(LevelUpBanner);
        await Task.Delay(7000);
        LevelUpBanner.IsVisible = false;
    }

    // =============== navigation ===============

    Control[] AllPages => [OverviewPage, AchievementsPage, GamesPage, GamePage, LevelPage, PlaytimePage, StoragePage, MusicPage, SettingsPage];

    Control? VisiblePage() => AllPages.FirstOrDefault(p => p.IsVisible);

    void ShowPage(Control page)
    {
        if (page != GamePage) currentGame = null;
        foreach (var p in AllPages) p.IsVisible = p == page;
        var nav = page == OverviewPage ? NavOverview : page == AchievementsPage ? NavAchievements
                : page == StoragePage ? NavStorage : page == PlaytimePage ? NavPlaytime : page == LevelPage ? NavLevel : page == MusicPage ? NavMusic : page == SettingsPage ? NavSettings : NavGames;
        if (nav.IsChecked != true)
        {
            syncingNav = true;
            nav.IsChecked = true;
            syncingNav = false;
        }
        _ = FadeIn(page);
    }

    static Task FadeIn(Control page)
    {
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(220),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1d) } },
            },
        };
        return anim.RunAsync(page);
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.F && cmd)
        {
            var box = VisiblePage() == GamesPage ? GameSearch : AchSearch;
            if (box == AchSearch) NavAchievements.IsChecked = true;
            box.Focus();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (VisiblePage() == GamePage) { GoBack(); e.Handled = true; }
            else if (FocusManager?.GetFocusedElement() is TextBox tb && !string.IsNullOrEmpty(tb.Text)) { tb.Text = ""; e.Handled = true; }
        }
        else if (e.Key == Key.Back && VisiblePage() == GamePage && FocusManager?.GetFocusedElement() is not TextBox)
        {
            GoBack();
            e.Handled = true;
        }
    }

    // =============== --snapshot support ===============

    internal void NavigateForSnapshot(string page, string? arg)
    {
        switch (page)
        {
            case "achievements": NavAchievements.IsChecked = true; AchSearch.Text = arg ?? ""; FillAchList(); break;
            case "games": NavGames.IsChecked = true; if (arg != null) GameSort.SelectedIndex = int.Parse(arg); break;
            case "storage": NavStorage.IsChecked = true; break;
            case "playtime": NavPlaytime.IsChecked = true; break;
            case "level": NavLevel.IsChecked = true; break;
            case "settings":
                NavSettings.IsChecked = true;
                if (arg != null) PickAccent(arg);
                BuildSettings();
                break;
            case "music":
                NavMusic.IsChecked = true;
                if (arg != null) _ = AddPlaylist(arg);
                break;
            case "game":
                var vm = games.FirstOrDefault(g => g.G.AppId == arg) ?? games.OrderByDescending(g => g.G.LastPlayed).First();
                OpenGame(vm);
                break;
            default: NavOverview.IsChecked = true; break;
        }
    }

    internal string DescribeView() =>
        $"page={VisiblePage()?.Name} games={games.Count} unlocked={unlocked.Count} " +
        $"achRows={(AchList.ItemsSource as System.Collections.IList)?.Count} gameRows={(GameAchList.ItemsSource as System.Collections.IList)?.Count} " +
        $"storage={(StorageList.ItemsSource as System.Collections.IList)?.Count} os={Environment.OSVersion.Platform} " +
        $"playlists={playlists.Count} music='{MiniTitle.Text} / {MiniArtist.Text}'";
}
