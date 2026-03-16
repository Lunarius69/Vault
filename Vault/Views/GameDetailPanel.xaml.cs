using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vault.Database;
using Vault.Models;
using Vault.Services;

namespace Vault.Views
{
    public partial class GameDetailPage : UserControl
    {
        private Game _game;
        private readonly AppSettings _settings;
        private readonly HltbService _hltb = new();
        private readonly RetroAchievementsService? _ra;
        private readonly BoxArtService? _boxArt;
        public event EventHandler? BackRequested;

        public GameDetailPage(Game game, AppSettings settings)
        {
            InitializeComponent();
            _game = game;
            _settings = settings;

            if (!string.IsNullOrEmpty(settings.SteamGridDbApiKey))
                _boxArt = new BoxArtService(settings);

            if (!string.IsNullOrEmpty(settings.RetroAchievementsApiKey))
                _ra = new RetroAchievementsService(settings);

            Loaded += (s, e) => LoadGame();
        }

        private async void LoadGame()
        {
            var game = _game;

            // Title, platform, status
            TxtTitle.Text = game.Title;
            TxtPlatform.Text = game.Platform;
            PlatformBadge.Background = new SolidColorBrush(GetPlatformColor(game.Platform));
            TxtStatus.Text = game.Status;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(game.Status));

            // Info fields
            TxtYear.Text = game.Year?.ToString() ?? "—";
            TxtGenre.Text = string.IsNullOrEmpty(game.Genre) ? "—" : game.Genre;
            TxtSize.Text = game.FileSizeGB.HasValue ? $"{game.FileSizeGB:F1} GB" : "—";
            TxtPlaytime.Text = game.PlaytimeMinutes > 0
                ? $"{game.PlaytimeMinutes / 60}h {game.PlaytimeMinutes % 60}m" : "—";
            TxtLastPlayed.Text = game.LastPlayed.HasValue
                ? game.LastPlayed.Value.ToString("MMM d, yyyy") : "Never";
            TxtDescription.Text = string.IsNullOrEmpty(game.Description)
                ? "No description available." : game.Description;

            // Launch button label
            UpdateLaunchButton();

            // Box art
            LoadBoxArt();

            // HLTB — show cached, fetch if missing
            RefreshHltb();
            if (!game.HltbMain.HasValue)
                await FetchHltbAsync();

            // Achievements
            RefreshAchievements();
            if (_ra?.IsConfigured == true && game.AchievementsTotal == null)
                await FetchAchievementsAsync();

            // Banner — fetch in background, show when ready
            if (_boxArt != null)
            {
                string? heroPath = await _boxArt.GetHeroAsync(game);
                if (!string.IsNullOrEmpty(heroPath) && File.Exists(heroPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(heroPath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    ImgBanner.Source = bmp;
                }
            }
        }

        private void LoadBoxArt()
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
                TxtPlaceholder.Text = _game.Title;
            }
        }

        private void UpdateLaunchButton()
        {
            bool canLaunch = _game.IsDownloaded &&
                (!string.IsNullOrEmpty(_game.ExePath) ||
                 !string.IsNullOrEmpty(_game.EmulatorPath));
            BtnLaunch.IsEnabled = canLaunch;
            BtnLaunch.Content = canLaunch ? "▶   Launch Game" : "▶   Not Downloaded";
        }

        private void RefreshHltb()
        {
            TxtHltbMain.Text = _game.HltbMain.HasValue ? $"{_game.HltbMain:F0}h" : "—";
            TxtHltbSides.Text = _game.HltbMainSides.HasValue ? $"{_game.HltbMainSides:F0}h" : "—";
            TxtHltbComplete.Text = _game.HltbComplete.HasValue ? $"{_game.HltbComplete:F0}h" : "—";
        }

        private async System.Threading.Tasks.Task FetchHltbAsync()
        {
            TxtHltbMain.Text = "...";
            var (main, sides, complete) = await _hltb.FetchAsync(_game.Title);
            if (main == null && sides == null && complete == null)
            {
                RefreshHltb();
                return;
            }

            using var db = new VaultContext();
            var dbGame = await db.Games.FindAsync(_game.Id);
            if (dbGame != null)
            {
                dbGame.HltbMain = main;
                dbGame.HltbMainSides = sides;
                dbGame.HltbComplete = complete;
                await db.SaveChangesAsync();
            }

            _game.HltbMain = main;
            _game.HltbMainSides = sides;
            _game.HltbComplete = complete;
            RefreshHltb();
        }

