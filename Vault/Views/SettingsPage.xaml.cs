using Microsoft.Win32;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Vault.Models;
using Vault.Services;

namespace Vault.Views
{
    public partial class SettingsPage : UserControl
    {
        private AppSettings _settings;
        private CancellationTokenSource? _resolveCts;

        public SettingsPage()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            LoadSettings();
        }

        private void LoadSettings()
        {
            TxtGamesFolder.Text = _settings.GamesFolderPath;
            TxtGamesFolder2.Text = _settings.GamesFolderPath2;
            TxtGamesFolder3.Text = _settings.GamesFolderPath3;
            TxtEmulatorsFolder.Text = _settings.EmulatorsFolderPath;
            TxtMoviesFolder.Text = _settings.MoviesFolderPath;
            TxtShowsFolder.Text = _settings.ShowsFolderPath;
            TxtAnimeFolder.Text = _settings.AnimeFolderPath;
            TxtDataFolder.Text = _settings.DataFolderPath;
            TxtSteamGridKey.Text = _settings.SteamGridDbApiKey;
            TxtTmdbKey.Text = _settings.TmdbApiKey;
            TxtSteamKey.Text = _settings.SteamApiKey;
            TxtSteamId.Text = _settings.SteamUserId;
            TxtRetroUser.Text = _settings.RetroAchievementsUser;
            TxtRetroKey.Text = _settings.RetroAchievementsApiKey;
            TxtOpenVGDB.Text = _settings.OpenVGDBPath ?? "";
            TxtGamesExcel.Text = _settings.GamesExcelPath;
            TxtMediaExcel.Text = _settings.MediaExcelPath;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.GamesFolderPath = TxtGamesFolder.Text.Trim();
            _settings.GamesFolderPath2 = TxtGamesFolder2.Text.Trim();
            _settings.GamesFolderPath3 = TxtGamesFolder3.Text.Trim();
            _settings.EmulatorsFolderPath = TxtEmulatorsFolder.Text.Trim();
            _settings.MoviesFolderPath = TxtMoviesFolder.Text.Trim();
            _settings.ShowsFolderPath = TxtShowsFolder.Text.Trim();
            _settings.AnimeFolderPath = TxtAnimeFolder.Text.Trim();
            _settings.DataFolderPath = TxtDataFolder.Text.Trim();
            _settings.SteamGridDbApiKey = TxtSteamGridKey.Text.Trim();
            _settings.TmdbApiKey = TxtTmdbKey.Text.Trim();
            _settings.SteamApiKey = TxtSteamKey.Text.Trim();
            _settings.SteamUserId = TxtSteamId.Text.Trim();
            _settings.RetroAchievementsUser = TxtRetroUser.Text.Trim();
            _settings.RetroAchievementsApiKey = TxtRetroKey.Text.Trim();
            _settings.OpenVGDBPath = TxtOpenVGDB.Text.Trim();
            _settings.GamesExcelPath = TxtGamesExcel.Text.Trim();
            _settings.MediaExcelPath = TxtMediaExcel.Text.Trim();
            _settings.Save();

            TxtSaveStatus.Text = "Settings saved successfully.";
            TxtSaveStatus.Visibility = Visibility.Visible;
        }

        // ── Steam AppID bulk resolver ─────────────────────────────────────────────

