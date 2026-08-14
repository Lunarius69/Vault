using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Vault.Database;
using Vault.Models;
using Vault.Services;

namespace Vault.Views
{
    public class AchievementRow
    {
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public bool IsUnlocked { get; set; }
        public string UnlockedDateDisplay { get; set; } = "";
    }

    public partial class GameDetailPage : UserControl
    {
        private Game _game;
        private readonly AppSettings _settings;
        private readonly HltbService _hltb = new();
        private readonly RetroAchievementsService? _ra;
        private readonly BoxArtService? _boxArt;
        private readonly AchievementWatcherService _achievementWatcher = new();

        public event EventHandler? BackRequested;
        public event EventHandler? GameUpdated;
        public event EventHandler<string>? MovedToMedia;
        // Fired after a game is deleted so GamesPage can remove the tile
        public event EventHandler? GameDeleted;

        public GameDetailPage(Game game, AppSettings settings)
        {
            InitializeComponent();
            _game = game;
            _settings = settings;

            try { AchievementWatcherService.SetApiKey(settings?.SteamApiKey); } catch { }

            try
            {
                if (!string.IsNullOrEmpty(settings?.SteamGridDbApiKey))
                    _boxArt = new BoxArtService(settings);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(settings?.RetroAchievementsApiKey))
                    _ra = new RetroAchievementsService(settings);
            }
            catch { }

            Loaded += (s, e) => LoadGame();
        }