        private void RefreshAchievements()
        {
            if (_game.AchievementsTotal is > 0)
            {
                int earned = _game.AchievementsEarned ?? 0;
                int total = _game.AchievementsTotal.Value;
                double pct = total > 0 ? (earned / (double)total) * 100.0 : 0;
                TxtAchievements.Text = $"{earned} / {total}";
                TxtAchievementPct.Text = $"{pct:F0}% complete";

                // Update bar width after layout
                Dispatcher.InvokeAsync(() =>
                {
                    double maxWidth = AchievementsPanel.ActualWidth - 40;
                    AchievementBarFill.Width = maxWidth > 0 ? maxWidth * pct / 100.0 : 0;
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (_ra == null)
            {
                TxtAchievements.Text = "—";
                TxtAchievementPct.Text = "Set RetroAchievements key in Settings";
            }
            else
            {
                TxtAchievements.Text = "—";
                TxtAchievementPct.Text = "Not found on RetroAchievements";
            }
        }

        private async System.Threading.Tasks.Task FetchAchievementsAsync()
        {
            if (_ra == null) return;
            TxtAchievements.Text = "...";

            int? raId = _game.RetroAchievementsGameId
                ?? await _ra.FindGameIdAsync(_game.Title, _game.Platform);

            if (raId == null)
            {
                TxtAchievements.Text = "—";
                TxtAchievementPct.Text = "Not found on RetroAchievements";
                return;
            }

            var result = await _ra.GetAchievementsAsync(raId.Value);
            if (result == null)
            {
                TxtAchievements.Text = "—";
                return;
            }

            using var db = new VaultContext();
            var dbGame = await db.Games.FindAsync(_game.Id);
            if (dbGame != null)
            {
                dbGame.RetroAchievementsGameId = raId;
                dbGame.AchievementsEarned = result.Value.Earned;
                dbGame.AchievementsTotal = result.Value.Total;
                await db.SaveChangesAsync();
            }

            _game.RetroAchievementsGameId = raId;
            _game.AchievementsEarned = result.Value.Earned;
            _game.AchievementsTotal = result.Value.Total;
            RefreshAchievements();
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

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
                ShowMessage($"Failed to launch: {ex.Message}", "#e17055");
            }
        }

        private async void BtnSetPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select executable for {_game.Title}",
                Filter = "Executables|*.exe;*.bat|ROM files|*.iso;*.bin;*.cue;*.chd;*.nsp;*.xci;*.gba;*.nds;*.n64;*.z64|All files|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            string path = dialog.FileName;
            bool isRom = GameLauncher.IsRomFile(path);

            using var db = new VaultContext();
            var dbGame = await db.Games.FindAsync(_game.Id);
            if (dbGame == null) return;

            if (isRom) { dbGame.EmulatorPath = path; _game.EmulatorPath = path; }
            else { dbGame.ExePath = path; _game.ExePath = path; }

            dbGame.IsDownloaded = true;
            _game.IsDownloaded = true;
            await db.SaveChangesAsync();

            UpdateLaunchButton();
            ShowMessage("Path saved!", "#00b894");
        }

        private async void BtnEditStatus_Click(object sender, RoutedEventArgs e)
        {
            string[] statuses = { "Not Started", "Playing", "Completed", "On Hold", "Dropped" };
            int idx = Array.IndexOf(statuses, _game.Status);
            string newStatus = statuses[(idx + 1) % statuses.Length];

            using var db = new VaultContext();
            var dbGame = await db.Games.FindAsync(_game.Id);
            if (dbGame == null) return;

            dbGame.Status = newStatus;
            _game.Status = newStatus;
            await db.SaveChangesAsync();

            TxtStatus.Text = newStatus;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(newStatus));
            ShowMessage($"Status: {newStatus}", "#00b894");
        }

        private void ShowMessage(string msg, string color)
        {
            TxtMessage.Text = msg;
            TxtMessage.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            TxtMessage.Visibility = Visibility.Visible;
        }

        private static Color GetPlatformColor(string platform) =>
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

        private static Color GetStatusColor(string status) =>
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