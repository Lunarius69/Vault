using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vault.Database;
using Vault.Models;
using Vault.Services;
using Vault.ViewModels;

namespace Vault.Views
{
    public partial class GamesPage : UserControl
    {
        private AppSettings _settings;
        private List<Game> _allGames = new();
        private ObservableCollection<GameTileViewModel> _tiles = new();
        private string _currentPlatform = "All";
        private string _currentStatus = "All";
        private bool _isGridView = true;
        private bool _isFetchingArt = false;


        public event EventHandler<Game>? GameSelected;

        public GamesPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DataContext = this;
            Loaded += GamesPage_Loaded;
        }

        private async void GamesPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            using var db = new VaultContext();
            _allGames = await db.Games
                .Where(g => !g.IsWishlist)
                .OrderBy(g => g.Title)
                .ToListAsync();

            // Auto-detect downloaded games from games folder
            var detector = new AutoDetectService(_settings);
            int found = await detector.ScanAndUpdateAsync();
            if (found > 0)
            {
                // Reload so IsDownloaded flags are fresh
                _allGames = await db.Games
                    .Where(g => !g.IsWishlist)
                    .OrderBy(g => g.Title)
                    .ToListAsync();
            }

            LoadingOverlay.Visibility = Visibility.Collapsed;
            BuildPlatformList();
            ApplyFilters();
        }

        private async Task FetchMissingBoxArtAsync(List<GameTileViewModel> tiles)
{
    if (string.IsNullOrEmpty(_settings.SteamGridDbApiKey)) return;
    if (_isFetchingArt) return; // Don't restart if already running

    var missing = tiles.Where(t => !t.HasBoxArt).ToList();
    if (missing.Count == 0) return;

    _isFetchingArt = true;

    try
    {
        var service = new BoxArtService(_settings);
        using var db = new VaultContext();
        bool anyUpdated = false;
        var dbLock = new object();
        var semaphore = new System.Threading.SemaphoreSlim(5);

        var tasks = missing.Select(async tile =>
        {
            await semaphore.WaitAsync();
            try
            {
                string? path = await service.GetBoxArtAsync(tile.Game);
                if (path != null)
                {
                    Dispatcher.Invoke(() => tile.BoxArtPath = path);
                    lock (dbLock)
                    {
                        var dbGame = db.Games.Find(tile.Id);
                        if (dbGame != null)
                        {
                            dbGame.BoxArtPath = path;
                            anyUpdated = true;
                        }
                    }
                }
            }
            catch { }
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);

        if (anyUpdated)
            await db.SaveChangesAsync();
    }
    finally
    {
        _isFetchingArt = false;
    }
}

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = platform,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"  {count}",
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#636e72")),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            });
            btn.Content = panel;

            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetValue(Border.MarginProperty, new Thickness(6, 1, 6, 1));
            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.MarginProperty,
                new Thickness(4, 0, 0, 0));
            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;
            btn.Template = template;
            btn.Click += PlatformBtn_Click;
            return btn;
        }

        private void PlatformBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentPlatform = (sender as Button)?.Tag?.ToString() ?? "All";
            BuildPlatformList();
            ApplyFilters();
        }

        private void FilterBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentStatus = (sender as Button)?.Tag?.ToString() ?? "All";

            BtnAll.Style = (Style)FindResource("FilterButton");
            BtnPlaying.Style = (Style)FindResource("FilterButton");
            BtnCompleted.Style = (Style)FindResource("FilterButton");
            BtnNotStarted.Style = (Style)FindResource("FilterButton");
            BtnDownloaded.Style = (Style)FindResource("FilterButton");

            var active = (Style)FindResource("FilterButtonActive");
            switch (_currentStatus)
            {
                case "All": BtnAll.Style = active; break;
                case "Playing": BtnPlaying.Style = active; break;
                case "Completed": BtnCompleted.Style = active; break;
                case "Not Started": BtnNotStarted.Style = active; break;
                case "Downloaded": BtnDownloaded.Style = active; break;
            }

            ApplyFilters();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
{
    if (GamesItemsControl == null || _allGames == null) return;

    var filtered = _allGames.AsEnumerable();

    if (_currentPlatform != "All")
        filtered = filtered.Where(g => g.Platform == _currentPlatform);

    if (_currentStatus == "Downloaded")
        filtered = filtered.Where(g => g.IsDownloaded);
    else if (_currentStatus != "All")
        filtered = filtered.Where(g => g.Status == _currentStatus);

    int sortIdx = SortCombo?.SelectedIndex ?? 0;
    filtered = sortIdx switch
    {
        0 => filtered.OrderBy(g => g.Title),
        1 => filtered.OrderByDescending(g => g.Title),
        2 => filtered.OrderByDescending(g => g.Year),
        3 => filtered.OrderBy(g => g.Year),
        4 => filtered.OrderBy(g => g.Platform).ThenBy(g => g.Title),
        5 => filtered.OrderByDescending(g => g.PlaytimeMinutes),
        _ => filtered
    };

    var list = filtered.ToList();

    if (TxtGameCount != null)
        TxtGameCount.Text = $"{list.Count} games";

    if (_isGridView)
    {
        // Build a lookup of already-loaded box art paths from existing tiles
        // so re-sorting doesn't throw away art that was already downloaded
        var existingArt = _tiles.ToDictionary(t => t.Id, t => t.BoxArtPath);

        _tiles = new ObservableCollection<GameTileViewModel>(
            list.Select(g =>
            {
                var tile = new GameTileViewModel(g);
                // Restore already-cached art path so image shows instantly
                if (existingArt.TryGetValue(g.Id, out string? cachedPath)
                    && cachedPath != null)
                    tile.BoxArtPath = cachedPath;
                return tile;
            }));

        GamesItemsControl.ItemsSource = _tiles;

        // Only fetch art that is still genuinely missing
        var stillMissing = _tiles.Where(t => !t.HasBoxArt).ToList();
        if (stillMissing.Count > 0)
            _ = FetchMissingBoxArtAsync(stillMissing);
    }
    else
    {
        GamesListView.ItemsSource = list.Select(g => new
        {
            g.Title,
            g.Platform,
            g.Year,
            g.Status,
            PlaytimeDisplay = g.PlaytimeMinutes > 0
                ? $"{g.PlaytimeMinutes / 60}h {g.PlaytimeMinutes % 60}m" : "-",
            SizeDisplay = g.FileSizeGB.HasValue
                ? $"{g.FileSizeGB:F1} GB" : "-"
        }).ToList();
    }
}

        private void GameTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is GameTileViewModel tile)
                GameSelected?.Invoke(this, tile.Game);
        }

        public void SetViewMode(bool isGrid)
        {
            _isGridView = isGrid;
            GridScrollViewer.Visibility = isGrid ? Visibility.Visible : Visibility.Collapsed;
            GamesListView.Visibility = isGrid ? Visibility.Collapsed : Visibility.Visible;
            ApplyFilters();
        }

        public void Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { ApplyFilters(); return; }
            query = query.ToLower();
            var list = _allGames
                .Where(g => g.Title.ToLower().Contains(query) ||
                            g.Platform.ToLower().Contains(query) ||
                            (g.Genre != null && g.Genre.ToLower().Contains(query)))
                .ToList();

            if (TxtGameCount != null)
                TxtGameCount.Text = $"{list.Count} games";

            _tiles = new ObservableCollection<GameTileViewModel>(
                list.Select(g => new GameTileViewModel(g)));
            GamesItemsControl.ItemsSource = _tiles;
        }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        public RelayCommand(Action<T?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) =>
            _execute(parameter is T t ? t : default);
        public event EventHandler? CanExecuteChanged;
    }
}