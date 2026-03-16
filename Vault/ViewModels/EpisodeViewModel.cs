using System.ComponentModel;
using System.IO;
using Vault.Models;

namespace Vault.ViewModels
{
    public class EpisodeViewModel : INotifyPropertyChanged
    {
        private Episode _episode;
        private double _cardWidth = 160;

        public EpisodeViewModel(Episode episode, double cardWidth = 160)
        {
            _episode = episode;
            _cardWidth = cardWidth;
        }

        public int Id => _episode.Id;
        public int SeasonNumber => _episode.SeasonNumber;
        public int EpisodeNumber => _episode.EpisodeNumber;
        public string? Title => _episode.Title;
        public string? FilePath => _episode.FilePath;
        public Episode Episode => _episode;

        public string EpisodeLabel => $"S{SeasonNumber:D2}E{EpisodeNumber:D2}";

        public bool IsWatched
        {
            get => _episode.IsWatched;
            set
            {
                _episode.IsWatched = value;
                OnPropertyChanged(nameof(IsWatched));
            }
        }

        public long ResumePositionSeconds
        {
            get => _episode.ResumePositionSeconds;
            set
            {
                _episode.ResumePositionSeconds = value;
                OnPropertyChanged(nameof(ResumePositionSeconds));
                OnPropertyChanged(nameof(ResumeBarWidth));
            }
        }

        public string? ThumbnailPath => !string.IsNullOrEmpty(_episode.ThumbnailPath) &&
                                         File.Exists(_episode.ThumbnailPath)
            ? _episode.ThumbnailPath : null;

        public bool HasThumbnail => ThumbnailPath != null;

        // Width of the resume progress bar on the episode card
        public double ResumeBarWidth
        {
            get
            {
                if (_episode.RuntimeMinutes <= 0 || _episode.ResumePositionSeconds <= 0)
                    return 0;
                double totalSeconds = _episode.RuntimeMinutes * 60.0;
                double pct = _episode.ResumePositionSeconds / totalSeconds;
                return System.Math.Min(pct, 1.0) * _cardWidth;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}