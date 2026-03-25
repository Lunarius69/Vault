// ViewModels/GameTileViewModel.cs
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vault.Models;

namespace Vault.ViewModels
{
    public class GameTileViewModel : INotifyPropertyChanged, IDisposable
    {
        private Game _game;
        private string? _boxArtPath;
        private bool? _hasBoxArtCache;
        private BitmapImage? _loadedBoxArt;
        private bool _disposed;
        private static bool _isLoadingImages = false;

        // Static cache for loaded images - this persists across ViewModel instances
        private static readonly ConcurrentDictionary<int, BitmapImage?> _imageCache = new();

        // Static caches for brushes
        private static readonly ConcurrentDictionary<string, SolidColorBrush> _platformBrushCache = new();
        private static readonly ConcurrentDictionary<string, SolidColorBrush> _statusBrushCache = new();

        public GameTileViewModel(Game game)
        {
            _game = game;
            _boxArtPath = game.BoxArtPath;

            // Try to load from cache first
            if (_imageCache.TryGetValue(game.Id, out var cachedImage))
            {
                _loadedBoxArt = cachedImage;
            }
            else if (!string.IsNullOrEmpty(_boxArtPath) && File.Exists(_boxArtPath))
            {
                // Load asynchronously if not in cache
                _ = LoadBoxArtAsync();
            }
        }

        public int Id => _game.Id;
        public string Title => _game.Title;
        public string Platform => _game.Platform;
        public string Status => _game.Status;
        public bool IsDownloaded => _game.IsDownloaded;
        public int? Year => _game.Year;
        public Game Game => _game;

        public string? BoxArtPath
        {
            get => _boxArtPath;
            set
            {
                _boxArtPath = value;
                _game.BoxArtPath = value;
                _hasBoxArtCache = null;
                OnPropertyChanged(nameof(BoxArtPath));
                OnPropertyChanged(nameof(HasBoxArt));

                // Clear cache for this game and reload
                _imageCache.TryRemove(_game.Id, out _);
                _ = LoadBoxArtAsync();
            }
        }

        public BitmapImage? LoadedBoxArt
        {
            get => _loadedBoxArt;
            private set
            {
                if (_loadedBoxArt == value) return;
                _loadedBoxArt = value;
                OnPropertyChanged(nameof(LoadedBoxArt));
                OnPropertyChanged(nameof(HasBoxArt));
            }
        }

        public bool HasBoxArt
        {
            get
            {
                if (_loadedBoxArt != null) return true;
                if (!_hasBoxArtCache.HasValue)
                    _hasBoxArtCache = !string.IsNullOrEmpty(_boxArtPath) && File.Exists(_boxArtPath);
                return _hasBoxArtCache.Value;
            }
        }

        public string PlatformShort => GetPlatformShort(_game.Platform);

        public Brush PlatformColor => _platformBrushCache.GetOrAdd(_game.Platform ?? "", p =>
        {
            var brush = new SolidColorBrush(GetPlatformColor(p));
            brush.Freeze();
            return brush;
        });

        public Brush StatusColor => _statusBrushCache.GetOrAdd(_game.Status ?? "", s =>
        {
            var brush = new SolidColorBrush(GetStatusColor(s));
            brush.Freeze();
            return brush;
        });

        /// <summary>
        /// Updates the game data without recreating the ViewModel
        /// This preserves the loaded image and prevents unnecessary reloads
        /// </summary>
        public void UpdateGame(Game updatedGame)
        {
            if (updatedGame == null) return;

            // Update properties that might have changed
            if (_game.Title != updatedGame.Title)
            {
                _game.Title = updatedGame.Title;
                OnPropertyChanged(nameof(Title));
            }

            if (_game.Platform != updatedGame.Platform)
            {
                _game.Platform = updatedGame.Platform;
                OnPropertyChanged(nameof(Platform));
                OnPropertyChanged(nameof(PlatformShort));
            }

            if (_game.Status != updatedGame.Status)
            {
                _game.Status = updatedGame.Status;
                OnPropertyChanged(nameof(Status));
            }

            if (_game.IsDownloaded != updatedGame.IsDownloaded)
            {
                _game.IsDownloaded = updatedGame.IsDownloaded;
                OnPropertyChanged(nameof(IsDownloaded));
            }

            if (_game.Year != updatedGame.Year)
            {
                _game.Year = updatedGame.Year;
                OnPropertyChanged(nameof(Year));
            }

            if (_game.PlaytimeMinutes != updatedGame.PlaytimeMinutes)
            {
                _game.PlaytimeMinutes = updatedGame.PlaytimeMinutes;
            }

            if (_game.FileSizeGB != updatedGame.FileSizeGB)
            {
                _game.FileSizeGB = updatedGame.FileSizeGB;
            }

            // Only reload box art if it changed
            if (_boxArtPath != updatedGame.BoxArtPath)
            {
                BoxArtPath = updatedGame.BoxArtPath;
            }

            // Update the game reference to the new one
            _game = updatedGame;
        }

