using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vault.Database;
using Vault.Models;
using Vault.ViewModels;

namespace Vault.Views
{
    public partial class GameDetailPanel : UserControl
    {
        private Game? _game;
        private AppSettings _settings;
        public event EventHandler? CloseRequested;
        public event EventHandler? GameUpdated;

        public GameDetailPanel()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
        }

        public void LoadGame(Game game)
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

            // Title & platform
            TxtTitle.Text = game.Title;
            TxtPlatform.Text = game.Platform;
            PlatformBadge.Background = new SolidColorBrush(GetPlatformColor(game.Platform));

            // Status
            TxtStatus.Text = game.Status;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(game.Status));

            // Info
            TxtYear.Text = game.Year?.ToString() ?? "—";
            TxtSize.Text = game.FileSizeGB.HasValue ? $"{game.FileSizeGB:F1} GB" : "—";
            TxtGenre.Text = string.IsNullOrEmpty(game.Genre) ? "—" : game.Genre;
            TxtPlaytime.Text = game.PlaytimeMinutes > 0
                ? $"{game.PlaytimeMinutes / 60}h {game.PlaytimeMinutes % 60}m" : "—";

            // HLTB
            TxtHltbMain.Text = game.HltbMain.HasValue ? $"{game.HltbMain:F0}h" : "—";
            TxtHltbSides.Text = game.HltbMainSides.HasValue ? $"{game.HltbMainSides:F0}h" : "—";
            TxtHltbComplete.Text = game.HltbComplete.HasValue ? $"{game.HltbComplete:F0}h" : "—";

            // Description
            TxtDescription.Text = string.IsNullOrEmpty(game.Description)
                ? "No description available." : game.Description;

            // Launch button state
            BtnLaunch.IsEnabled = game.IsDownloaded &&
                (!string.IsNullOrEmpty(game.ExePath) || !string.IsNullOrEmpty(game.EmulatorPath));
            BtnLaunch.Content = game.IsDownloaded ? "▶  Launch Game" : "▶  Not Downloaded";

            TxtMessage.Visibility = Visibility.Collapsed;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke(this, EventArgs.Empty);

        private async void BtnLaunch_Click(object sender, RoutedEventArgs e)
        {
            if (_game == null) return;

            try
            {
                var launcher = new Vault.Services.GameLauncher(_settings);
                launcher.Launch(_game);

                // Start tracking playtime
                ShowMessage("Game launched!", "#00b894");
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
                Filter = "Executables|*.exe;*.bat;*.cmd|ROM files|*.iso;*.bin;*.cue;*.chd;*.nsp;*.xci;*.elf;*.pkg|All files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            string path = dialog.FileName;

            // Detect if it's a ROM (needs emulator) or direct exe
            bool isRom = IsRomFile(path);

            using var db = new VaultContext();
            var dbGame = await db.Games.FindAsync(_game.Id);
            if (dbGame == null) return;

            if (isRom)
            {
                dbGame.EmulatorPath = path;
                _game.EmulatorPath = path;
            }
            else
            {
                dbGame.ExePath = path;
                _game.ExePath = path;
            }

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

            // Cycle through statuses
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
            ShowMessage($"Status updated to: {newStatus}", "#00b894");
            GameUpdated?.Invoke(this, EventArgs.Empty);
        }

        private void ShowMessage(string msg, string color)
        {
            TxtMessage.Text = msg;
            TxtMessage.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            TxtMessage.Visibility = Visibility.Visible;
        }

        private static bool IsRomFile(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLower();
            return ext is ".iso" or ".bin" or ".cue" or ".chd" or
                          ".nsp" or ".xci" or ".elf" or ".pkg" or
                          ".rom" or ".nds" or ".3ds" or ".cia";
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