        private async void BtnResolveSteamIds_Click(object sender, RoutedEventArgs e)
        {
            // If already running, cancel
            if (_resolveCts != null)
            {
                _resolveCts.Cancel();
                return; // UI resets in the finally block below
            }

            _resolveCts = new CancellationTokenSource();
            BtnResolveSteamIds.Content = "⏹  Cancel";
            PnlResolveProgress.Visibility = Visibility.Visible;
            TxtResolveStatus.Visibility = Visibility.Collapsed;

            var resolver = new BulkSteamAppIdResolver();

            var progress = new Progress<(int current, int total, string title)>(p =>
            {
                TxtResolveGame.Text = p.title;
                TxtResolveCount.Text = $"{p.current} / {p.total}";

                // Update the custom progress bar fill width
                double trackWidth = PnlResolveProgress.ActualWidth - 32; // minus padding
                double fill = p.total > 0 ? trackWidth * p.current / p.total : 0;
                PbResolveFill.Width = Math.Max(0, fill);
            });

            try
            {
                var result = await resolver.ResolveAllAsync(progress, _resolveCts.Token);

                TxtResolveGame.Text = "Complete";
                TxtResolveCount.Text = $"{result.Total} / {result.Total}";
                PbResolveFill.Width = Math.Max(0, PnlResolveProgress.ActualWidth - 32);

                TxtResolveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                TxtResolveStatus.Text =
                    $"✓  {result.Resolved} resolved • " +
                    $"{result.AlreadyHad} already cached • " +
                    $"{result.Skipped} non-Steam • " +
                    (result.Failed.Count > 0 ? $"{result.Failed.Count} errors" : "no errors");
            }
            catch (OperationCanceledException)
            {
                TxtResolveStatus.Foreground =
                    new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                            .ConvertFromString("#fdcb6e"));
                TxtResolveStatus.Text = "Cancelled — progress saved to database.";
            }
            catch (Exception ex)
            {
                TxtResolveStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                TxtResolveStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                _resolveCts?.Dispose();
                _resolveCts = null;
                BtnResolveSteamIds.Content = "🔍  Resolve Steam AppIDs";
                TxtResolveStatus.Visibility = Visibility.Visible;
            }
        }

        // ── Browse helpers ────────────────────────────────────────────────────────

        private string BrowseFolder()
        {
            var dialog = new OpenFolderDialog { Title = "Select Folder" };
            return dialog.ShowDialog() == true ? dialog.FolderName : string.Empty;
        }

        private string BrowseFile(string filter)
        {
            var dialog = new OpenFileDialog { Filter = filter };
            return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
        }

        private void BrowseGames_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtGamesFolder.Text = path;
        }

        private void BrowseGames2_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtGamesFolder2.Text = path;
        }

        private void BrowseGames3_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtGamesFolder3.Text = path;
        }

        private void BrowseEmulators_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtEmulatorsFolder.Text = path;
        }

        private void BrowseMovies_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtMoviesFolder.Text = path;
        }

        private void BrowseShows_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtShowsFolder.Text = path;
        }

        private void BrowseAnime_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtAnimeFolder.Text = path;
        }

        private void BrowseData_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFolder();
            if (!string.IsNullOrEmpty(path)) TxtDataFolder.Text = path;
        }

        private void BrowseOpenVGDB_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFile("SQLite Database|*.sqlite;*.db|All files|*.*");
            if (!string.IsNullOrEmpty(path)) TxtOpenVGDB.Text = path;
        }

        private void BrowseGamesExcel_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFile("Excel Files|*.xlsx;*.xls");
            if (!string.IsNullOrEmpty(path)) TxtGamesExcel.Text = path;
        }

        private void BrowseMediaExcel_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseFile("Excel Files|*.xlsx;*.xls");
            if (!string.IsNullOrEmpty(path)) TxtMediaExcel.Text = path;
        }

        private async void ImportGames_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtGamesExcel.Text))
            {
                TxtImportStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                TxtImportStatus.Text = "Please select a Games Excel file first.";
                TxtImportStatus.Visibility = Visibility.Visible;
                return;
            }
            TxtImportStatus.Foreground = System.Windows.Media.Brushes.Gray;
            TxtImportStatus.Text = "Importing games...";
            TxtImportStatus.Visibility = Visibility.Visible;

            using var db = new Vault.Database.VaultContext();
            var boxArtService = new Vault.Services.BoxArtService(_settings);
            var importer = new Vault.Services.ExcelImporter(db, boxArtService);
            var result = await importer.ImportGamesAsync(TxtGamesExcel.Text.Trim());

            TxtImportStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            TxtImportStatus.Text = $"Done! {result.GamesImported} games imported.";
        }

        private async void ImportMedia_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtMediaExcel.Text))
            {
                TxtImportStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
                TxtImportStatus.Text = "Please select a Media Excel file first.";
                TxtImportStatus.Visibility = Visibility.Visible;
                return;
            }
            TxtImportStatus.Foreground = System.Windows.Media.Brushes.Gray;
            TxtImportStatus.Text = "Importing media...";
            TxtImportStatus.Visibility = Visibility.Visible;

            using var db = new Vault.Database.VaultContext();
            var boxArtService = new Vault.Services.BoxArtService(_settings);
            var importer = new Vault.Services.ExcelImporter(db, boxArtService);
            var result = await importer.ImportMediaAsync(TxtMediaExcel.Text.Trim());

            TxtImportStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            TxtImportStatus.Text = $"Done! {result.MediaImported} media items imported.";
        }
    }
}