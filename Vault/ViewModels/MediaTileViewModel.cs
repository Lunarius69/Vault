// ViewModels/MediaTileViewModel.cs
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Vault.Models;

namespace Vault.ViewModels
{
    public class MediaTileViewModel : INotifyPropertyChanged, IDisposable
    {
        private MediaItem _item;
        private string? _posterPath;
        private BitmapImage? _loadedBitmap;
        private bool _disposed;

        private static readonly SemaphoreSlim _loadThrottle = new SemaphoreSlim(4, 4);

        // Per-mediaType cache so Movies, Anime, AnimatedSeries etc. never share
        // bitmaps. A wrong fetch in one category can never bleed into another,
        // and clearing one type's cache doesn't affect the others.
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, BitmapImage?>>
            _typedCache = new();

        private ConcurrentDictionary<int, BitmapImage?> MyCache =>
            _typedCache.GetOrAdd(_item.MediaType, _ => new());

        public MediaTileViewModel(MediaItem item)
        {
            _item = item;
            _posterPath = item.PosterPath;

            if (MyCache.TryGetValue(item.Id, out var cached))
                _loadedBitmap = cached;
            else if (!string.IsNullOrEmpty(_posterPath) && File.Exists(_posterPath))
                _ = LoadPosterAsync();
        }

        public int Id => _item.Id;
        public string Title => _item.Title;
        public string MediaType => _item.MediaType;
        public string WatchStatus => _item.WatchStatus;
        public int? Year => _item.Year;
        public MediaItem Item => _item;

        public string? PosterPath
        {
            get => _posterPath;
            set
            {
                _posterPath = value;
                _item.PosterPath = value;
                MyCache.TryRemove(_item.Id, out _);
                OnPropertyChanged(nameof(PosterPath));
                OnPropertyChanged(nameof(HasPoster));
                _ = LoadPosterAsync();
            }
        }

        public BitmapImage? LoadedBitmap
        {
            get => _loadedBitmap;
            set
            {
                _loadedBitmap = value;
                OnPropertyChanged(nameof(LoadedBitmap));
                OnPropertyChanged(nameof(HasPoster));
            }
        }

        public bool HasPoster => _loadedBitmap != null ||
            (!string.IsNullOrEmpty(_posterPath) && File.Exists(_posterPath));

        public double ProgressPercent
        {
            get
            {
                if (IsMovie)
                {
                    if (_item.WatchStatus == "Completed") return 0;
                    if (_item.RuntimeMinutes <= 0 || _item.ResumePositionSeconds <= 0) return 0;
                    return Math.Min(100, (_item.ResumePositionSeconds / (_item.RuntimeMinutes * 60.0)) * 100.0);
                }
                if (_item.TotalEpisodes <= 0) return 0;
                return (_item.WatchedEpisodes / (double)_item.TotalEpisodes) * 100.0;
            }
        }

        public bool IsMovie => _item.MediaType == "Movie" ||
                               _item.MediaType == "AnimeMovie" ||
                               _item.MediaType == "AnimatedMovie";

        public string StatusColor => _item.WatchStatus switch
        {
            "Watching" => "#00b894",
            "Completed" => "#0984e3",
            "Not Started" => "#636e72",
            "On Hold" => "#fdcb6e",
            "Dropped" => "#d63031",
            _ => "#636e72"
        };

        public string EpisodeInfo
        {
            get
            {
                if (IsMovie) return "";
                if (_item.TotalEpisodes > 0)
                    return $"EP {_item.WatchedEpisodes}/{_item.TotalEpisodes}";
                return "";
            }
        }

        private async System.Threading.Tasks.Task LoadPosterAsync()
        {
            var path = _posterPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var cache = MyCache;

            if (cache.TryGetValue(_item.Id, out var cached))
            {
                await Application.Current.Dispatcher.InvokeAsync(() => LoadedBitmap = cached);
                return;
            }

            await _loadThrottle.WaitAsync();
            try
            {
                if (cache.TryGetValue(_item.Id, out cached))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => LoadedBitmap = cached);
                    return;
                }

                var bitmap = await System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(path);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 150;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                    catch { return null; }
                });

                cache[_item.Id] = bitmap;
                await Application.Current.Dispatcher.InvokeAsync(() => LoadedBitmap = bitmap);
            }
            catch { }
            finally { _loadThrottle.Release(); }
        }

        public static void InvalidateCache(int itemId)
        {
            foreach (var cache in _typedCache.Values)
                cache.TryRemove(itemId, out _);
        }

        public static void ClearCacheForType(string mediaType)
        {
            if (_typedCache.TryGetValue(mediaType, out var cache))
                cache.Clear();
        }

        public static void ClearAllCache() => _typedCache.Clear();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _loadedBitmap = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}