        private async void LoadGame()
        {
            try
            {
                var game = _game;

                TxtTitle.Text = game.Title ?? "";
                TxtPlatform.Text = game.Platform ?? "";

                try { PlatformBadge.Background = new SolidColorBrush(GetPlatformColor(game.Platform)); } catch { }

                TxtStatus.Text = game.Status ?? "";
                try { StatusDot.Fill = new SolidColorBrush(GetStatusColor(game.Status)); } catch { }

                TxtYear.Text = game.Year?.ToString() ?? "—";
                TxtGenre.Text = string.IsNullOrEmpty(game.Genre) ? "—" : game.Genre;
                TxtSize.Text = game.FileSizeGB.HasValue ? $"{game.FileSizeGB:F1} GB" : "—";
                TxtPlaytime.Text = game.PlaytimeMinutes > 0
                    ? $"{game.PlaytimeMinutes / 60}h {game.PlaytimeMinutes % 60}m" : "—";
                TxtLastPlayed.Text = game.LastPlayed.HasValue
                    ? game.LastPlayed.Value.ToString("MMM d, yyyy") : "Never";
                TxtNotes.Text = string.IsNullOrEmpty(game.Notes) ? "—" : game.Notes;
                TxtEmulator.Text = string.IsNullOrEmpty(game.Emulator) ? "—" : game.Emulator;

                try { UpdateLaunchButton(); } catch { }
                try { LoadBoxArt(); } catch { }
                try { RefreshHltb(); } catch { }

                _ = SafeLoadBannerAsync();

                try { if (!game.HltbMain.HasValue) await FetchHltbAsync(); } catch { }

                await LoadAchievementsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDetailPage] LoadGame error: {ex}");
            }
        }

        // ── Platform helpers ──────────────────────────────────────────────────────

        private static bool IsPcGame(Game game)
        {
            try
            {
                string p = game?.Platform?.ToLower() ?? "";
                return p.Contains("pc") || p.Contains("windows");
            }
            catch { return false; }
        }

        private static bool IsRetroAchievementsPlatform(Game game)
        {
            try
            {
                string p = game?.Platform?.ToLower() ?? "";
                return p.Contains("nes") || p.Contains("snes") ||
                       p.Contains("nintendo 64") || p.Contains("game boy") ||
                       p.Contains("gameboy") || p.Contains("gba") || p.Contains("gbc") ||
                       p.Contains("nintendo ds") || p.Contains("nintendo 3ds") ||
                       p.Contains("gamecube") || p.Contains("wii") ||
                       p.Contains("genesis") || p.Contains("mega drive") ||
                       p.Contains("master system") || p.Contains("saturn") ||
                       p.Contains("dreamcast") || p.Contains("playstation 1") ||
                       p.Contains("playstation 2") || p.Contains("playstation 3") ||
                       p.Contains("ps1") || p.Contains("ps2") || p.Contains("ps3") ||
                       p.Contains("psp") || p.Contains("ps vita") || p.Contains("vita") ||
                       p.Contains("atari") || p.Contains("xbox original") ||
                       p.Contains("xbox 360") || p.Contains("sega");
            }
            catch { return false; }
        }

        // ── Delete ────────────────────────────────────────────────────────────────

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Populate the confirmation message with the game title
                TxtDeleteConfirmMsg.Text =
                    $"Are you sure you want to remove \"{_game.Title}\" from your library?\n\n" +
                    "This will delete all saved data for this game including playtime, " +
                    "achievements, and file paths. This cannot be undone.";

                DeleteConfirmOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDetailPage] BtnDelete_Click error: {ex}");
            }
        }

        private void BtnDeleteCancel_Click(object sender, RoutedEventArgs e)
        {
            try { DeleteConfirmOverlay.Visibility = Visibility.Collapsed; } catch { }
        }

        private async void BtnDeleteConfirm_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DeleteConfirmOverlay.Visibility = Visibility.Collapsed;

                using var db = new VaultContext();
                var dbGame = await db.Games.FindAsync(_game.Id);
                if (dbGame != null)
                    db.Games.Remove(dbGame);
                await db.SaveChangesAsync();

                // Notify GamesPage to remove the tile, then navigate back
                GameDeleted?.Invoke(this, EventArgs.Empty);
                BackRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDetailPage] BtnDeleteConfirm_Click error: {ex}");
                try { ShowMessage($"Failed to delete: {ex.Message}", "#e17055"); } catch { }
            }
        }

        // ── Achievements ──────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task LoadAchievementsAsync()
        {
            try
            {
                TxtAchLoading.Visibility = Visibility.Visible;
                AchievementsList.Visibility = Visibility.Collapsed;
                PanelNoAppId.Visibility = Visibility.Collapsed;
                AchCountBadge.Visibility = Visibility.Collapsed;
                TxtAchievementPct.Text = "";
                AchievementBarFill.Width = 0;
            }
            catch { }

            try
            {
                bool isPc = IsPcGame(_game);

                if (isPc)
                {
                    bool isSteam = _game.SteamAppId.HasValue
                                || AchievementWatcherService.IsSteamGame(_game);

                    if (!isSteam)
                    {
                        try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }
                        return;
                    }

                    List<Achievement>? achievements = null;
                    try { achievements = await _achievementWatcher.LoadAchievementsAsync(_game); }
                    catch { achievements = null; }

                    try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }

                    if (achievements == null)
                    {
                        if (!_game.SteamAppId.HasValue)
                        {
                            try { PanelNoAppId.Visibility = Visibility.Visible; } catch { }
                            if (_game.AchievementsTotal is > 0)
                                try { ShowAchievementCountOnly(); } catch { }
                        }
                        else
                        {
                            bool hasKey = !string.IsNullOrEmpty(_settings?.SteamApiKey);
                            try
                            {
                                TxtAchievementPct.Text = hasKey
                                    ? "No achievements found on Steam for this game."
                                    : "Add a Steam Web API Key in Settings to load achievements.\nGet one free at steamcommunity.com/dev/apikey";
                            }
                            catch { }
                        }
                        return;
                    }

                    if (achievements.Count == 0)
                    {
                        try { TxtAchievementPct.Text = "No achievements for this game."; } catch { }
                        return;
                    }

                    try { await SaveAchievementsToDbAsync(achievements); } catch { }
                    try
                    {
                        _game.AchievementsEarned = achievements.Count(a => a.IsUnlocked);
                        _game.AchievementsTotal = achievements.Count;
                    }
                    catch { }
                    try { RenderAchievementList(achievements); } catch { }
                    return;
                }

                try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }

                if (!IsRetroAchievementsPlatform(_game)) return;

                if (_ra == null || !_ra.IsConfigured)
                {
                    try
                    {
                        TxtAchievementPct.Text =
                            "Add a RetroAchievements username & API key in Settings to enable achievements for console games.";
                    }
                    catch { }
                    return;
                }

                if (_game.RetroAchievementsGameId == null || _game.RetroAchievementsGameId == 0)
                {
                    try { TxtAchievementPct.Text = "Looking up game on RetroAchievements…"; } catch { }
                    try { TxtAchLoading.Visibility = Visibility.Visible; } catch { }

                    try
                    {
                        int? raId = await _ra.SearchGameIdAsync(_game.Title, _game.Platform);
                        if (raId != null)
                        {
                            _game.RetroAchievementsGameId = raId;
                            try
                            {
                                using var db = new VaultContext();
                                var dbGame = await db.Games.FindAsync(_game.Id);
                                if (dbGame != null)
                                {
                                    dbGame.RetroAchievementsGameId = raId;
                                    await db.SaveChangesAsync();
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }
                }

                if (_game.RetroAchievementsGameId == null || _game.RetroAchievementsGameId == 0)
                {
                    try { TxtAchievementPct.Text = "Game not found on RetroAchievements."; } catch { }
                    return;
                }

                try
                {
                    try { TxtAchLoading.Visibility = Visibility.Visible; } catch { }
                    var raAchievements = await _ra.GetAchievementsAsync(_game.RetroAchievementsGameId.Value);
                    try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }

                    if (raAchievements == null || raAchievements.Count == 0)
                    {
                        try { TxtAchievementPct.Text = "No achievements found on RetroAchievements."; } catch { }
                        return;
                    }

                    try { await SaveAchievementsToDbAsync(raAchievements); } catch { }
                    try
                    {
                        _game.AchievementsEarned = raAchievements.Count(a => a.IsUnlocked);
                        _game.AchievementsTotal = raAchievements.Count;
                    }
                    catch { }
                    try { RenderAchievementList(raAchievements); } catch { }
                }
                catch (Exception ex)
                {
                    try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }
                    try { TxtAchievementPct.Text = $"RetroAchievements error: {ex.Message}"; } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDetailPage] LoadAchievementsAsync error: {ex}");
                try { TxtAchLoading.Visibility = Visibility.Collapsed; } catch { }
                try { TxtAchievementPct.Text = "Could not load achievements."; } catch { }
            }
        }

        private void RenderAchievementList(List<Achievement> achievements)
        {
            try
            {
                int earned = achievements.Count(a => a.IsUnlocked);
                int total = achievements.Count;
                double pct = total > 0 ? (earned / (double)total) * 100.0 : 0;

                try { TxtAchCountBadge.Text = $"{earned} / {total}"; AchCountBadge.Visibility = Visibility.Visible; } catch { }

                try
                {
                    TxtAchievementPct.Text = $"{pct:F0}% complete";
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            double maxWidth = AchievementsPanel.ActualWidth - 40;
                            AchievementBarFill.Width = maxWidth > 0 ? maxWidth * pct / 100.0 : 0;
                        }
                        catch { }
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                catch { }

                try
                {
                    var rows = achievements.Select(a => new AchievementRow
                    {
                        DisplayName = a.DisplayName ?? "",
                        Description = a.Description ?? "",
                        IsUnlocked = a.IsUnlocked,
                        UnlockedDateDisplay = a.UnlockedAt.HasValue
                            ? a.UnlockedAt.Value.ToString("MMM d")
                            : (a.IsUnlocked ? "Unlocked" : "")
                    }).ToList();

                    AchievementsList.ItemsSource = rows;
                    AchievementsList.Visibility = Visibility.Visible;
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDetailPage] RenderAchievementList error: {ex}");
            }
        }

        private void ShowAchievementCountOnly()
        {
            try
            {
                int earned = _game.AchievementsEarned ?? 0;
                int total = _game.AchievementsTotal!.Value;
                double pct = total > 0 ? (earned / (double)total) * 100.0 : 0;

                TxtAchCountBadge.Text = $"{earned} / {total}";
                AchCountBadge.Visibility = Visibility.Visible;
                TxtAchievementPct.Text = $"{pct:F0}% complete";

                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        double maxWidth = AchievementsPanel.ActualWidth - 40;
                        AchievementBarFill.Width = maxWidth > 0 ? maxWidth * pct / 100.0 : 0;
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDetailPage] ShowAchievementCountOnly error: {ex}");
            }
        }

        private async System.Threading.Tasks.Task SaveAchievementsToDbAsync(List<Achievement> achievements)
        {
            try
            {
                using var db = new VaultContext();
                var existing = db.Achievements.Where(a => a.GameId == _game.Id);
                db.Achievements.RemoveRange(existing);

                foreach (var a in achievements)
                {
                    try { a.GameId = _game.Id; db.Achievements.Add(a); } catch { }
                }

                var dbGame = await db.Games.FindAsync(_game.Id);
                if (dbGame != null)
                {
                    dbGame.AchievementsEarned = achievements.Count(a => a.IsUnlocked);
                    dbGame.AchievementsTotal = achievements.Count;
                }

                await db.SaveChangesAsync();
            }
            catch { }
        }

        // ── Banner + BoxArt ───────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task SafeLoadBannerAsync()
        {
            try { await LoadBannerAsync(); } catch { }
        }

        private async System.Threading.Tasks.Task LoadBannerAsync()
        {
            if (_boxArt == null) return;
            try
            {
                string? heroPath = await _boxArt.GetHeroAsync(_game);
                if (string.IsNullOrEmpty(heroPath) || !File.Exists(heroPath)) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(heroPath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        ImgBanner.Source = bmp;
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void LoadBoxArt()
        {
            try
            {
                if (!string.IsNullOrEmpty(_game.BoxArtPath) && File.Exists(_game.BoxArtPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(_game.BoxArtPath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    ImgBoxArt.Source = bmp;
                    ImgBoxArt.Visibility = Visibility.Visible;
                    PlaceholderBg.Visibility = Visibility.Collapsed;
                    TxtPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ImgBoxArt.Visibility = Visibility.Collapsed;
                    PlaceholderBg.Visibility = Visibility.Visible;
                    TxtPlaceholder.Visibility = Visibility.Visible;
                    TxtPlaceholder.Text = _game.Title ?? "";
                }
            }
            catch { }
        }

        // ── HLTB ──────────────────────────────────────────────────────────────────

        private void UpdateLaunchButton()
        {
            try
            {
                bool canLaunch = _game.IsDownloaded &&
                    (!string.IsNullOrEmpty(_game.ExePath) ||
                     !string.IsNullOrEmpty(_game.EmulatorPath));
                BtnLaunch.IsEnabled = canLaunch;
                BtnLaunch.Content = canLaunch ? "▶   Launch Game" : "▶   Not Downloaded";
            }
            catch { }
        }

        private void RefreshHltb()
        {
            try
            {
                TxtHltbMain.Text = _game.HltbMain.HasValue ? $"{_game.HltbMain:F0}h" : "—";
                TxtHltbSides.Text = _game.HltbMainSides.HasValue ? $"{_game.HltbMainSides:F0}h" : "—";
                TxtHltbComplete.Text = _game.HltbComplete.HasValue ? $"{_game.HltbComplete:F0}h" : "—";
            }
            catch { }
        }

        private async System.Threading.Tasks.Task FetchHltbAsync()
        {
            try
            {
                TxtHltbMain.Text = TxtHltbSides.Text = TxtHltbComplete.Text = "...";

                var data = await _hltb.FetchGameDataAsync(_game.Title ?? "");
                if (data == null) { RefreshHltb(); return; }

                try
                {
                    using var db = new VaultContext();
                    var dbGame = await db.Games.FindAsync(_game.Id);
                    if (dbGame != null)
                    {
                        dbGame.HltbMain = data.MainStory;
                        dbGame.HltbMainSides = data.MainPlusExtra;
                        dbGame.HltbComplete = data.Completionist;
                        if (!string.IsNullOrEmpty(data.Description)) dbGame.Description = data.Description;
                        if (!string.IsNullOrEmpty(data.Genre)) dbGame.Genre = data.Genre;
                        await db.SaveChangesAsync();
                    }
                }
                catch { }

                _game.HltbMain = data.MainStory;
                _game.HltbMainSides = data.MainPlusExtra;
                _game.HltbComplete = data.Completionist;
                if (!string.IsNullOrEmpty(data.Genre))
                {
                    _game.Genre = data.Genre;
                    try { TxtGenre.Text = data.Genre; } catch { }
                }

                RefreshHltb();
            }
            catch { }
        }

        // ── Button handlers ───────────────────────────────────────────────────────

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            try { BackRequested?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var launcher = new GameLauncher(_settings);
                launcher.Launch(_game);
                ShowMessage("Game launched! Tracking playtime...", "#00b894");
            }
            catch (Exception ex)
            {
                try { ShowMessage($"Failed to launch: {ex.Message}", "#e17055"); } catch { }
            }
        }

        private async void BtnSetPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = $"Select executable for {_game.Title}",
                    Filter = "Executables|*.exe;*.bat|ROM files|*.iso;*.bin;*.cue;*.chd;*.nsp;*.xci;*.gba;*.nds;*.n64;*.z64|All files|*.*"
                };
                if (dialog.ShowDialog() != true) return;

                string path = dialog.FileName;
                bool isRom = GameLauncher.IsRomFile(path);

                try
                {
                    using var db = new VaultContext();
                    var dbGame = await db.Games.FindAsync(_game.Id);
                    if (dbGame == null) return;

                    if (isRom) { dbGame.EmulatorPath = path; _game.EmulatorPath = path; }
                    else { dbGame.ExePath = path; _game.ExePath = path; }

                    dbGame.IsDownloaded = true;
                    _game.IsDownloaded = true;
                    await db.SaveChangesAsync();

                    // FIX: without this, ProcessWatcherService never learns
                    // about a newly-set ExePath — it only built its
                    // exe→game map once at startup, so this game could
                    // never be detected running externally, and playtime/
                    // status would never update outside the app's own
                    // Launch button.
                    if (!isRom) ProcessWatcherService.RequestExeMapRefresh();
                }
                catch (Exception ex)
                {
                    ShowMessage($"Failed to save path: {ex.Message}", "#e17055");
                    return;
                }

                try { UpdateLaunchButton(); } catch { }
                ShowMessage("Path saved!", "#00b894");
                try { GameUpdated?.Invoke(this, EventArgs.Empty); } catch { }
                await LoadAchievementsAsync();
            }
            catch (Exception ex)
            {
                try { ShowMessage($"Error: {ex.Message}", "#e17055"); } catch { }
            }
        }

        private void BtnMoveToCategory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var popup = new Popup
                {
                    PlacementTarget = sender as Button,
                    Placement = PlacementMode.Bottom,
                    StaysOpen = false,
                    AllowsTransparency = true
                };

                var border = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16213e")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(4),
                    MinWidth = 200
                };

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = "MOVE TO CATEGORY",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#636e72")),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(12, 8, 12, 4)
                });

                var categories = new[]
                {
                    ("🎬  Movie",           "Movie"),
                    ("🎌  Anime Movie",     "AnimeMovie"),
                    ("📺  TV Show",         "Show"),
                    ("🎌  Anime Series",    "Anime"),
                    ("🎨  Animated Movie",  "AnimatedMovie"),
                    ("🎨  Animated Series", "AnimatedSeries"),
                };

                foreach (var (label, mediaType) in categories)
                {
                    var btn = new Button
                    {
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(12, 8, 16, 8),
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };
                    btn.Content = new TextBlock
                    {
                        Text = label,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 13
                    };

                    var template = new ControlTemplate(typeof(Button));
                    var factory = new FrameworkElementFactory(typeof(Border));
                    factory.SetBinding(Border.BackgroundProperty,
                        new System.Windows.Data.Binding("Background")
                        {
                            RelativeSource = new System.Windows.Data.RelativeSource(
                                System.Windows.Data.RelativeSourceMode.TemplatedParent)
                        });
                    factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                    factory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                    template.VisualTree = factory;
                    btn.Template = template;

                    btn.MouseEnter += (s, _) =>
                        ((Button)s).Background = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#2d3561"));
                    btn.MouseLeave += (s, _) =>
                        ((Button)s).Background = Brushes.Transparent;

                    string capturedType = mediaType;
                    btn.Click += async (s, _) =>
                    {
                        popup.IsOpen = false;
                        await SafeMoveToMediaAsync(capturedType);
                    };
                    stack.Children.Add(btn);
                }

                border.Child = stack;
                popup.Child = border;
                popup.IsOpen = true;
            }
            catch (Exception ex)
            {
                try { ShowMessage($"Error: {ex.Message}", "#e17055"); } catch { }
            }
        }

        private async System.Threading.Tasks.Task SafeMoveToMediaAsync(string mediaType)
        {
            try { await MoveToMediaAsync(mediaType); }
            catch (Exception ex)
            {
                try { ShowMessage($"Failed to move: {ex.Message}", "#e17055"); } catch { }
            }
        }

        private async System.Threading.Tasks.Task MoveToMediaAsync(string mediaType)
        {
            using var db = new VaultContext();

            bool alreadyExists = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .AnyAsync(db.MediaItems, m => m.Title == _game.Title && m.MediaType == mediaType);

            if (!alreadyExists)
            {
                db.MediaItems.Add(new MediaItem
                {
                    Title = _game.Title,
                    MediaType = mediaType,
                    Year = _game.Year,
                    Genre = _game.Genre,
                    Description = _game.Description,
                    WatchStatus = "Not Started",
                    TmdbId = 0,
                    PosterPath = _game.BoxArtPath
                });
            }

            var dbGame = await db.Games.FindAsync(_game.Id);
            if (dbGame != null) db.Games.Remove(dbGame);
            await db.SaveChangesAsync();

            string friendlyName = mediaType switch
            {
                "Movie" => "Movies",
                "AnimeMovie" => "Anime Movies",
                "Show" => "TV Shows",
                "Anime" => "Anime Series",
                "AnimatedMovie" => "Animated Movies",
                "AnimatedSeries" => "Animated Series",
                _ => mediaType
            };

            ShowMessage($"Moved to {friendlyName}!", "#00b894");
            await System.Threading.Tasks.Task.Delay(800);
            await Dispatcher.InvokeAsync(() =>
            {
                try { MovedToMedia?.Invoke(this, mediaType); } catch { }
                try { BackRequested?.Invoke(this, EventArgs.Empty); } catch { }
            });
        }

        private void BtnEditStatus_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var popup = new Popup
                {
                    PlacementTarget = sender as Button,
                    Placement = PlacementMode.Bottom,
                    StaysOpen = false,
                    AllowsTransparency = true
                };

                var button = sender as Button;
                var border = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16213e")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(4),
                    MinWidth = button?.ActualWidth ?? 200
                };

                var stack = new StackPanel();
                string[] statuses = { "Not Started", "Playing", "Completed", "On Hold", "Dropped" };

                foreach (string status in statuses)
                {
                    string statusColor = status switch
                    {
                        "Playing" => "#00b894",
                        "Completed" => "#0984e3",
                        "Not Started" => "#636e72",
                        "On Hold" => "#fdcb6e",
                        "Dropped" => "#d63031",
                        _ => "#636e72"
                    };
                    bool isCurrent = _game?.Status == status;

                    var btn = new Button
                    {
                        Background = isCurrent
                            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561"))
                            : Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(12, 8, 16, 8),
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };

                    var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    btnPanel.Children.Add(new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                    btnPanel.Children.Add(new TextBlock
                    {
                        Text = status,
                        Foreground = isCurrent ? Brushes.White
                            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    btn.Content = btnPanel;

                    var template = new ControlTemplate(typeof(Button));
                    var factory = new FrameworkElementFactory(typeof(Border));
                    factory.SetBinding(Border.BackgroundProperty,
                        new System.Windows.Data.Binding("Background")
                        {
                            RelativeSource = new System.Windows.Data.RelativeSource(
                                System.Windows.Data.RelativeSourceMode.TemplatedParent)
                        });
                    factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                    factory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                    template.VisualTree = factory;
                    btn.Template = template;

                    string capturedStatus = status;
                    btn.Click += async (s, args) =>
                    {
                        popup.IsOpen = false;
                        try { await SetStatusAsync(capturedStatus); } catch { }
                    };
                    btn.MouseEnter += (s, args) =>
                        btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561"));
                    btn.MouseLeave += (s, args) =>
                        btn.Background = isCurrent
                            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561"))
                            : Brushes.Transparent;

                    stack.Children.Add(btn);
                }

                border.Child = stack;
                popup.Child = border;
                popup.IsOpen = true;
            }
            catch (Exception ex)
            {
                try { ShowMessage($"Error: {ex.Message}", "#e17055"); } catch { }
            }
        }

        private async System.Threading.Tasks.Task SetStatusAsync(string newStatus)
        {
            try
            {
                using var db = new VaultContext();
                var dbGame = await db.Games.FindAsync(_game.Id);
                if (dbGame == null) return;

                dbGame.Status = newStatus;
                _game.Status = newStatus;
                await db.SaveChangesAsync();

                try { TxtStatus.Text = newStatus; } catch { }
                try { StatusDot.Fill = new SolidColorBrush(GetStatusColor(newStatus)); } catch { }
                ShowMessage($"Status: {newStatus}", "#00b894");
                try { GameUpdated?.Invoke(this, EventArgs.Empty); } catch { }
            }
            catch (Exception ex)
            {
                try { ShowMessage($"Failed to update status: {ex.Message}", "#e17055"); } catch { }
            }
        }

        private async void BtnMarkNotDownloaded_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var db = new VaultContext();
                var dbGame = await db.Games.FindAsync(_game.Id);
                if (dbGame == null) return;

                dbGame.IsDownloaded = false;
                dbGame.ExePath = null;
                dbGame.EmulatorPath = null;
                dbGame.ManuallyMarkedNotDownloaded = true;
                _game.IsDownloaded = false;
                _game.ExePath = null;
                _game.EmulatorPath = null;
                await db.SaveChangesAsync();

                try { UpdateLaunchButton(); } catch { }
                ShowMessage("Marked as not downloaded.", "#636e72");
                try { GameUpdated?.Invoke(this, EventArgs.Empty); } catch { }
            }
            catch (Exception ex)
            {
                try { ShowMessage($"Error: {ex.Message}", "#e17055"); } catch { }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void ShowMessage(string msg, string color)
        {
            try
            {
                TxtMessage.Text = msg;
                TxtMessage.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                TxtMessage.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private static Color GetPlatformColor(string? platform) =>
            platform?.ToLower() switch
            {
                var p when p != null && p.Contains("pc") => (Color)ColorConverter.ConvertFromString("#0078d4"),
                var p when p != null && (p.Contains("ps5") || p.Contains("playstation 5")) => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p != null && (p.Contains("ps4") || p.Contains("playstation 4")) => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p != null && (p.Contains("ps3") || p.Contains("playstation 3")) => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p != null && (p.Contains("ps2") || p.Contains("playstation 2")) => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p != null && p.Contains("switch") => (Color)ColorConverter.ConvertFromString("#e60012"),
                var p when p != null && p.Contains("xbox") => (Color)ColorConverter.ConvertFromString("#107c10"),
                var p when p != null && (p.Contains("gamecube") || p.Contains("wii")) => (Color)ColorConverter.ConvertFromString("#6a0dad"),
                _ => (Color)ColorConverter.ConvertFromString("#2d3561")
            };

        private static Color GetStatusColor(string? status) =>
            status?.ToLower() switch
            {
                "playing" => (Color)ColorConverter.ConvertFromString("#00b894"),
                "completed" => (Color)ColorConverter.ConvertFromString("#0984e3"),
                "not started" => (Color)ColorConverter.ConvertFromString("#636e72"),
                "on hold" => (Color)ColorConverter.ConvertFromString("#fdcb6e"),
                "dropped" => (Color)ColorConverter.ConvertFromString("#d63031"),
                _ => (Color)ColorConverter.ConvertFromString("#636e72")
            };
    }
}