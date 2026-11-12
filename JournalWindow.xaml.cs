using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Trophyline;

public partial class JournalWindow : Window
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
    ICollectionView? achView;
    ICollectionView? gameAchView;
    GameVM? currentGame;
    FrameworkElement? pageBeforeGame;

    /// Completes once the first load (including online extras) is done. Used by --snapshot.
    public Task Ready => ready.Task;

    public JournalWindow()
    {
        InitializeComponent();

        // Opens maximized; if you un-maximize, it still fits on the screen.
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(1280, area.Width * 0.9);
        Height = Math.Min(900, area.Height * 0.9);

        NavOverview.Checked += (_, _) => { if (!syncingNav) ShowPage(OverviewPage); };
        NavAchievements.Checked += (_, _) => { if (!syncingNav) ShowPage(AchievementsPage); };
        NavGames.Checked += (_, _) => { if (!syncingNav) ShowPage(GamesPage); };
        NavStorage.Checked += (_, _) => { if (!syncingNav) ShowPage(StoragePage); };

        AchRarity.ItemsSource = RarityOptions;
        AchRarity.SelectedIndex = 0;
        GameSort.ItemsSource = SortOptions;
        GameSort.SelectedIndex = 0;

        achSearchTimer.Tick += (_, _) => { achSearchTimer.Stop(); RefreshAchView(); };
        gameSearchTimer.Tick += (_, _) => { gameSearchTimer.Stop(); FillGamesGrid(); };
        AchSearch.TextChanged += (_, _) => { achSearchTimer.Stop(); achSearchTimer.Start(); };
        GameSearch.TextChanged += (_, _) => { gameSearchTimer.Stop(); gameSearchTimer.Start(); };
        AchGame.SelectionChanged += (_, _) => RefreshAchView();
        AchRarity.SelectionChanged += (_, _) => RefreshAchView();
        GameSort.SelectionChanged += (_, _) => FillGamesGrid();
        ChipAll.Checked += (_, _) => gameAchView?.Refresh();
        ChipUnlocked.Checked += (_, _) => gameAchView?.Refresh();
        ChipLocked.Checked += (_, _) => gameAchView?.Refresh();

        reloadTimer.Tick += async (_, _) => { reloadTimer.Stop(); await LoadAsync(online: false); };
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += async (_, _) => { WatchJournal(); await LoadAsync(online: true); ready.TrySetResult(); };
        Activated += async (_, _) =>
        {
            if (!loading && lastLoad != default && DateTime.Now - lastLoad > TimeSpan.FromMinutes(2))
                await LoadAsync(online: false);
        };
        Closed += (_, _) => watcher?.Dispose();
    }

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
                ShowOverlay("Couldn't read your Steam files.\n\n" + ex.Message);
                return;
            }
            Apply(d);
            Overlay.Visibility = Visibility.Collapsed;
            lastLoad = DateTime.Now;
            Program.LogWindow($"loaded games={d.Games.Count} unlocked={unlocked.Count} persona={(d.PersonaName != null)} home={Program.Home}");

            if (online)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Trophyline/1.0");
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
        if (currentGame != null && games.FirstOrDefault(g => g.G.AppId == currentGame.G.AppId) is GameVM again)
            FillGamePage(again);
    }

    void WatchJournal()
    {
        // The tracker writes the journal the moment you unlock something: reload then.
        try
        {
            watcher = new FileSystemWatcher(Program.Home, "journal.tsv")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() => { reloadTimer.Stop(); reloadTimer.Start(); });
            watcher.Changed += h;
            watcher.Created += h;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Program.LogError("watch: " + ex.Message); }
    }

    void ShowOverlay(string text)
    {
        OverlayText.Text = text;
        Overlay.Visibility = Visibility.Visible;
    }

    // =============== sidebar ===============

    void BuildProfile()
    {
        var name = data?.PersonaName ?? "Steam player";
        ProfileName.Text = name;
        AvatarLetter.Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
        bool tracking = Program.TrackerRunning();
        TrackerDot.Fill = tracking ? (Brush)FindResource("Green") : Fmt.Frozen("#F59E4B");
        TrackerText.Text = tracking ? "Tracking live" : "Tracker off";
        TrackerText.ToolTip = tracking
            ? "The background tracker is recording new achievements."
            : "The background tracker isn't running, so new achievements won't be recorded with their time.";
    }

    // =============== overview ===============

    void BuildOverview()
    {
        Persona.Text = data?.PersonaName ?? "Player";

        var recent = games.Where(g => g.G.LastPlayed > 0).OrderByDescending(g => g.G.LastPlayed).ToList();
        var heroGame = recent.FirstOrDefault(g => g.G.Hero != null) ?? recent.FirstOrDefault();
        HeroImage.ImageSource = Img.File(heroGame?.G.Hero ?? heroGame?.G.Header, 1600);
        HeroLogo.Source = Img.File(heroGame?.G.Logo, 700);
        var two = games.Sum(g => g.G.Playtime2WeeksMinutes);
        HeroSub.Text = recent.FirstOrDefault() is GameVM last
            ? $"Last played {last.Name}  ·  {Fmt.Ago(last.G.LastPlayed)}" + (two > 0 ? $"  ·  {Fmt.Hours(two)} in the last 2 weeks" : "")
            : "Play a game on Steam and it shows up here.";

        // tiles
        Tiles.Children.Clear();
        var withAch = games.Where(g => g.G.Total > 0).ToList();
        var started = withAch.Where(g => g.G.UnlockedCount > 0).ToList();
        int totalAch = withAch.Sum(g => g.G.Total);
        int perfect = withAch.Count(g => g.G.Perfect);
        int half = withAch.Count(g => g.G.Completion >= 0.5);
        double avg = started.Count == 0 ? 0 : started.Average(g => g.G.Completion) * 100;
        int minutes = games.Sum(g => g.G.PlaytimeMinutes);
        var rarest = unlocked.Where(a => a.A.Rarity != null).OrderBy(a => a.A.Rarity).FirstOrDefault();
        var gold = (Brush)FindResource("Gold");
        var accent = (Brush)FindResource("Accent");

        Tiles.Children.Add(Tile("", unlocked.Count.ToString("N0"), "achievements unlocked", $"out of {totalAch:N0} in your games", accent));
        Tiles.Children.Add(Tile("", perfect.ToString(), perfect == 1 ? "perfect game" : "perfect games", $"{half} games at 50% or more", gold));
        Tiles.Children.Add(Tile("", $"{avg:0}%", "average completion", $"across {started.Count} games you've started", Fmt.Purple));
        Tiles.Children.Add(Tile("", $"{minutes / 60.0:N0} h", "total playtime", two > 0 ? $"{Fmt.Hours(two)} in the last 2 weeks" : "nothing in the last 2 weeks", (Brush)FindResource("Green")));
        Tiles.Children.Add(Tile("", games.Count.ToString(), "games", $"{games.Count(g => g.G.Installed)} installed", accent));
        Tiles.Children.Add(rarest != null
            ? Tile("", rarest.A.Rarity < 10 ? $"{rarest.A.Rarity:0.0}%" : $"{rarest.A.Rarity:0}%", "rarest unlock", $"{rarest.Name} · {rarest.Game}", gold)
            : Tile("", "–", "rarest unlock", "loading global stats…", gold));

        BuildHeatmap();

        RecentList.ItemsSource = unlocked.Take(6).ToList();
        RareList.ItemsSource = unlocked.Where(a => a.A.Rarity != null).OrderBy(a => a.A.Rarity).Take(6).ToList();
        MostPlayed.ItemsSource = games.Where(g => g.G.PlaytimeMinutes > 0).OrderByDescending(g => g.G.PlaytimeMinutes).Take(10).ToList();
    }

    Border Tile(string glyph, string value, string label, string sub, Brush color)
    {
        var icon = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(12),
            Background = Fmt.Tint(color, 0x26),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 19, Foreground = color,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = value, FontSize = 26, FontWeight = FontWeights.Bold });
        text.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Text"), FontSize = 13 });
        text.Children.Add(new TextBlock
        {
            Text = sub, Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 4, 0, 0);
        row.Children.Add(icon);
        row.Children.Add(text);
        return new Border
        {
            Style = (Style)FindResource("CardStyle"),
            Margin = new Thickness(8),
            Padding = new Thickness(18, 16, 18, 16),
            Child = row,
        };
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
        Brush[] levels = [(Brush)FindResource("Line"), Fmt.Frozen("#1D4466"), Fmt.Frozen("#2B6A9C"), Fmt.Frozen("#3F94CF"), Fmt.Frozen("#7FD0FF")];
        var muted = (Brush)FindResource("Muted");

        int lastMonth = -1;
        for (int w = 0; w < weeks; w++)
        {
            for (int d = 0; d < 7; d++)
            {
                var date = start.AddDays(w * 7 + d);
                if (date > today) break;
                counts.TryGetValue(date, out var n);
                int lvl = n == 0 ? 0 : n == 1 ? 1 : n <= 3 ? 2 : n <= 6 ? 3 : 4;
                var r = new Rectangle
                {
                    Width = cell, Height = cell, RadiusX = 3, RadiusY = 3, Fill = levels[lvl],
                    ToolTip = $"{date:ddd dd.MM.yyyy}:  {(n == 0 ? "no" : n.ToString())} achievement{(n == 1 ? "" : "s")}",
                };
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
            Canvas.SetTop(t, top + d * step);
            Heatmap.Children.Add(t);
        }
        Heatmap.Width = left + weeks * step;
        Heatmap.Height = top + 7 * step;

        HeatLegend.Children.Add(new TextBlock { Text = "Less", FontSize = 11, Foreground = muted, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var b in levels)
            HeatLegend.Children.Add(new Rectangle { Width = 11, Height = 11, RadiusX = 2, RadiusY = 2, Fill = b, Margin = new Thickness(2, 0, 2, 0) });
        HeatLegend.Children.Add(new TextBlock { Text = "More", FontSize = 11, Foreground = muted, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        // summary: last 12 months
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

        foreach (var a in unlocked) a.GameVisibility = Visibility.Visible;
        achView = new ListCollectionView(unlocked);
        achView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AchVM.Group)));
        achView.Filter = o => MatchAch((AchVM)o);
        AchList.GroupStyle.Clear();
        AchList.GroupStyle.Add((GroupStyle)FindResource("MonthGroups"));
        AchList.ItemsSource = achView;
        AchSub.Text = $"{unlocked.Count:N0} unlocked across {names.Count} games";
        UpdateAchEmpty();
    }

    bool MatchAch(AchVM a)
    {
        if (AchGame.SelectedItem is string g && g != AllGames && a.Game != g) return false;
        switch (AchRarity.SelectedIndex)
        {
            case 1 when a.A.Rarity is not < 15: return false;
            case 2 when a.A.Rarity is not < 5: return false;
        }
        var q = AchSearch.Text.Trim().ToLowerInvariant();
        return q.Length == 0 || q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(a.SearchText.Contains);
    }

    void RefreshAchView()
    {
        achView?.Refresh();
        UpdateAchEmpty();
    }

    void UpdateAchEmpty()
    {
        bool none = achView == null || achView.IsEmpty;
        AchEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
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
        var q = GameSearch.Text.Trim().ToLowerInvariant();
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

    void OpenGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GameVM vm) OpenGame(vm);
    }

    void OpenGame(GameVM vm)
    {
        pageBeforeGame = VisiblePage() is FrameworkElement p && p != GamePage ? p : pageBeforeGame ?? GamesPage;
        ChipAll.IsChecked = true;
        FillGamePage(vm);
        ShowPage(GamePage);
    }

    void FillGamePage(GameVM vm)
    {
        currentGame = vm;
        var g = vm.G;
        GameHero.ImageSource = Img.File(g.Hero ?? g.Header ?? g.Portrait, 1800);
        GameLogo.Source = Img.File(g.Logo, 800);
        GameTitle.Text = vm.Name;
        GameTitle.Visibility = GameLogo.Source == null ? Visibility.Visible : Visibility.Collapsed;

        GameStats.Children.Clear();
        GameStats.Children.Add(MiniStat("Playtime", g.PlaytimeMinutes > 0 ? Fmt.Hours(g.PlaytimeMinutes) : "–"));
        GameStats.Children.Add(MiniStat("Last played", g.LastPlayed > 0 ? Fmt.Ago(g.LastPlayed) : "never"));
        if (g.Playtime2WeeksMinutes > 0) GameStats.Children.Add(MiniStat("Last 2 weeks", Fmt.Hours(g.Playtime2WeeksMinutes)));
        if (g.Total > 0)
        {
            GameStats.Children.Add(MiniStat("Achievements", $"{g.UnlockedCount} / {g.Total}"));
            GameStats.Children.Add(MiniStat("Completion", $"{g.Completion * 100:0}%", g.Perfect ? (Brush)FindResource("Gold") : null));
            var rare = g.Achievements.Where(a => a.Unlocked && a.Rarity != null).OrderBy(a => a.Rarity).FirstOrDefault();
            if (rare != null) GameStats.Children.Add(MiniStat("Rarest unlock", $"{rare.Rarity:0.0}%", (Brush)FindResource("Gold")));
        }
        if (g.Installed) GameStats.Children.Add(MiniStat("On disk", Fmt.Size(g.SizeOnDisk)));

        bool hasAch = g.Total > 0;
        GameBarHost.Visibility = GameChips.Visibility = GameAchList.Visibility = hasAch ? Visibility.Visible : Visibility.Collapsed;
        GameNoAch.Visibility = hasAch ? Visibility.Collapsed : Visibility.Visible;
        GameBar.Background = g.Perfect ? (Brush)FindResource("Gold") : (Brush)FindResource("Accent");
        GameBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0, g.Completion, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        int nUnlocked = g.UnlockedCount, nLocked = g.Total - nUnlocked;
        var rows = g.Achievements
            .OrderByDescending(a => a.Unlocked)
            .ThenByDescending(a => a.Unlocked ? a.Time ?? 0 : 0)
            .ThenByDescending(a => a.Rarity ?? -1)
            .Select(a => new AchVM(a, g)
            {
                GameVisibility = Visibility.Collapsed,
                Group = a.Unlocked ? $"Unlocked  ·  {nUnlocked}" : $"Still to get  ·  {nLocked}  ·  easiest first",
            })
            .ToList();
        gameAchView = new ListCollectionView(rows);
        gameAchView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AchVM.Group)));
        gameAchView.Filter = o =>
        {
            var a = (AchVM)o;
            return ChipUnlocked.IsChecked == true ? a.A.Unlocked : ChipLocked.IsChecked == true ? !a.A.Unlocked : true;
        };
        GameAchList.GroupStyle.Clear();
        GameAchList.GroupStyle.Add((GroupStyle)FindResource("MonthGroups"));
        GameAchList.ItemsSource = gameAchView;
    }

    Border MiniStat(string label, string value, Brush? color = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted") });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = color ?? (Brush)FindResource("Text"), Margin = new Thickness(0, 2, 0, 0) });
        return new Border
        {
            Style = (Style)FindResource("CardStyle"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 10, 22, 12),
            Margin = new Thickness(0, 0, 10, 10),
            Child = sp,
        };
    }

    void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    void GoBack()
    {
        currentGame = null;
        ShowPage(pageBeforeGame ?? GamesPage);
    }

    void ShowAllGames_Click(object sender, RoutedEventArgs e) => NavGames.IsChecked = true;

    // =============== storage ===============

    void BuildStorage()
    {
        var installed = games.Where(g => g.G.Installed && g.G.SizeOnDisk > 0).OrderByDescending(g => g.G.SizeOnDisk).ToList();
        long max = installed.Count > 0 ? installed[0].G.SizeOnDisk : 1;
        foreach (var g in installed) g.SizeFraction = (double)g.G.SizeOnDisk / max;
        long total = installed.Sum(g => g.G.SizeOnDisk);
        var stale = installed.Where(g => g.StaleVisibility == Visibility.Visible).ToList();
        StorageSub.Text = $"{installed.Count} installed games use {Fmt.Size(total)}"
            + (stale.Count > 0 ? $"  ·  {stale.Count} not played in 6+ months could free {Fmt.Size(stale.Sum(g => g.G.SizeOnDisk))}" : "");
        StorageList.ItemsSource = installed;
    }

    // =============== navigation ===============

    FrameworkElement[] AllPages => [OverviewPage, AchievementsPage, GamesPage, GamePage, StoragePage];

    FrameworkElement? VisiblePage() => AllPages.FirstOrDefault(p => p.Visibility == Visibility.Visible);

    void ShowPage(FrameworkElement page)
    {
        if (page != GamePage) currentGame = null;
        foreach (var p in AllPages) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        // keep the sidebar in sync without re-triggering navigation
        var nav = page == OverviewPage ? NavOverview : page == AchievementsPage ? NavAchievements
                : page == StoragePage ? NavStorage : NavGames;
        if (nav.IsChecked != true)
        {
            syncingNav = true;
            nav.IsChecked = true;
            syncingNav = false;
        }

        var move = new TranslateTransform(0, 14);
        page.RenderTransform = move;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
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
            else if (Keyboard.FocusedElement is TextBox tb && tb.Text.Length > 0) { tb.Text = ""; e.Handled = true; }
        }
        else if (e.Key == Key.Back && VisiblePage() == GamePage && Keyboard.FocusedElement is not TextBox)
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
            case "achievements": NavAchievements.IsChecked = true; AchSearch.Text = arg ?? ""; RefreshAchView(); break;
            case "games": NavGames.IsChecked = true; if (arg != null) GameSort.SelectedIndex = int.Parse(arg); break;
            case "storage": NavStorage.IsChecked = true; break;
            case "game":
                var vm = games.FirstOrDefault(g => g.G.AppId == arg) ?? games.OrderByDescending(g => g.G.LastPlayed).First();
                OpenGame(vm);
                break;
            default: NavOverview.IsChecked = true; break;
        }
    }

    internal string DescribeView() =>
        $"page={VisiblePage()?.Name} games={games.Count} unlocked={unlocked.Count} achVisible={achView?.Cast<object>().Count()} " +
        $"gameAch={gameAchView?.Cast<object>().Count()} storage={(StorageList.ItemsSource as System.Collections.IList)?.Count}";

    // =============== chrome ===============

    void DarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x0016100B; // COLORREF (0x00BBGGRR) of the window background #0B1016
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
