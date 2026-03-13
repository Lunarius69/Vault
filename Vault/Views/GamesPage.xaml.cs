using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vault.Database;
using Vault.Models;

namespace Vault.Views
{
    public partial class GamesPage : Page
    {
        private AppSettings _settings;
        private List<Game> _allGames = new();
        private List<Game> _filteredGames = new();
        private string _currentPlatform = "All";
        private string _currentStatus = "All";
        private bool _isGridView = true;

        public GamesPage(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
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

        private async Task FetchMissingBoxArtAsync(List<Game> games)
        {
            if (string.IsNullOrEmpty(_settings.SteamGridDbApiKey)) return;

            var service = new Vault.Services.BoxArtService(_settings);
            var missing = games
                .Where(g => string.IsNullOrEmpty(g.BoxArtPath) || !System.IO.File.Exists(g.BoxArtPath))
                .ToList();

            if (missing.Count == 0) return;

            using var db = new VaultContext();
            bool anyUpdated = false;

            foreach (var game in missing)
            {
                string? path = await service.GetBoxArtAsync(game);
                if (path != null)
                {
                    game.BoxArtPath = path;
                    anyUpdated = true;

                    var dbGame = await db.Games.FindAsync(game.Id);
                    if (dbGame != null) dbGame.BoxArtPath = path;

                    Dispatcher.Invoke(() =>
                    {
                        int idx = _filteredGames.IndexOf(game);
                        if (idx >= 0 && idx < GamesWrapPanel.Children.Count)
                        {
                            GamesWrapPanel.Children.RemoveAt(idx);
                            GamesWrapPanel.Children.Insert(idx, MakeGameTile(game));
                        }
                    });
                }
            }

            if (anyUpdated)
                await db.SaveChangesAsync();
        }

        private void BuildPlatformList()
        {
            PlatformPanel.Children.Clear();
            PlatformPanel.Children.Add(MakePlatformButton("All", _allGames.Count));

            var platforms = _allGames
                .GroupBy(g => g.Platform)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in platforms)
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
                Cursor = System.Windows.Input.Cursors.Hand,
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
                new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
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
            if (GamesWrapPanel == null) return;
            if (_allGames == null) return;

            _filteredGames = _allGames.ToList();

            if (_currentPlatform != "All")
                _filteredGames = _filteredGames.Where(g => g.Platform == _currentPlatform).ToList();

            if (_currentStatus == "Downloaded")
                _filteredGames = _filteredGames.Where(g => g.IsDownloaded).ToList();
            else if (_currentStatus != "All")
                _filteredGames = _filteredGames.Where(g => g.Status == _currentStatus).ToList();

            int sortIdx = SortCombo?.SelectedIndex ?? 0;
            _filteredGames = sortIdx switch
            {
                0 => _filteredGames.OrderBy(g => g.Title).ToList(),
                1 => _filteredGames.OrderByDescending(g => g.Title).ToList(),
                2 => _filteredGames.OrderByDescending(g => g.Year).ToList(),
                3 => _filteredGames.OrderBy(g => g.Year).ToList(),
                4 => _filteredGames.OrderBy(g => g.Platform).ThenBy(g => g.Title).ToList(),
                5 => _filteredGames.OrderByDescending(g => g.PlaytimeMinutes).ToList(),
                _ => _filteredGames
            };

            if (TxtGameCount != null)
                TxtGameCount.Text = $"{_filteredGames.Count} games";

            if (_isGridView) RenderGrid();
            else RenderList();
        }

        private void RenderGrid()
        {
            GamesWrapPanel.Children.Clear();
            foreach (var game in _filteredGames)
                GamesWrapPanel.Children.Add(MakeGameTile(game));

            _ = FetchMissingBoxArtAsync(_filteredGames.Take(30).ToList());
        }

