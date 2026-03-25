using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.EntityFrameworkCore;
using Vault.Database;
using Vault.Models;
using Vault.ViewModels;

namespace Vault.Views
{
    public partial class GamesPage : UserControl
    {
        private readonly AppSettings _settings;
        private List<GameTileViewModel> _allGames = new();
        private string _currentFilter = "All";
        private string _currentPlatform = "All";
        private string _currentSearch = "";

        public event EventHandler<Game>? GameSelected;

        public GamesPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            Loaded += GamesPage_Loaded;
        }

        private async void GamesPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadGamesAsync();
        }

        public async Task RefreshAsync()
        {
            await LoadGamesAsync();
        }

        public void Search(string query)
        {
            _currentSearch = query ?? "";
            ApplyFilters();
        }

        public void SetViewMode(bool isGrid)
        {
            GridScrollViewer.Visibility = isGrid ? Visibility.Visible : Visibility.Collapsed;
            GamesListView.Visibility = isGrid ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task LoadGamesAsync()
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            try
            {
                using var db = new VaultContext();
                var games = await db.Games.OrderBy(g => g.Title).ToListAsync();

                var existingById = _allGames.ToDictionary(vm => vm.Id);
                var newList = new List<GameTileViewModel>(games.Count);

                foreach (var game in games)
                {
                    if (existingById.TryGetValue(game.Id, out var existing))
                    {
                        existing.UpdateGame(game);
                        newList.Add(existing);
                        existingById.Remove(game.Id);
                    }
                    else
                    {
                        newList.Add(new GameTileViewModel(game));
                    }
                }

                foreach (var removed in existingById.Values)
                    removed.Dispose();

                _allGames = newList;

                BuildPlatformSidebar();
                ApplyFilters();

                // Fetch box art for games that are missing it
                _ = FetchMissingBoxArtAsync(games);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async Task FetchMissingBoxArtAsync(List<Game> games)
        {
            var boxArtService = new Services.BoxArtService(_settings);
            var missing = games.Where(g =>
                string.IsNullOrEmpty(g.BoxArtPath) || !System.IO.File.Exists(g.BoxArtPath))
                .ToList();

            if (missing.Count == 0) return;

            using var db = new VaultContext();

            foreach (var game in missing)
            {
                try
                {
                    string? path = await boxArtService.GetBoxArtAsync(game);
                    if (string.IsNullOrEmpty(path)) continue;

                    // Update DB
                    var dbGame = await db.Games.FindAsync(game.Id);
                    if (dbGame != null)
                    {
                        dbGame.BoxArtPath = path;
                        game.BoxArtPath = path;
                    }

                    // Update the tile on the UI thread
                    var vm = _allGames.FirstOrDefault(v => v.Id == game.Id);
                    if (vm != null)
                        vm.BoxArtPath = path;
                }
                catch { }
            }

            await db.SaveChangesAsync();
        }

        private void BuildPlatformSidebar()
        {
            PlatformPanel.Children.Clear();

            var platforms = _allGames
                .Select(g => g.Platform)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            platforms.Insert(0, "All");

            foreach (var platform in platforms)
            {
                var count = platform == "All"
                    ? _allGames.Count
                    : _allGames.Count(g => g.Platform == platform);

                var isActive = platform == _currentPlatform;
                var btn = new Button
                {
                    Content = $"{platform} ({count})",
                    Tag = platform,
                    Height = 34,
                    Margin = new Thickness(8, 2, 8, 2),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 0, 10, 0),
                    Background = isActive
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e94560"))
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16213e")),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12
                };
                btn.Click += PlatformBtn_Click;
                PlatformPanel.Children.Add(btn);
            }
        }

        private void PlatformBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string platform)
            {
                _currentPlatform = platform;
                BuildPlatformSidebar();
                ApplyFilters();
            }
        }

        private void ApplyFilters()
        {
            var filtered = _allGames.AsEnumerable();

            // Status / downloaded filter
            if (_currentFilter != "All")
            {
                if (_currentFilter == "Downloaded")
                    filtered = filtered.Where(g => g.IsDownloaded);
                else
                    filtered = filtered.Where(g => g.Status == _currentFilter);
            }

            // Platform filter
            if (_currentPlatform != "All")
                filtered = filtered.Where(g => g.Platform == _currentPlatform);

            // Search
            if (!string.IsNullOrWhiteSpace(_currentSearch))
                filtered = filtered.Where(g =>
                    g.Title.Contains(_currentSearch, StringComparison.OrdinalIgnoreCase));

            // Sort
            filtered = (SortCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
            {
                "Title Z-A" => filtered.OrderByDescending(g => g.Title),
                "Year (Newest)" => filtered.OrderByDescending(g => g.Year),
                "Year (Oldest)" => filtered.OrderBy(g => g.Year),
                "Platform" => filtered.OrderBy(g => g.Platform),
                "Playtime" => filtered.OrderByDescending(g => g.Game.PlaytimeMinutes),
                _ => filtered.OrderBy(g => g.Title),
            };

            var list = filtered.ToList();
            GamesItemsControl.ItemsSource = list;
            GamesListView.ItemsSource = list;
            TxtGameCount.Text = $"{list.Count} game{(list.Count != 1 ? "s" : "")}";
        }

        private void FilterBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;

            _currentFilter = btn.Tag?.ToString() ?? "All";

            var inactive = (Style)FindResource("FilterButton");
            var active = (Style)FindResource("FilterButtonActive");
            BtnAll.Style = inactive;
            BtnPlaying.Style = inactive;
            BtnCompleted.Style = inactive;
            BtnNotStarted.Style = inactive;
            BtnDownloaded.Style = inactive;
            btn.Style = active;

            ApplyFilters();
        }

        private async void BtnScanRoms_Click(object sender, RoutedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            try
            {
                var scanner = new Services.RomScanner(_settings);
                await scanner.ScanAsync();
                await LoadGamesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Scan failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Guard: _allGames may not be initialized yet during XAML load
            if (_allGames.Count == 0) return;
            ApplyFilters();
        }

        private void GameTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameTileViewModel vm)
                GameSelected?.Invoke(this, vm.Game);
        }
    }
}