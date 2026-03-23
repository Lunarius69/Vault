using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using Vault.Models;

namespace Vault.ViewModels
{
    public class MediaTileViewModel : INotifyPropertyChanged
    {
        private MediaItem _item;
        private string? _posterPath;
        private BitmapImage? _loadedBitmap;

        public MediaTileViewModel(MediaItem item)
        {
            _item = item;
            _posterPath = item.PosterPath;

            // Load existing local posters synchronously — they're already on disk
            // so this is fast and avoids the blank tile flash
            if (!string.IsNullOrEmpty(_posterPath) && File.Exists(_posterPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(_posterPath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 150;
                    bmp.EndInit();
                    bmp.Freeze();
                    _loadedBitmap = bmp;
                }
                catch { }
            }
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
                OnPropertyChanged(nameof(PosterPath));
                OnPropertyChanged(nameof(HasPoster));
            }
        }

        /// <summary>
        /// Pre-loaded bitmap set by async loader — bind Image.Source to this
        /// instead of PosterPath to avoid synchronous disk I/O on the UI thread.
        /// </summary>
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

        public bool HasPoster =>
            _loadedBitmap != null ||
            (!string.IsNullOrEmpty(_posterPath) && File.Exists(_posterPath));

        public double ProgressPercent
        {
            get
            {
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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}