        private void RenderList()
        {
            GamesListView.ItemsSource = _filteredGames.Select(g => new
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

        private Border MakeGameTile(Game game)
        {
            var tile = new Border
            {
                Width = 150,
                Height = 210,
                Margin = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16213e")),
                Cursor = System.Windows.Input.Cursors.Hand,
                ClipToBounds = true
            };

            var grid = new Grid();

            if (!string.IsNullOrEmpty(game.BoxArtPath) && System.IO.File.Exists(game.BoxArtPath))
            {
                grid.Children.Add(new Image
                {
                    Source = new BitmapImage(new Uri(game.BoxArtPath)),
                    Stretch = Stretch.UniformToFill
                });
            }
            else
            {
                var placeholder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0f3460"))
                };
                placeholder.Child = new TextBlock
                {
                    Text = game.Title,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(8)
                };
                grid.Children.Add(placeholder);
            }

            var gradient = new Border { VerticalAlignment = VerticalAlignment.Bottom, Height = 70 };
            gradient.Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Color.FromArgb(230, 22, 33, 62), 1)
                },
                new Point(0, 0), new Point(0, 1));
            grid.Children.Add(gradient);

            var info = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 0, 8, 8)
            };

            var badge = new Border
            {
                Background = new SolidColorBrush(GetPlatformColor(game.Platform)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4)
            };
            badge.Child = new TextBlock
            {
                Text = GetPlatformShort(game.Platform),
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI")
            };
            info.Children.Add(badge);

            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal };
            statusPanel.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(GetStatusColor(game.Status)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            statusPanel.Children.Add(new TextBlock
            {
                Text = game.Status,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center
            });
            info.Children.Add(statusPanel);
            grid.Children.Add(info);

            if (!game.IsDownloaded)
            {
                var overlay = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0))
                };
                overlay.Child = new TextBlock
                {
                    Text = "⬇ Not Downloaded",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#636e72")),
                    FontSize = 11,
                    FontFamily = new FontFamily("Segoe UI"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.Children.Add(overlay);
            }

            tile.Child = grid;
            tile.MouseLeftButtonUp += (s, e) => OpenGameDetail(game);
            return tile;
        }

        private void OpenGameDetail(Game game) { }

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
            _filteredGames = _allGames
                .Where(g => g.Title.ToLower().Contains(query) ||
                            g.Platform.ToLower().Contains(query) ||
                            (g.Genre != null && g.Genre.ToLower().Contains(query)))
                .ToList();
            if (TxtGameCount != null)
                TxtGameCount.Text = $"{_filteredGames.Count} games";
            if (_isGridView) RenderGrid(); else RenderList();
        }

        private static Color GetPlatformColor(string platform)
        {
            return platform?.ToLower() switch
            {
                var p when p.Contains("pc") => (Color)ColorConverter.ConvertFromString("#0078d4"),
                var p when p.Contains("ps5") => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p.Contains("ps4") => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p.Contains("ps3") => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p.Contains("ps2") => (Color)ColorConverter.ConvertFromString("#003087"),
                var p when p.Contains("switch") => (Color)ColorConverter.ConvertFromString("#e60012"),
                var p when p.Contains("xbox") => (Color)ColorConverter.ConvertFromString("#107c10"),
                var p when p.Contains("gamecube") || p.Contains("wii") => (Color)ColorConverter.ConvertFromString("#6a0dad"),
                var p when p.Contains("psp") || p.Contains("vita") => (Color)ColorConverter.ConvertFromString("#003087"),
                _ => (Color)ColorConverter.ConvertFromString("#2d3561")
            };
        }

        private static string GetPlatformShort(string platform)
        {
            return platform?.ToLower() switch
            {
                var p when p.Contains("playstation 5") || p.Contains("ps5") => "PS5",
                var p when p.Contains("playstation 4") || p.Contains("ps4") => "PS4",
                var p when p.Contains("playstation 3") || p.Contains("ps3") => "PS3",
                var p when p.Contains("playstation 2") || p.Contains("ps2") => "PS2",
                var p when p.Contains("switch 2") => "NSW2",
                var p when p.Contains("switch") => "NSW",
                var p when p.Contains("xbox 360") => "X360",
                var p when p.Contains("xbox") => "XBOX",
                var p when p.Contains("gamecube") => "GCN",
                var p when p.Contains("wii u") => "WiiU",
                var p when p.Contains("wii") => "Wii",
                var p when p.Contains("pc") => "PC",
                var p when p.Contains("psp") => "PSP",
                var p when p.Contains("vita") => "Vita",
                var p when p.Contains("3ds") => "3DS",
                var p when p.Contains("ds") => "DS",
                _ => platform?.Length > 6 ? platform[..6] : platform ?? "?"
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
