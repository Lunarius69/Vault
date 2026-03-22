using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
        private CancellationTokenSource? _artFetchCts;

        public event EventHandler<Game>? GameSelected;

        public GamesPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DataContext = this;
            Loaded += GamesPage_Loaded;
        }

        public async Task RefreshAsync()
        {
            using var db = new VaultContext();
            _allGames = await db.Games
                .Where(g => !g.IsWishlist)
                .OrderBy(g => g.Title)
                .ToListAsync();
            BuildPlatformList();
            ApplyFilters();
        }

        private async void GamesPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            using (var cleanDb = new VaultContext())
            {
                var badGames = await cleanDb.Games
                    .Where(g => g.Title == "v8_context_snapshot" ||
                                g.ManuallyMarkedNotDownloaded == true)
                    .ToListAsync();
                foreach (var g in badGames)
                {
                    g.IsDownloaded = false;
                    g.ExePath = null;
                    g.EmulatorPath = null;
                    g.ManuallyMarkedNotDownloaded = true;
                }
                if (badGames.Any())
                    await cleanDb.SaveChangesAsync();
            }

            var detector = new AutoDetectService(_settings);
            await detector.ScanAndUpdateAsync();

            using var db = new VaultContext();
            _allGames = await db.Games
                .Where(g => !g.IsWishlist)
                .OrderBy(g => g.Title)
                .ToListAsync();

            LoadingOverlay.Visibility = Visibility.Collapsed;
            BuildPlatformList();
            ApplyFilters();

            _ = FetchAllMissingBoxArtAsync();
        }

        private string GetAttemptCacheFile()
        {
            string folder = string.IsNullOrEmpty(_settings.DataFolderPath)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Vault", "cache")
                : System.IO.Path.Combine(_settings.DataFolderPath, "cache");
            System.IO.Directory.CreateDirectory(folder);
            return System.IO.Path.Combine(folder, "art_attempted.txt");
        }

        private async Task FetchAllMissingBoxArtAsync()
        {
            if (string.IsNullOrEmpty(_settings.SteamGridDbApiKey)) return;

            string attemptCacheFile = GetAttemptCacheFile();

            var attempted = new HashSet<int>();
            if (System.IO.File.Exists(attemptCacheFile))
            {
                foreach (var line in await System.IO.File.ReadAllLinesAsync(attemptCacheFile))
                    if (int.TryParse(line.Trim(), out int id))
                        attempted.Add(id);
            }

            var toFetch = _allGames
                .Where(g => !attempted.Contains(g.Id) &&
                            (string.IsNullOrEmpty(g.BoxArtPath) ||
                             !System.IO.File.Exists(g.BoxArtPath)))
                .ToList();

            if (toFetch.Count == 0)
            {
                Dispatcher.Invoke(() => TxtArtStatus.Text = "All art loaded");
                return;
            }

            Dispatcher.Invoke(() => TxtArtStatus.Text = $"Fetching art: 0/{toFetch.Count}");

            _artFetchCts?.Cancel();
            _artFetchCts = new CancellationTokenSource();
            var token = _artFetchCts.Token;

            try
            {
                var steamGridService = new BoxArtService(_settings);
                var openVgdbService = new OpenVGDBService(_settings);
                using var db = new VaultContext();
                var dbLock = new object();
                var newAttempted = new ConcurrentBag<int>();
                int completed = 0;
                var semaphore = new SemaphoreSlim(3);

                var tasks = toFetch.Select(async game =>
                {
                    if (token.IsCancellationRequested) return;
                    await semaphore.WaitAsync(token);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        await Task.Delay(200, token);

                        // Try SteamGridDB first
                        string? path = await steamGridService.GetBoxArtAsync(game);

                        // If SteamGridDB failed, try OpenVGDB as fallback
                        if (path == null && openVgdbService.IsConfigured)
                            path = await openVgdbService.GetBoxArtAsync(game);

                        if (path != null)
                        {
                            game.BoxArtPath = path;

                            // Only mark as attempted if successful
                            newAttempted.Add(game.Id);

                            Dispatcher.Invoke(() =>
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    var tile = _tiles.FirstOrDefault(t => t.Id == game.Id);
                                    if (tile != null) tile.BoxArtPath = path;
                                }
                            });

                            lock (dbLock)
                            {
                                var dbGame = db.Games.Find(game.Id);
                                if (dbGame != null) dbGame.BoxArtPath = path;
                            }

                            int done = Interlocked.Increment(ref completed);
                            Dispatcher.Invoke(() =>
                                TxtArtStatus.Text = $"Fetching art: {done}/{toFetch.Count}");

                            if (done % 10 == 0 && !token.IsCancellationRequested)
                                await db.SaveChangesAsync();
                        }
                        // If both failed — do NOT add to newAttempted, retry next session
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    finally { semaphore.Release(); }
                });

                await Task.WhenAll(tasks);

                if (!token.IsCancellationRequested)
                {
                    await db.SaveChangesAsync();

                    var allAttempted = attempted
                        .Concat(newAttempted)
                        .Distinct()
                        .Select(id => id.ToString());
                    await System.IO.File.WriteAllLinesAsync(attemptCacheFile, allAttempted);

                    Dispatcher.Invoke(() => TxtArtStatus.Text = completed > 0
                        ? $"Done — {completed} new art fetched"
                        : "All art loaded");
                }
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() => TxtArtStatus.Text = "Art fetch cancelled");
            }
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
                Foreground = platform == _currentPlatform
                    ? Brushes.White
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
                var existingArt = _tiles.ToDictionary(t => t.Id, t => t.BoxArtPath);

                _tiles = new ObservableCollection<GameTileViewModel>(
                    list.Select(g =>
                    {
                        var tile = new GameTileViewModel(g);
                        if (existingArt.TryGetValue(g.Id, out string? cachedPath)
                            && cachedPath != null)
                            tile.BoxArtPath = cachedPath;
                        return tile;
                    }));

                GamesItemsControl.ItemsSource = _tiles;
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

        private async void BtnScanRoms_Click(object sender, RoutedEventArgs e)
        {
            BtnScanRoms.IsEnabled = false;
            BtnScanRoms.Content = "⟳  Scanning...";

            try
            {
                var importer = new RomImportService(_settings);
                var progress = new Progress<string>(msg =>
                    Dispatcher.Invoke(() => BtnScanRoms.Content = $"⟳  {msg}"));

                int imported = await importer.ScanAndImportAsync(progress);

                using var db = new VaultContext();
                _allGames = await db.Games
                    .Where(g => !g.IsWishlist)
                    .OrderBy(g => g.Title)
                    .ToListAsync();

                BuildPlatformList();
                ApplyFilters();

                BtnScanRoms.Content = imported > 0
                    ? $"✓  {imported} imported"
                    : "✓  Up to date";

                await Task.Delay(3000);
                BtnScanRoms.Content = "⟳  Scan ROMs";
            }
            catch (Exception ex)
            {
                BtnScanRoms.Content = "✗  Scan failed";
                MessageBox.Show($"ROM scan failed: {ex.Message}",
                    "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
                await Task.Delay(2000);
                BtnScanRoms.Content = "⟳  Scan ROMs";
            }
            finally
            {
                BtnScanRoms.IsEnabled = true;
            }
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

            var existingArt = _tiles.ToDictionary(t => t.Id, t => t.BoxArtPath);

            _tiles = new ObservableCollection<GameTileViewModel>(
                list.Select(g =>
                {
                    var tile = new GameTileViewModel(g);
                    if (existingArt.TryGetValue(g.Id, out string? cachedPath)
                        && cachedPath != null)
                        tile.BoxArtPath = cachedPath;
                    return tile;
                }));

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