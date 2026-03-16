using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vault.Database;
using Vault.Models;
using Vault.Services;

namespace Vault.Views
{
    public partial class GameDetailPanel : UserControl
    {
        private Game? _game;
        private readonly AppSettings _settings;
        private readonly HltbService _hltb = new();
        private readonly RetroAchievementsService? _ra;

        public event EventHandler? CloseRequested;
        public event EventHandler? GameUpdated;

        public GameDetailPanel()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            if (!string.IsNullOrEmpty(_settings.RetroAchievementsApiKey))
                _ra = new RetroAchievementsService(_settings);
        }

        public async void LoadGame(Game game)
        {
            _game = game;

            // Box art
            if (!string.IsNullOrEmpty(game.BoxArtPath) &&
                System.IO.File.Exists(game.BoxArtPath))
            {
                ImgBoxArt.Source = new BitmapImage(new Uri(game.BoxArtPath));
                ImgBoxArt.Visibility = Visibility.Visible;
                PlaceholderBg.Visibility = Visibility.Collapsed;
                TxtPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                ImgBoxArt.Visibility = Visibility.Collapsed;
                PlaceholderBg.Visibility = Visibility.Visible;
                TxtPlaceholder.Visibility = Visibility.Visible;
                TxtPlaceholder.Text = game.Title;
            }

            // Core info
            TxtTitle.Text = game.Title;
            TxtPlatform.Text = game.Platform;
            PlatformBadge.Background = new SolidColorBrush(GetPlatformColor(game.Platform));
            TxtStatus.Text = game.Status;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(game.Status));
            TxtYear.Text = game.Year?.ToString() ?? "—";
            TxtSize.Text = game.FileSizeGB.HasValue ? $"{game.FileSizeGB:F1} GB" : "—";
            TxtGenre.Text = string.IsNullOrEmpty(game.Genre) ? "—" : game.Genre;

            // Playtime
            RefreshPlaytime();

            // Last played
            TxtLastPlayed.Text = game.LastPlayed.HasValue
                ? game.LastPlayed.Value.ToString("MMM d, yyyy") : "Never";

            // HLTB — show cached first, then fetch if missing
            RefreshHltb();
            if (!game.HltbMain.HasValue)
                _ = FetchHltbAsync();

            // Achievements
            RefreshAchievements();
            if (_ra?.IsConfigured == true && game.AchievementsTotal == null)
                _ = FetchAchievementsAsync();

            // Description
            TxtDescription.Text = string.IsNullOrEmpty(game.Description)
                ? "No description available." : game.Description;

            // Launch button
            BtnLaunch.IsEnabled = game.IsDownloaded &&
                (!string.IsNullOrEmpty(game.ExePath) ||
                 !string.IsNullOrEmpty(game.EmulatorPath));
            BtnLaunch.Content = game.IsDownloaded ? "▶  Launch Game" : "▶  Not Downloaded";

            TxtMessage.Visibility = Visibility.Collapsed;
        }

        private void RefreshPlaytime()
        {
            if (_game == null) return;
            TxtPlaytime.Text = _game.PlaytimeMinutes > 0
                ? $"{_game.PlaytimeMinutes / 60}h {_game.PlaytimeMinutes % 60}m"
                : "—";
        }

        private void RefreshHltb()
        {
            if (_game == null) return;
            TxtHltbMain.Text = _game.HltbMain.HasValue ? $"{_game.HltbMain:F0}h" : "—";
            TxtHltbSides.Text = _game.HltbMainSides.HasValue ? $"{_game.HltbMainSides:F0}h" : "—";
            TxtHltbComplete.Text = _game.HltbComplete.HasValue ? $"{_game.HltbComplete:F0}h" : "—";
        }

        private async System.Threading.Tasks.Task FetchHltbAsync()
        {
            if (_game == null) return;
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

            Dispatcher.Invoke(RefreshHltb);
        }

        private void RefreshAchievements()
        {
            if (_game == null) return;
            if (_game.AchievementsTotal is > 0)
            {
                int earned = _game.AchievementsEarned ?? 0;
                int total = _game.AchievementsTotal.Value;
                double pct = total > 0 ? (earned / (double)total) * 100 : 0;
                TxtAchievements.Text = $"{earned} / {total}  ({pct:F0}%)";
                AchievementBar.Value = pct;
                AchievementsPanel.Visibility = Visibility.Visible;
            }
            else if (_ra?.IsConfigured == false)
            {
                TxtAchievements.Text = "Set RetroAchievements API key in Settings";
                AchievementBar.Value = 0;
                AchievementsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                TxtAchievements.Text = "—";
                AchievementBar.Value = 0;
            }
        }

        private async System.Threading.Tasks.Task FetchAchievementsAsync()
        {
            if (_game == null || _ra == null) return;
            TxtAchievements.Text = "...";

            // Find RA game ID if we don't have it
            int? raId = _game.RetroAchievementsGameId;
            if (raId == null)
                raId = await _ra.FindGameIdAsync(_game.Title, _game.Platform);

            if (raId == null) 
            { 
                Dispatcher.Invoke(() => TxtAchievements.Text = "Not found on RA"); 
                return; 
            }

            var result = await _ra.GetAchievementsAsync(raId.Value);
            if (result == null) 
            { 
                Dispatcher.Invoke(() => TxtAchievements.Text = "—"); 
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

            Dispatcher.Invoke(RefreshAchievements);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;
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
            if (_game == null) return;

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

            BtnLaunch.IsEnabled = true;
            BtnLaunch.Content = "▶  Launch Game";
            ShowMessage("Path saved!", "#00b894");
            GameUpdated?.Invoke(this, EventArgs.Empty);
        }

        private async void BtnEditStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;
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
            GameUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void ShowMessage(string msg, string color)
        {
            TxtMessage.Text = msg;
            TxtMessage.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            TxtMessage.Visibility = Visibility.Visible;
        }

        private static Color GetPlatformColor(string platform)
        {
            return platform?.ToLower() switch
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
        }

        private static Color GetStatusColor(string status)
        {
            return status?.ToLower() switch
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
}