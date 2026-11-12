using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
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
        NavLeaderboard.IsCheckedChanged += async (_, _) => { if (NavLeaderboard.IsChecked == true && !syncingNav) { ShowPage(LeaderboardPage); await LoadLeaderboard(); } };
        NavShop.IsCheckedChanged += (_, _) => { if (NavShop.IsChecked == true && !syncingNav) { BuildShop(); ShowPage(ShopPage); } };
        NavChat.IsCheckedChanged += async (_, _) => { if (NavChat.IsChecked == true && !syncingNav) { ShowPage(ChatPage); await RefreshChat(full: true); } };
        chatTimer.Tick += async (_, _) => { if (ChatPage.IsVisible) await RefreshChat(full: false); };
        ChatInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendChat(); } };
        foreach (var (rb, key) in new[] { (BoardSeason, "season"), (BoardXp, "xp"), (BoardPerfect, "perfect"), (BoardOver75, "over75") })
            rb.IsCheckedChanged += async (_, _) => { if (rb.IsChecked == true) { board = key; await LoadLeaderboard(); } };
        SeasonPick.SelectionChanged += async (_, _) => { if (!fillingSeasons) await LoadLeaderboard(); };
        OnlineSwitch.IsCheckedChanged += async (_, _) =>
        {
            if (fillingSettings) return;
            if (OnlineSwitch.IsChecked == true)
            {
                var name = (OnlineNameBox.Text ?? "").Trim();
                if (name.Length == 0) name = data?.PersonaName ?? "Player";
                Online.Enable(name);
                OnlineNameBox.Text = name;
                await UploadNow();
            }
            else
            {
                var err = await Online.Disable();
                OnlineStatus.Text = err == null ? "You're offline. Your entry was removed from the server." : "Turned off, but couldn't reach the server: " + err;
            }
        };
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
            OnlineSwitch.IsChecked = cfg.OnlineEnabled;
            OnlineSwitch.IsEnabled = Online.Available;
            OnlineNameBox.Text = cfg.OnlineName.Length > 0 ? cfg.OnlineName : data?.PersonaName ?? "";
            BuildLinkBoxes();
            ShowMyPicture();
            OnlineStatus.Text = !Online.Available ? "The Trophyline server isn't set up yet, so this can't be turned on."
                : cfg.OnlineEnabled ? $"You're on the leaderboards as \"{cfg.OnlineName}\"." : "You're not on the leaderboards.";
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
        UpdateAvatar();
        if (Online.Enabled) _ = UploadNow(force: false);
    }

    // =============== online: upload, leaderboards, profiles ===============

    string board = "season";
    bool fillingSeasons;
    List<string> seasonIds = [];

    async Task UploadNow(bool force = true)
    {
        if (data == null || xp == null) return;
        var err = await Online.Upload(data.Games, xp, force);
        if (err == null && Online.Enabled) await SyncPicture();
        if (OnlineStatus != null)
            OnlineStatus.Text = err == null
                ? (Online.Enabled ? $"You're on the leaderboards as \"{Program.Settings().OnlineName}\"." : OnlineStatus.Text)
                : "Couldn't reach the server: " + err;
    }

    async void Join_Click(object? sender, RoutedEventArgs e)
    {
        var name = (JoinName.Text ?? "").Trim();
        if (name.Length == 0) { JoinHint.Text = "Type a name first."; return; }
        Online.Enable(name);
        JoinHint.Text = "Joining…";
        await UploadNow();
        await LoadLeaderboard();
    }

    async void SaveOnlineName_Click(object? sender, RoutedEventArgs e)
    {
        var name = (OnlineNameBox.Text ?? "").Trim();
        if (name.Length == 0) return;
        var cfg = Program.Settings();
        cfg.OnlineName = name;
        Program.SaveSettings(cfg);
        if (Online.Enabled) await UploadNow();
        else OnlineStatus.Text = "Name saved. Turn the switch on to join.";
    }

    async void LbRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (Online.Enabled) await UploadNow();
        await LoadLeaderboard();
    }

    static long L(JsonNode? n)
    {
        if (n == null) return 0;
        var s = n.ToJsonString();
        return long.TryParse(s, out var v) ? v
             : double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
    }

    static string S(JsonNode? n) => n == null ? "" : n.GetValueKind() == System.Text.Json.JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();

    string BoardValue(long v) => board switch
    {
        "perfect" => $"{v:N0} {(v == 1 ? "game" : "games")} at 100%",
        "over75" => $"{v:N0} {(v == 1 ? "game" : "games")}",
        _ => $"{v:N0} XP",
    };

    async Task LoadLeaderboard()
    {
        var season = Season.Current;
        SeasonKicker.Text = $"{season.Name.ToUpperInvariant()}  ·  {season.Label.ToUpperInvariant()}";
        SeasonTitle.Text = season.Label;
        var start = DateTimeOffset.FromUnixTimeSeconds(season.Start).LocalDateTime;
        var end = DateTimeOffset.FromUnixTimeSeconds(season.End).LocalDateTime.AddDays(-1);
        SeasonSub.Text = $"{start:dd.MM.yyyy} – {end:dd.MM.yyyy}  ·  {season.DaysLeft} days left. Every season lasts 3 months; season XP starts at 0 each time.";
        SeasonBar.Value = Math.Clamp((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - season.Start) / (double)(season.End - season.Start), 0, 1);
        MySeasonXp.Text = xp != null ? $"{xp.SeasonXp(season):N0}" : "–";
        MySeasonPlace.Text = Online.Enabled ? "" : "not on the leaderboards";

        SeasonPick.IsVisible = board == "season";
        if (!Online.Available)
        {
            LbSub.Text = "Online leaderboards need the Trophyline server, which isn't set up yet.";
            JoinCard.IsVisible = false;
            LbRows.Children.Clear();
            LbEmpty.IsVisible = true;
            LbEmpty.Text = "Once the server is online, you'll see everyone's season XP, highest XP, completed games and games over 75% here.";
            return;
        }
        JoinCard.IsVisible = !Online.Enabled;
        if (string.IsNullOrWhiteSpace(JoinName.Text)) JoinName.Text = data?.PersonaName ?? "";

        // the season picker and the champions list
        if (seasonIds.Count == 0 && await Online.Seasons() is JsonNode sn)
        {
            fillingSeasons = true;
            var list = sn["seasons"]?.AsArray() ?? [];
            seasonIds = list.Select(s => S(s?["id"])).ToList();
            SeasonPick.ItemsSource = list.Select(s => $"{S(s?["name"])} · {S(s?["label"])}" + (s?["current"]?.GetValue<bool>() == true ? " (now)" : "")).ToList();
            SeasonPick.SelectedIndex = 0;
            fillingSeasons = false;
            BuildSeasonHistory(list);
        }
        var seasonId = board == "season" && SeasonPick.SelectedIndex >= 0 && SeasonPick.SelectedIndex < seasonIds.Count ? seasonIds[SeasonPick.SelectedIndex] : null;

        LbSub.Text = "Loading…";
        var res = await Online.Leaderboard(board, seasonId);
        if (res == null)
        {
            LbSub.Text = "Couldn't reach the Trophyline server. Check your internet connection and press Refresh.";
            return;
        }
        var rows = res["rows"]?.AsArray() ?? [];
        LbSub.Text = $"{L(res["players"]):N0} players on Trophyline  ·  click someone to see their profile";
        var me = Online.MyId;
        LbRows.Children.Clear();
        foreach (var r in rows)
            LbRows.Children.Add(LeaderRow((int)L(r?["pos"]), S(r?["id"]), S(r?["name"]), (int)L(r?["level"]), S(r?["rank"]), S(r?["icon"]), L(r?["value"]), S(r?["id"]) == me, L(r?["avatar"])));
        if (res["me"] is JsonNode mine && !rows.Any(r => S(r?["id"]) == me))
        {
            LbRows.Children.Add(new TextBlock { Text = "…", Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center });
            LbRows.Children.Add(LeaderRow((int)L(mine["pos"]), me, Program.Settings().OnlineName, xp?.Level ?? 1, Xp.RankFor(xp?.Level ?? 1).Title,
                                          Shop.Load().Equipped, L(mine["value"]), true, 1));
        }
        if (board == "season" && (seasonId == null || seasonId == season.Id))
            MySeasonPlace.Text = res["me"] is JsonNode m2 ? $"#{L(m2["pos"])} this season" : Online.Enabled ? "earn XP to get on the board" : "not on the leaderboards";
        LbEmpty.IsVisible = rows.Count == 0;
        LbEmpty.Text = "Nobody here yet. Be the first!";
    }

    void BuildSeasonHistory(JsonArray seasons)
    {
        SeasonHistory.Children.Clear();
        foreach (var s in seasons)
        {
            var sp = new StackPanel { Spacing = 6 };
            sp.Children.Add(new TextBlock { Text = $"{S(s?["name"])}  ·  {S(s?["label"])}" + (s?["current"]?.GetValue<bool>() == true ? "  (now)" : ""), FontWeight = FontWeight.SemiBold });
            var top = s?["top"]?.AsArray() ?? [];
            if (top.Count == 0) sp.Children.Add(new TextBlock { Text = "no champions yet", Foreground = Res("Muted"), FontSize = 12 });
            string[] medals = ["#F5C451", "#C9D6E3", "#D08A5B"];
            for (int i = 0; i < top.Count; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = $"{i + 1}.", Foreground = Fmt.Brush(medals[i]), FontWeight = FontWeight.Bold, Width = 18 });
                row.Children.Add(new TextBlock { Text = S(top[i]?["name"]), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = $"{L(top[i]?["value"]):N0} XP", Foreground = Res("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(row);
            }
            var card = new Border { Child = sp, Padding = new Thickness(16, 12), Margin = new Thickness(0, 0, 12, 12), Width = 290 };
            card.Classes.Add("card");
            SeasonHistory.Children.Add(card);
        }
    }

    Control LeaderRow(int pos, string id, string name, int level, string rank, string icon, long value, bool isMe, long pictureVer = 0)
    {
        string[] medals = ["#F5C451", "#C9D6E3", "#D08A5B"];
        var posText = new TextBlock
        {
            Text = $"#{pos}", FontSize = pos <= 3 ? 20 : 16, FontWeight = FontWeight.Bold, Width = 56, VerticalAlignment = VerticalAlignment.Center,
            Foreground = pos <= 3 ? Fmt.Brush(medals[pos - 1]) : Res("Muted"),
        };
        var (rankTitle, rankColor) = Xp.RankFor(level);
        rank = rankTitle; // always from the level, whatever the server says
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0) };
        who.Children.Add(new TextBlock { Text = name + (isMe ? "  (you)" : ""), FontWeight = FontWeight.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        who.Children.Add(new TextBlock { Text = $"Level {level}  ·  {rank}", FontSize = 12, Foreground = Fmt.Brush(rankColor) });
        var val = new TextBlock { Text = BoardValue(value), FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var avatar = Avatar(icon, name, 42, Fmt.Brush(rankColor), id, pictureVer);
        Grid.SetColumn(avatar, 1); Grid.SetColumn(who, 2); Grid.SetColumn(val, 3);
        grid.Children.Add(posText); grid.Children.Add(avatar); grid.Children.Add(who); grid.Children.Add(val);
        var card = new Border { Child = grid, Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(12) };
        card.Classes.Add("card"); card.Classes.Add("row");
        if (isMe) { card.BorderBrush = Palette.Brush; card.BorderThickness = new Thickness(2); }
        var b = new Button { Content = card, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        b.Classes.Add("plain");
        b.Click += (_, _) => OpenPlayer(id);
        return b;
    }

    /// A round profile picture: their uploaded picture, else the equipped shop icon, else the first letter of the name.
    static Control Avatar(string? iconId, string name, double size, IBrush ring, string? playerId = null, long pictureVer = 0)
    {
        var icon = Shop.Find(iconId);
        Control inner = icon != null
            ? new TextBlock { Text = icon.Glyph, FontSize = size * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock
            {
                Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?", FontSize = size * 0.42, FontWeight = FontWeight.Bold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        var border = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = icon != null ? Fmt.Tint(Fmt.Brush(icon.Color), 0x30) : Res("AccentAvatar"),
            BorderBrush = icon != null ? Fmt.Brush(icon.Color) : ring, BorderThickness = new Thickness(Math.Max(2, size / 22)),
            Child = inner,
        };
        if (playerId != null)
        {
            // the picture replaces the icon once it's loaded (cached after the first time)
            Avatars.Get(playerId, pictureVer).ContinueWith(t =>
            {
                if (t.Result is Bitmap bmp)
                    Dispatcher.UIThread.Post(() =>
                    {
                        border.Child = null;
                        border.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                        border.BorderBrush = ring;
                    });
            });
        }
        return border;
    }

    void UpdateAvatar()
    {
        var icon = Shop.Find(Shop.Load().Equipped);
        var name = data?.PersonaName ?? "?";
        AvatarLetter.Text = icon?.Glyph ?? (name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?");
        AvatarLetter.FontSize = icon != null ? 21 : 17;
        var pic = Avatars.Mine();
        SidePic.IsVisible = pic != null;
        SidePic.Fill = pic != null ? new ImageBrush(pic) { Stretch = Stretch.UniformToFill } : null;
    }

    // ----- your profile picture (Settings) -----

    void ShowMyPicture()
    {
        MyPicturePreview.Children.Clear();
        MyPicturePreview.Children.Add(Avatar(Shop.Load().Equipped, data?.PersonaName ?? "?", 72, Palette.Brush, Online.MyId.Length > 0 ? Online.MyId : "me", 1));
        RemovePictureButton.IsVisible = Avatars.HasMine;
    }

    async void ChoosePicture_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a profile picture",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.gif"] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not string path) return;
        var jpeg = await Task.Run(() => { try { return Avatars.Prepare(path); } catch { return null; } });
        if (jpeg == null) { PictureStatus.Text = "Couldn't read that picture. Try a PNG or JPG."; return; }
        Avatars.SaveMine(jpeg);
        await SyncPicture();
    }

    async void RemovePicture_Click(object? sender, RoutedEventArgs e)
    {
        Avatars.SaveMine(null);
        await SyncPicture();
    }

    /// Shows your picture everywhere and uploads it if it changed.
    async Task SyncPicture()
    {
        UpdateAvatar();
        ShowMyPicture();
        if (!Online.Enabled) { PictureStatus.Text = Avatars.HasMine ? "Saved. Others see it once you join the leaderboards." : "Removed."; return; }
        var bytes = Avatars.HasMine ? await File.ReadAllBytesAsync(Avatars.MinePath) : null;
        var hash = bytes == null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var cfg = Program.Settings();
        if (cfg.AvatarHash == hash) { PictureStatus.Text = ""; return; }
        PictureStatus.Text = "Uploading…";
        var err = await Online.SetAvatar(bytes);
        if (err != null) { PictureStatus.Text = err; return; }
        cfg = Program.Settings();
        cfg.AvatarHash = hash;
        Program.SaveSettings(cfg);
        PictureStatus.Text = bytes == null ? "Removed from your profile." : "Uploaded: it's on your profile now.";
    }

    // ----- someone's profile -----

    void PlayerBack_Click(object? sender, RoutedEventArgs e) => NavLeaderboard.IsChecked = true;

    async void OpenPlayer(string id)
    {
        ShowPage(PlayerPage);
        PlayerBody.Children.Clear();
        PlayerBody.Children.Add(new TextBlock { Text = "Loading profile…", Foreground = Res("Muted") });
        var p = await Online.Player(id);
        PlayerBody.Children.Clear();
        if (p == null) { PlayerBody.Children.Add(new TextBlock { Text = "Couldn't load this profile.", Foreground = Res("Muted") }); return; }

        var name = S(p["name"]);
        int level = (int)L(p["level"]);
        var (rank, rankHex) = Xp.RankFor(level);
        var prof = p["profile"];

        // header
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        head.Children.Add(Avatar(S(prof?["icon"]), name, 96, Fmt.Brush(rankHex), id, L(p["avatar"])));
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(new TextBlock { Text = name + (id == Online.MyId ? "  (you)" : ""), FontSize = 32, FontWeight = FontWeight.Bold });
        ht.Children.Add(new TextBlock { Text = $"Level {level}  ·  {rank}", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Fmt.Brush(rankHex) });
        ht.Children.Add(new TextBlock
        {
            Text = $"On Trophyline since {Fmt.Local(L(p["created"])):dd.MM.yyyy}  ·  updated {Fmt.Ago(L(p["updated"]))}",
            Foreground = Res("Muted"), FontSize = 13, Margin = new Thickness(0, 4, 0, 0),
        });
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (id != Online.MyId)
        {
            var msg = new Button { Content = new TextBlock { Text = "Message" }, Margin = new Thickness(0, 0, 8, 8) };
            msg.Classes.Add("pill");
            msg.Click += async (_, _) => await OpenChat(id, name);
            actions.Children.Add(msg);
        }
        // their links (usernames only; the web address is built here, never taken from the server)
        var links = prof?["links"] as JsonObject;
        foreach (var n in Social.Networks)
        {
            var handle = S(links?[n.Key]);
            if (handle.Length == 0 || !n.Valid.IsMatch(handle)) continue;
            actions.Children.Add(LinkChip(n, handle));
        }
        if (id == Online.MyId)
        {
            var edit = new Button { Content = new TextBlock { Text = "Edit my links" }, Margin = new Thickness(0, 0, 8, 8) };
            edit.Classes.Add("pill");
            edit.Click += (_, _) => NavSettings.IsChecked = true;
            actions.Children.Add(edit);
        }
        if (actions.Children.Count > 0) ht.Children.Add(actions);
        head.Children.Add(ht);
        PlayerBody.Children.Add(head);

        var places = p["places"];
        var season = p["season"];
        var tiles = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(-8, 18, -8, 0) };
        tiles.Children.Add(Tile("IconShield", $"{L(p["xp"]):N0}", "total XP", $"#{L(places?["xp"])} on the XP board", Palette.Brush));
        tiles.Children.Add(Tile("IconPodium", $"{L(season?["xp"]):N0}", "season XP", $"{S(season?["name"])} · {S(season?["label"])}", Fmt.Purple));
        tiles.Children.Add(Tile("IconStar", $"{L(p["perfect"])}", "completed games", $"#{L(places?["perfect"])} on the completion board", Fmt.Gold));
        tiles.Children.Add(Tile("IconChart", $"{L(p["over75"])}", "games over 75%", $"#{L(places?["over75"])} on that board", Fmt.Green));
        tiles.Children.Add(Tile("IconStar", $"{L(p["unlocked"]):N0}", "achievements", $"in {L(p["games"])} games", Palette.Brush));
        tiles.Children.Add(Tile("IconClock", $"{L(p["hours"]):N0} h", "played", "on Steam", Fmt.Orange));
        PlayerBody.Children.Add(tiles);

        // top games
        var topGames = prof?["topGames"]?.AsArray() ?? [];
        if (topGames.Count > 0)
        {
            PlayerBody.Children.Add(new TextBlock { Text = "Best games", FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 22, 0, 12) });
            var wrap = new WrapPanel();
            foreach (var g in topGames)
            {
                var appId = S(g?["appId"]);
                long un = L(g?["unlocked"]), tot = Math.Max(1, L(g?["total"]));
                var img = new Image { Stretch = Stretch.UniformToFill };
                _ = LoadHeader(img, appId);
                var sp = new StackPanel();
                sp.Children.Add(new Border { Height = 98, ClipToBounds = true, Background = Res("CardHi"), Child = img, CornerRadius = new CornerRadius(10, 10, 0, 0) });
                var info = new StackPanel { Margin = new Thickness(12, 8, 12, 12), Spacing = 4 };
                info.Children.Add(new TextBlock { Text = S(g?["name"]), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = $"{un} / {tot}  ·  {100.0 * un / tot:0}%", FontSize = 12, Foreground = un == tot ? Fmt.Gold : Res("Muted") });
                var bar = new ProgressBar { Value = (double)un / tot, Background = Res("Line"), Foreground = un == tot ? Fmt.Gold : Palette.Brush };
                bar.Classes.Add("thin");
                info.Children.Add(bar);
                sp.Children.Add(info);
                var card = new Border { Child = sp, Width = 210, Margin = new Thickness(0, 0, 14, 14), Padding = new Thickness(0), ClipToBounds = true };
                card.Classes.Add("card");
                wrap.Children.Add(card);
            }
            PlayerBody.Children.Add(wrap);
        }

        // latest unlocks
        var recent = prof?["recent"]?.AsArray() ?? [];
        if (recent.Count > 0)
        {
            PlayerBody.Children.Add(new TextBlock { Text = "Latest unlocks", FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 12) });
            foreach (var a in recent)
            {
                var appId = S(a?["appId"]);
                var iconName = S(a?["icon"]);
                var img = new Image { Stretch = Stretch.UniformToFill };
                if (iconName.Length > 0)
                {
                    var u = new Unlock(null, "", appId, "", "", "", iconName);
                    _ = Task.Run(async () => await Img.Cached(IconCache.RemoteUrl(u), IconCache.LocalPath(u), 96))
                        .ContinueWith(t => Dispatcher.UIThread.Post(() => img.Source = t.Result));
                }
                double? rarity = a?["rarity"] is JsonNode rn && rn.GetValueKind() == System.Text.Json.JsonValueKind.Number ? rn.GetValue<double>() : null;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                row.Children.Add(new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Res("Line"), Child = img });
                var t2 = new StackPanel { Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center };
                t2.Children.Add(new TextBlock { Text = S(a?["name"]), FontWeight = FontWeight.SemiBold });
                t2.Children.Add(new TextBlock { Text = $"{S(a?["game"])}  ·  {Fmt.Ago(L(a?["time"]))}", FontSize = 12, Foreground = Res("Muted") });
                Grid.SetColumn(t2, 1);
                row.Children.Add(t2);
                var right = new TextBlock
                {
                    Text = (rarity is double r ? $"{r:0.#}%  ·  " : "") + $"+{Xp.ForAchievement(rarity)} XP",
                    FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = rarity is < 10 ? Fmt.Gold : Palette.Brush,
                };
                Grid.SetColumn(right, 2);
                row.Children.Add(right);
                var card = new Border { Child = row, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12) };
                card.Classes.Add("card");
                PlayerBody.Children.Add(card);
            }
        }
    }

    Control LinkChip(SocialNetwork n, string handle)
    {
        var color = Fmt.Brush(n.Color);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = color, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = n.Label, FontWeight = FontWeight.SemiBold });
        row.Children.Add(new TextBlock { Text = (n.Key == "discord" ? "" : "@") + handle, Foreground = Res("Muted"), MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Button { Content = row, Margin = new Thickness(0, 0, 8, 8) };
        b.Classes.Add("pill");
        var url = Social.Url(n, handle);
        ToolTip.SetTip(b, url ?? "Click to copy the Discord username");
        b.Click += async (_, _) =>
        {
            if (url != null) { try { Platform.OpenUrl(url); } catch (Exception ex) { Program.LogError("open link: " + ex.Message); } return; }
            if (Clipboard is { } cb) await cb.SetTextAsync(handle);
            ((TextBlock)row.Children[2]).Text = "copied!";
            await Task.Delay(1500);
            ((TextBlock)row.Children[2]).Text = handle;
        };
        return b;
    }

    // ----- your own links (Settings) -----

    readonly Dictionary<string, TextBox> linkBoxes = [];

    void BuildLinkBoxes()
    {
        var cfg = Program.Settings();
        if (LinkBoxes.Children.Count == 0)
            foreach (var n in Social.Networks)
            {
                var sp = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 12, 10), Spacing = 4 };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                head.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Fmt.Brush(n.Color), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Text = n.Label, FontSize = 12, FontWeight = FontWeight.SemiBold });
                sp.Children.Add(head);
                var box = new TextBox { Watermark = n.Placeholder, MaxLength = 120 };
                box.Classes.Add("search");
                sp.Children.Add(box);
                linkBoxes[n.Key] = box;
                LinkBoxes.Children.Add(sp);
            }
        foreach (var n in Social.Networks)
            linkBoxes[n.Key].Text = cfg.Links.GetValueOrDefault(n.Key, "");
    }

    async void SaveLinks_Click(object? sender, RoutedEventArgs e)
    {
        var cfg = Program.Settings();
        var bad = new List<string>();
        cfg.Links.Clear();
        foreach (var n in Social.Networks)
        {
            var text = linkBoxes[n.Key].Text ?? "";
            if (text.Trim().Length == 0) continue;
            if (Social.Clean(n, text) is string clean) { cfg.Links[n.Key] = clean; linkBoxes[n.Key].Text = clean; }
            else bad.Add(n.Label);
        }
        Program.SaveSettings(cfg);
        if (bad.Count > 0) { LinksStatus.Text = $"Check {string.Join(", ", bad)}: that doesn't look like a username."; return; }
        if (Online.Enabled)
        {
            LinksStatus.Text = "Saving…";
            await UploadNow();
            LinksStatus.Text = "Saved. They're on your profile now.";
        }
        else LinksStatus.Text = "Saved. They'll show once you join the leaderboards.";
    }

    async Task LoadHeader(Image img, string appId)
    {
        var mine = games.FirstOrDefault(g => g.G.AppId == appId)?.HeaderImage;
        if (mine != null) { img.Source = mine; return; }
        img.Source = await Img.Cached($"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
                                      System.IO.Path.Combine(Program.Home, "webcache", $"header_{appId}.jpg"), 420);
    }

    // =============== shop ===============

    void BuildShop()
    {
        var s = Shop.Load();
        int level = xp?.Level ?? 1;
        long balance = Shop.Balance(s, level);
        CoinBalance.Text = $"{balance:N0} coins";
        CoinSub.Text = $"Level {level}  ·  level {level + 1} gives you {Shop.CoinsForLevel(level + 1)} more  ·  {Shop.Earned(level):N0} earned, {s.Spent:N0} spent";
        EquippedPreview.Children.Clear();
        EquippedPreview.Children.Add(Avatar(s.Equipped, data?.PersonaName ?? "?", 58, Palette.Brush));

        ShopTiers.Children.Clear();
        foreach (var (tier, hex, price) in Shop.Tiers)
        {
            var color = Fmt.Brush(hex);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 18, 0, 12) };
            head.Children.Add(new TextBlock { Text = tier, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = color });
            head.Children.Add(new TextBlock { Text = $"{price:N0} coins each", Foreground = Res("Muted"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            ShopTiers.Children.Add(head);
            var wrap = new WrapPanel();
            foreach (var icon in Shop.Icons.Where(i => i.Tier == tier))
                wrap.Children.Add(ShopCard(icon, s, level, balance));
            ShopTiers.Children.Add(wrap);
        }
    }

    Control ShopCard(ShopIcon icon, Shop.State s, int level, long balance)
    {
        bool owned = s.Owned.Contains(icon.Id), equipped = owned && s.Equipped == icon.Id, locked = level < icon.MinLevel;
        var color = Fmt.Brush(icon.Color);
        var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var pic = new Border
        {
            Width = 76, Height = 76, CornerRadius = new CornerRadius(38), Background = Fmt.Tint(color, 0x26), BorderBrush = color,
            BorderThickness = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Center, Opacity = locked ? 0.45 : 1,
            Child = new TextBlock { Text = icon.Glyph, FontSize = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        sp.Children.Add(pic);
        sp.Children.Add(new TextBlock { Text = icon.Name, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });

        string label = equipped ? "Unequip" : owned ? "Equip" : locked ? $"Level {icon.MinLevel}" : $"{icon.Price:N0} coins";
        var btn = new Button { HorizontalAlignment = HorizontalAlignment.Center, Content = new TextBlock { Text = label } };
        btn.Classes.Add("pill");
        btn.IsEnabled = owned || (!locked && balance >= icon.Price);
        if (!owned && !locked && balance < icon.Price) ToolTip.SetTip(btn, $"You need {icon.Price - balance:N0} more coins");
        btn.Click += async (_, _) =>
        {
            if (equipped) Shop.Equip("");
            else if (owned) Shop.Equip(icon.Id);
            else
            {
                var err = Shop.Buy(icon, level);
                ShopMessage.IsVisible = true;
                ShopMessage.Text = err ?? $"Bought {icon.Glyph} {icon.Name}, and it's on your profile now!";
                ShopMessage.Foreground = err == null ? Fmt.Gold : Fmt.Orange;
            }
            BuildShop();
            UpdateAvatar();
            if (Online.Enabled) await UploadNow();
        };
        sp.Children.Add(btn);
        if (equipped) sp.Children.Add(new TextBlock { Text = "EQUIPPED", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center });

        var card = new Border { Child = sp, Width = 150, Padding = new Thickness(12, 16), Margin = new Thickness(0, 0, 12, 12) };
        card.Classes.Add("card");
        if (equipped) { card.BorderBrush = color; card.BorderThickness = new Thickness(2); }
        return card;
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

    // =============== chat ===============

    readonly DispatcherTimer chatTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    string? chatWith;            // null = lobby
    string chatWithName = "";
    long chatLast;               // newest message id shown
    bool chatBusy, chatBlocked;

    async Task OpenChat(string? with, string name)
    {
        chatWith = with;
        chatWithName = name;
        chatLast = 0;
        ChatMessages.Children.Clear();
        if (NavChat.IsChecked != true) NavChat.IsChecked = true; // shows the page and refreshes
        else await RefreshChat(full: true);
    }

    async Task RefreshChat(bool full)
    {
        if (chatBusy) return;
        chatBusy = true;
        try
        {
            chatTimer.Start();
            bool canWrite = Online.Enabled;
            ChatInput.IsEnabled = canWrite;
            ChatInput.Watermark = !Online.Available ? "Chat needs the Trophyline server, which isn't set up yet."
                : canWrite ? (chatWith == null ? "Write to everyone…" : $"Write to {chatWithName}…")
                : "Join the leaderboards (Leaderboards page) to write messages.";
            if (!Online.Available) { ChatTitle.Text = "Lobby"; ChatTitleSub.Text = "offline"; return; }

            ChatTitle.Text = chatWith == null ? "Lobby" : chatWithName;
            ChatTitleSub.Text = chatWith == null ? "everyone on Trophyline · be nice" : "private conversation";
            ChatProfileButton.IsVisible = ChatBlockButton.IsVisible = chatWith != null;

            if (full) await BuildChatRooms();

            JsonNode? res = chatWith == null ? await Online.Lobby(chatLast)
                          : canWrite ? await Online.Dm(chatWith, chatLast) : null;
            var msgs = res?["messages"]?.AsArray() ?? [];
            bool atBottom = ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - 40;
            foreach (var m in msgs)
            {
                chatLast = Math.Max(chatLast, L(m?["id"]));
                ChatMessages.Children.Add(ChatBubble(m));
            }
            if (ChatMessages.Children.Count == 0)
                ChatMessages.Children.Add(new TextBlock { Text = chatWith == null ? "No messages yet. Say hi!" : "No messages yet.", Foreground = Res("Muted"), Tag = "empty" });
            else if (ChatMessages.Children.Count > 1 && ChatMessages.Children[0] is TextBlock { Tag: "empty" } e) ChatMessages.Children.Remove(e);
            if (msgs.Count > 0 && (full || atBottom))
                Dispatcher.UIThread.Post(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
        finally { chatBusy = false; }
    }

    async Task BuildChatRooms()
    {
        ChatRooms.Children.Clear();
        ChatRooms.Children.Add(ChatRoomButton(null, "Lobby", "everyone", "", chatWith == null));
        if (!Online.Enabled) return;
        var inbox = await Online.Inbox();
        var convs = inbox?["conversations"]?.AsArray() ?? [];
        if (convs.Count > 0)
            ChatRooms.Children.Add(new TextBlock { Text = "PRIVATE", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Res("Muted"), Margin = new Thickness(10, 12, 0, 4) });
        bool listed = false;
        foreach (var c in convs)
        {
            var id = S(c?["id"]);
            var last = c?["last"];
            var preview = (S(last?["sender"]) == Online.MyId ? "You: " : "") + S(last?["text"]);
            if (id == chatWith) { listed = true; chatBlocked = c?["blocked"]?.GetValue<bool>() == true; }
            ChatRooms.Children.Add(ChatRoomButton(id, S(c?["name"]), preview, S(c?["icon"]), id == chatWith, L(c?["avatar"])));
        }
        if (chatWith != null && !listed)
        {
            chatBlocked = false;
            ChatRooms.Children.Add(ChatRoomButton(chatWith, chatWithName, "new conversation", "", true));
        }
        ChatBlockText.Text = chatBlocked ? "Unblock" : "Block";
    }

    Control ChatRoomButton(string? id, string name, string sub, string icon, bool active, long pictureVer = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(id == null
            ? new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Fmt.Tint(Palette.Brush, 0x30),
                           Child = new PathIcon { Data = Icon("IconChat"), Width = 16, Height = 16, Foreground = Palette.Brush } }
            : Avatar(icon, name, 34, Res("Line"), id, pictureVer));
        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 170 };
        t.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        t.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(t);
        var bd = new Border { Child = row, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(10), Background = active ? Res("CardHi") : Brushes.Transparent };
        var b = new Button { Content = bd, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        b.Classes.Add("plain");
        b.Click += async (_, _) => await OpenChat(id, name);
        return b;
    }

    Control ChatBubble(JsonNode? m)
    {
        var sender = S(m?["sender"]);
        bool mine = sender == Online.MyId;
        var name = S(m?["name"]);
        int level = (int)Math.Max(1, L(m?["level"]));
        var (_, rankHex) = Xp.RankFor(level);

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = mine ? "You" : name, FontWeight = FontWeight.SemiBold, FontSize = 13 });
        head.Children.Add(new TextBlock { Text = $"Lv {level}", FontSize = 11, Foreground = Fmt.Brush(rankHex), VerticalAlignment = VerticalAlignment.Center });
        var when = Fmt.Local(L(m?["time"]));
        head.Children.Add(new TextBlock { Text = when.Date == DateTime.Today ? when.ToString("HH:mm") : when.ToString("dd.MM HH:mm"), FontSize = 11, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center });

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8), MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left,
            Background = mine ? Fmt.Tint(Palette.Brush, 0x33) : Res("CardHi"),
            Child = new SelectableTextBlock { Text = S(m?["text"]), TextWrapping = TextWrapping.Wrap, FontSize = 14 },
        };
        var col = new StackPanel { Spacing = 4 };
        col.Children.Add(head);
        col.Children.Add(bubble);

        var avatar = Avatar(S(m?["icon"]), name, 36, Fmt.Brush(rankHex), sender, L(m?["avatar"]));
        avatar.VerticalAlignment = VerticalAlignment.Top;
        if (!mine)
        {
            avatar.Cursor = new Cursor(StandardCursorType.Hand);
            avatar.PointerPressed += (_, _) => OpenPlayer(sender);
            ToolTip.SetTip(avatar, $"{name}'s profile");
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(avatar);
        row.Children.Add(col);
        return row;
    }

    async Task SendChat()
    {
        var text = (ChatInput.Text ?? "").Trim();
        if (text.Length == 0 || !Online.Enabled) return;
        ChatInput.Text = "";
        var err = await Online.SendChat(text, chatWith);
        if (err != null) { ChatInput.Text = text; ChatTitleSub.Text = err; return; }
        await RefreshChat(full: chatWith != null && ChatRooms.Children.Count <= 2);
        ChatScroll.ScrollToEnd();
    }

    async void ChatSend_Click(object? sender, RoutedEventArgs e) => await SendChat();

    void ChatProfile_Click(object? sender, RoutedEventArgs e) { if (chatWith != null) OpenPlayer(chatWith); }

    async void ChatBlock_Click(object? sender, RoutedEventArgs e)
    {
        if (chatWith == null) return;
        var err = await Online.Block(chatWith, !chatBlocked);
        if (err != null) { ChatTitleSub.Text = err; return; }
        chatBlocked = !chatBlocked;
        ChatBlockText.Text = chatBlocked ? "Unblock" : "Block";
        ChatTitleSub.Text = chatBlocked ? $"Blocked: {chatWithName} can't message you, and you won't see their messages." : "private conversation";
        if (chatBlocked) { chatLast = 0; ChatMessages.Children.Clear(); await RefreshChat(full: true); }
    }

    Control[] AllPages => [OverviewPage, AchievementsPage, GamesPage, GamePage, LevelPage, LeaderboardPage, PlayerPage, ChatPage, ShopPage, PlaytimePage, StoragePage, MusicPage, SettingsPage];

    Control? VisiblePage() => AllPages.FirstOrDefault(p => p.IsVisible);

    void ShowPage(Control page)
    {
        if (page != GamePage) currentGame = null;
        foreach (var p in AllPages) p.IsVisible = p == page;
        var nav = page == OverviewPage ? NavOverview : page == AchievementsPage ? NavAchievements
                : page == StoragePage ? NavStorage : page == PlaytimePage ? NavPlaytime : page == LevelPage ? NavLevel : page == LeaderboardPage || page == PlayerPage ? NavLeaderboard : page == ShopPage ? NavShop : page == ChatPage ? NavChat : page == MusicPage ? NavMusic : page == SettingsPage ? NavSettings : NavGames;
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
            case "shop": NavShop.IsChecked = true; break;
            case "chat":
                if (arg != null) _ = OpenChat(arg, "NightOwl"); else NavChat.IsChecked = true;
                break;
            case "leaderboard":
                if (arg is "xp" or "perfect" or "over75") board = arg;
                NavLeaderboard.IsChecked = true;
                if (arg == "xp") BoardXp.IsChecked = true;
                break;
            case "player": if (arg != null) OpenPlayer(arg); break;
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