        private async System.Threading.Tasks.Task LoadBoxArtAsync()
        {
            if (string.IsNullOrEmpty(_boxArtPath) || !File.Exists(_boxArtPath))
                return;

            try
            {
                // Prevent multiple simultaneous loads
                if (_isLoadingImages)
                {
                    // Wait a bit and try again
                    await System.Threading.Tasks.Task.Delay(50);
                    if (_imageCache.TryGetValue(_game.Id, out var cached))
                    {
                        LoadedBoxArt = cached;
                        return;
                    }
                }

                _isLoadingImages = true;

                // Load on background thread
                var bitmap = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(_boxArtPath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 200; // Thumbnail size
                        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // Don't use WPF's cache
                        bmp.EndInit();
                        bmp.Freeze(); // Allow cross-thread access
                        return bmp;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to load image {_boxArtPath}: {ex.Message}");
                        return null;
                    }
                });

                // Cache the result
                _imageCache[_game.Id] = bitmap;
                LoadedBoxArt = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading box art for {Title}: {ex.Message}");
            }
            finally
            {
                _isLoadingImages = false;
            }
        }

        private static Color GetPlatformColor(string platform)
        {
            var c = ColorConverter.ConvertFromString;
            return platform?.ToLower() switch
            {
                var p when p != null && p.Contains("pc") => (Color)c("#0078d4"),
                var p when p != null && p.Contains("ps5") => (Color)c("#003087"),
                var p when p != null && p.Contains("ps4") => (Color)c("#003087"),
                var p when p != null && p.Contains("ps3") => (Color)c("#003087"),
                var p when p != null && p.Contains("ps2") => (Color)c("#003087"),
                var p when p != null && p.Contains("switch") => (Color)c("#e60012"),
                var p when p != null && p.Contains("xbox") => (Color)c("#107c10"),
                var p when p != null && (p.Contains("gamecube") || p.Contains("wii")) => (Color)c("#6a0dad"),
                var p when p != null && (p.Contains("psp") || p.Contains("vita")) => (Color)c("#003087"),
                _ => (Color)c("#2d3561")
            };
        }

        private static string GetPlatformShort(string platform)
        {
            return platform?.ToLower() switch
            {
                var p when p != null && (p.Contains("playstation 5") || p.Contains("ps5")) => "PS5",
                var p when p != null && (p.Contains("playstation 4") || p.Contains("ps4")) => "PS4",
                var p when p != null && (p.Contains("playstation 3") || p.Contains("ps3")) => "PS3",
                var p when p != null && (p.Contains("playstation 2") || p.Contains("ps2")) => "PS2",
                var p when p != null && p.Contains("switch 2") => "NSW2",
                var p when p != null && p.Contains("switch") => "NSW",
                var p when p != null && p.Contains("xbox 360") => "X360",
                var p when p != null && p.Contains("xbox") => "XBOX",
                var p when p != null && p.Contains("gamecube") => "GCN",
                var p when p != null && p.Contains("wii u") => "WiiU",
                var p when p != null && p.Contains("wii") => "Wii",
                var p when p != null && p.Contains("pc") => "PC",
                var p when p != null && p.Contains("psp") => "PSP",
                var p when p != null && p.Contains("vita") => "Vita",
                var p when p != null && p.Contains("3ds") => "3DS",
                var p when p != null && p.Contains("ds") => "DS",
                _ => platform?.Length > 6 ? platform[..6] : platform ?? "?"
            };
        }

        private static Color GetStatusColor(string status)
        {
            var c = ColorConverter.ConvertFromString;
            return status?.ToLower() switch
            {
                "playing" => (Color)c("#00b894"),
                "completed" => (Color)c("#0984e3"),
                "not started" => (Color)c("#636e72"),
                "on hold" => (Color)c("#fdcb6e"),
                "dropped" => (Color)c("#d63031"),
                _ => (Color)c("#636e72")
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Don't dispose the image - it's cached
            LoadedBoxArt = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}