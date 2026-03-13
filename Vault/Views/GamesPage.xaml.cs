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
using Vault.ViewModels;

namespace Vault.Views
{
    public partial class GamesPage : Page
    {
        private AppSettings _settings;
        private List<Game> _allGames = new();
        private ObservableCollection<GameTileViewModel> _tiles = new();
        private string _currentPlatform = "All";
        private string _currentStatus = "All";
        private bool _isGridView = true;

        public ICommand GameClickCommand { get; }

        public GamesPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            GameClickCommand = new RelayCommand<GameTileViewModel>(OnGameClicked);
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
            LoadingOverlay.Visibility = Visibility.Collapsed;
            BuildPlatformList();
            ApplyFilters();
        }

        private async Task FetchMissingBoxArtAsync(List<GameTileViewModel> tiles)
        {
            if (string.IsNullOrEmpty(_settings.SteamGridDbApiKey)) return;

            var service = new Vault.Services.BoxArtService(_settings);
            var missing = tiles.Where(t => !t.HasBoxArt).ToList();
            if (missing.Count == 0) return;

            using var db = new VaultContext();
            bool anyUpdated = false;

            foreach (var tile in missing)
            {
                string? path = await service.GetBoxArtAsync(tile.Game);
                if (path != null)
                {
                    // Update ViewModel — binding auto-updates the UI
                    Dispatcher.Invoke(() => tile.BoxArtPath = path);

                    var dbGame = await db.Games.FindAsync(tile.Id);
                    if (dbGame != null) dbGame.BoxArtPath = path;
                    anyUpdated = true;
                }
            }

            if (anyUpdated)
                await db.SaveChangesAsync();
        }

        private void BuildPlatformList()
        {
            PlatformPanel.Children.Clear();
            PlatformPanel.Children.Add(MakePlatformButton("All", _allGames.Count));

            foreach (var group in _allGames.GroupBy(g => g.Platform).OrderBy(g => g.Key))
                PlatformPanel.Children.Add(MakePlatformButton(group.Key, group.Count()));
        }

        private Button MakePlatformButton(string platform, int count)
        {
            var btn = new Button
            {
                Tag = platform,
                Height = 36,
                Background = platform == _currentPlatform
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e94560"))
                    : Brushes.Transparent,
                Foreground = platform == _currentPlatform ? Brushes.White
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 0, 8, 0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13
            };

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
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#636e72")),
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
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.MarginProperty, new Thickness(4, 0, 0, 0));
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
            if (GamesItemsControl == null) return;
            if (_allGames == null) return;

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
                _tiles = new ObservableCollection<GameTileViewModel>(
                    list.Select(g => new GameTileViewModel(g)));
                GamesItemsControl.ItemsSource = _tiles;

                // Fetch art for first visible batch
                _ = FetchMissingBoxArtAsync(_tiles.Take(30).ToList());
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
                    SizeDisplay = g.FileSizeGB.HasValue ? $"{g.FileSizeGB:F1} GB" : "-"
                }).ToList();
            }
        }

        private void OnGameClicked(GameTileViewModel? tile)
        {
            if (tile == null) return;
            // Detail panel — next step
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

    // Simple relay command helper
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        public RelayCommand(Action<T?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter is T t ? t : default);
        public event EventHandler? CanExecuteChanged;
    }
}
