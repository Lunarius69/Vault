using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vault.Database;
using Vault.Models;
using Vault.Services;
using Vault.ViewModels;

namespace Vault.Views
{
    public partial class MediaDetailPage : UserControl
    {
        private MediaItem _item;
        private readonly AppSettings _settings;
        private readonly TmdbService _tmdb;
        private int _currentSeason = 1;
        private List<Episode> _allEpisodes = new();
        private bool _isMovie;

        public event EventHandler? BackRequested;

        public MediaDetailPage(MediaItem item, AppSettings settings)
        {
            InitializeComponent();
            _item = item;
            _settings = settings;
            _tmdb = new TmdbService(settings);
            _isMovie = item.MediaType == "Movie" ||
                       item.MediaType == "AnimeMovie" ||
                       item.MediaType == "AnimatedMovie";
            Loaded += (s, e) => _ = LoadPageAsync();
        }

        private async Task LoadPageAsync()
        {
            // Core info
            TxtTitle.Text = _item.Title;
            TxtMediaType.Text = _item.MediaType switch
            {
                "Show" => "TV Show",
                "Anime" => "Anime",
                "AnimatedSeries" => "Animated Series",
                "Movie" => "Movie",
                "AnimeMovie" => "Anime Movie",
                "AnimatedMovie" => "Animated Movie",
                _ => _item.MediaType
            };

            TxtStatus.Text = _item.WatchStatus;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(_item.WatchStatus));
            TxtYear.Text = _item.Year?.ToString() ?? "—";
            TxtGenre.Text = string.IsNullOrEmpty(_item.Genre) ? "—" : _item.Genre;
            TxtRating.Text = _item.TmdbRating.HasValue
                ? $"★ {_item.TmdbRating:F1}" : "—";
            TxtDescription.Text = string.IsNullOrEmpty(_item.Description)
                ? "No description available." : _item.Description;

            // Show/hide episode section
            EpisodesSection.Visibility = _isMovie
                ? Visibility.Collapsed : Visibility.Visible;

            // Seasons / episodes count
            if (_isMovie)
            {
                TxtSeasons.Text = "—";
                TxtEpisodes.Text = "—";
                TxtProgress.Text = _item.WatchStatus == "Completed"
                    ? "Watched" : "Not Watched";
            }
            else
            {
                TxtSeasons.Text = _item.TotalSeasons?.ToString() ?? "—";
                TxtEpisodes.Text = _item.TotalEpisodes > 0
                    ? _item.TotalEpisodes.ToString() : "—";
                TxtProgress.Text = _item.TotalEpisodes > 0
                    ? $"{_item.WatchedEpisodes} / {_item.TotalEpisodes}"
                    : "—";
            }

            // Play button label
            UpdatePlayButton();

            // Load poster
            LoadPoster();
            RefreshProgressBar();

            // Load banner
            if (!string.IsNullOrEmpty(_item.BannerPath) &&
                File.Exists(_item.BannerPath))
            {
                var bmp = LoadBitmap(_item.BannerPath);
                if (bmp != null) ImgBanner.Source = bmp;
            }

            // Fetch from TMDB if missing data
            if (_item.TmdbId == 0 || string.IsNullOrEmpty(_item.Description))
                await FetchTmdbDataAsync();

            // Load episodes for series
            if (!_isMovie)
            {
                await LoadEpisodesAsync();
                BuildSeasonTabs();
                await ShowSeasonAsync(_currentSeason);
                UpdateNextEpisodePanel();
            }
        }

        private void LoadPoster()
        {
            if (!string.IsNullOrEmpty(_item.PosterPath) &&
                File.Exists(_item.PosterPath))
            {
                var bmp = LoadBitmap(_item.PosterPath);
                if (bmp != null)
                {
                    ImgPoster.Source = bmp;
                    ImgPoster.Visibility = Visibility.Visible;
                    PlaceholderBg.Visibility = Visibility.Collapsed;
                    TxtPlaceholder.Visibility = Visibility.Collapsed;
                    return;
                }
            }
            ImgPoster.Visibility = Visibility.Collapsed;
            PlaceholderBg.Visibility = Visibility.Visible;
            TxtPlaceholder.Visibility = Visibility.Visible;
            TxtPlaceholder.Text = _item.Title;
        }

        private async Task FetchTmdbDataAsync()
        {
            if (!_tmdb.IsConfigured) return;

            int tmdbId = _item.TmdbId;
            if (tmdbId == 0)
            {
                int? found = await _tmdb.SearchAsync(_item.Title, !_isMovie);
                if (found == null) return;
                tmdbId = found.Value;
            }

            var details = await _tmdb.FetchDetailsAsync(tmdbId, !_isMovie);
            if (details == null) return;

            // Download poster if missing
            string? posterPath = _item.PosterPath;
            if (string.IsNullOrEmpty(posterPath) && details.PosterPath != null)
                posterPath = await _tmdb.DownloadPosterAsync(_item.Id, details.PosterPath);

            // Download banner if missing
            string? bannerPath = _item.BannerPath;
            if (string.IsNullOrEmpty(bannerPath) && details.BackdropPath != null)
                bannerPath = await _tmdb.DownloadBannerAsync(_item.Id, details.BackdropPath);

            // Update DB
            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.TmdbId = tmdbId;
                if (posterPath != null) dbItem.PosterPath = posterPath;
                if (bannerPath != null) dbItem.BannerPath = bannerPath;
                if (details.Description != null) dbItem.Description = details.Description;
                if (details.Rating.HasValue) dbItem.TmdbRating = details.Rating;
                if (details.Year.HasValue && dbItem.Year == null) dbItem.Year = details.Year;
                if (details.Genre != null && dbItem.Genre == null) dbItem.Genre = details.Genre;
                if (details.TotalSeasons.HasValue) dbItem.TotalSeasons = details.TotalSeasons;
                if (details.TotalEpisodes.HasValue && dbItem.TotalEpisodes == 0)
                    dbItem.TotalEpisodes = details.TotalEpisodes.Value;
                await db.SaveChangesAsync();
            }

            // Update in-memory item and UI
            _item.TmdbId = tmdbId;
            if (posterPath != null) { _item.PosterPath = posterPath; LoadPoster(); }
            if (bannerPath != null)
            {
                _item.BannerPath = bannerPath;
                var bmp = LoadBitmap(bannerPath);
                if (bmp != null) ImgBanner.Source = bmp;
            }
            if (details.Description != null)
            {
                _item.Description = details.Description;
                TxtDescription.Text = details.Description;
            }
            if (details.Rating.HasValue)
            {
                _item.TmdbRating = details.Rating;
                TxtRating.Text = $"★ {details.Rating:F1}";
            }
            if (details.TotalSeasons.HasValue)
            {
                _item.TotalSeasons = details.TotalSeasons;
                TxtSeasons.Text = details.TotalSeasons.ToString();
            }
            if (details.TotalEpisodes.HasValue && _item.TotalEpisodes == 0)
            {
                _item.TotalEpisodes = details.TotalEpisodes.Value;
                TxtEpisodes.Text = details.TotalEpisodes.ToString();
            }
        }

        private async Task LoadEpisodesAsync()
        {
            using var db = new VaultContext();
            _allEpisodes = await db.Episodes
                .Where(e => e.MediaItemId == _item.Id)
                .OrderBy(e => e.SeasonNumber)
                .ThenBy(e => e.EpisodeNumber)
                .ToListAsync();

            // If no episodes in DB yet, fetch from TMDB
            if (_allEpisodes.Count == 0 && _item.TmdbId > 0 && _item.TotalSeasons > 0)
                await FetchAndSaveEpisodesAsync(db);
        }

        private async Task FetchAndSaveEpisodesAsync(VaultContext db)
        {
            if (!_tmdb.IsConfigured) return;

            TxtEpisodesLoading.Visibility = Visibility.Visible;
            int totalSeasons = _item.TotalSeasons ?? 1;

            for (int s = 1; s <= totalSeasons; s++)
            {
                var tmdbEps = await _tmdb.FetchSeasonEpisodesAsync(_item.TmdbId, s);
                foreach (var ep in tmdbEps)
                {
                    var episode = new Episode
                    {
                        MediaItemId = _item.Id,
                        SeasonNumber = s,
                        EpisodeNumber = ep.EpisodeNumber,
                        Title = ep.Title,
                        Description = ep.Description,
                        RuntimeMinutes = ep.RuntimeMinutes
                    };

                    // Download thumbnail in background
                    if (!string.IsNullOrEmpty(ep.ThumbnailPath))
                    {
                        string? thumbPath = await _tmdb.DownloadThumbnailAsync(
                            _item.Id * 1000 + ep.EpisodeNumber, ep.ThumbnailPath);
                        episode.ThumbnailPath = thumbPath;
                    }

                    db.Episodes.Add(episode);
                    _allEpisodes.Add(episode);
                }
            }

            await db.SaveChangesAsync();
            TxtEpisodesLoading.Visibility = Visibility.Collapsed;
        }

        private void BuildSeasonTabs()
        {
            SeasonTabsPanel.Children.Clear();
            if (_allEpisodes.Count == 0) return;

            var seasons = _allEpisodes
                .Select(e => e.SeasonNumber)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            foreach (int season in seasons)
            {
                var btn = new Button
                {
                    Content = $"Season {season}",
                    Tag = season,
                    Style = season == _currentSeason
                        ? (Style)FindResource("SeasonTabActive")
                        : (Style)FindResource("SeasonTab")
                };
                btn.Click += SeasonTab_Click;
                SeasonTabsPanel.Children.Add(btn);
            }
        }

        private async void SeasonTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int season)
            {
                _currentSeason = season;

                // Update tab styles
                foreach (Button tab in SeasonTabsPanel.Children)
                {
                    tab.Style = (int)tab.Tag == season
                        ? (Style)FindResource("SeasonTabActive")
                        : (Style)FindResource("SeasonTab");
                }

                await ShowSeasonAsync(season);
            }
        }

        private async Task ShowSeasonAsync(int season)
        {
            var episodes = _allEpisodes
                .Where(e => e.SeasonNumber == season)
                .OrderBy(e => e.EpisodeNumber)
                .ToList();

            var viewModels = episodes
                .Select(e => new EpisodeViewModel(e))
                .ToList();

            EpisodesItemsControl.ItemsSource =
                new ObservableCollection<EpisodeViewModel>(viewModels);

            await Task.CompletedTask;
        }

        private async void EpisodeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is EpisodeViewModel vm)
                await PlayEpisodeAsync(vm.Episode);
        }

        private async Task PlayEpisodeAsync(Episode episode)
        {
            // Find the file if not set yet
            string? filePath = episode.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                filePath = FindEpisodeFile(episode);
                if (filePath != null)
                {
                    using var db = new VaultContext();
                    var dbEp = await db.Episodes.FindAsync(episode.Id);
                    if (dbEp != null)
                    {
                        dbEp.FilePath = filePath;
                        episode.FilePath = filePath;
                        await db.SaveChangesAsync();
                    }
                }
            }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ShowMessage("File not found. Set the media folder first.", "#e17055");
                return;
            }

            // Launch VLC
            string vlcArgs = episode.ResumePositionSeconds > 0
                ? $"--start-time={episode.ResumePositionSeconds} \"{filePath}\""
                : $"\"{filePath}\"";

            string? vlcPath = FindVlc();
            if (vlcPath == null)
            {
                ShowMessage("VLC not found. Please install VLC.", "#e17055");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = vlcPath,
                Arguments = vlcArgs,
                UseShellExecute = false
            });

            // Mark episode as watched and update progress
            await MarkEpisodeWatchedAsync(episode);
        }

        private async Task MarkEpisodeWatchedAsync(Episode episode)
        {
            if (episode.IsWatched) return;

            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(episode.Id);
            if (dbEp != null)
            {
                dbEp.IsWatched = true;
                dbEp.WatchedDate = DateTime.Now;
                dbEp.ResumePositionSeconds = 0;
                episode.IsWatched = true;
                episode.WatchedDate = DateTime.Now;
            }

            // Update parent MediaItem watched count
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.WatchedEpisodes = await db.Episodes
                    .CountAsync(e => e.MediaItemId == _item.Id && e.IsWatched);

                // Update current episode pointer
                dbItem.CurrentEpisode = episode.EpisodeNumber;
                dbItem.CurrentSeason = episode.SeasonNumber;

                // Auto-set watch status
                if (dbItem.WatchedEpisodes == 0)
                    dbItem.WatchStatus = "Not Started";
                else if (dbItem.TotalEpisodes > 0 &&
                         dbItem.WatchedEpisodes >= dbItem.TotalEpisodes)
                    dbItem.WatchStatus = "Completed";
                else
                    dbItem.WatchStatus = "Watching";

                _item.WatchedEpisodes = dbItem.WatchedEpisodes;
                _item.WatchStatus = dbItem.WatchStatus;
                _item.CurrentEpisode = dbItem.CurrentEpisode;
                _item.CurrentSeason = dbItem.CurrentSeason;
            }

            await db.SaveChangesAsync();

            // Refresh UI
            TxtStatus.Text = _item.WatchStatus;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(_item.WatchStatus));
            TxtProgress.Text = $"{_item.WatchedEpisodes} / {_item.TotalEpisodes}";
            RefreshProgressBar();
            UpdatePlayButton();
            UpdateNextEpisodePanel();

            // Refresh episode grid
            await ShowSeasonAsync(_currentSeason);
        }

        private void UpdatePlayButton()
        {
            if (_isMovie)
            {
                BtnPlay.Content = _item.WatchStatus == "Completed"
                    ? "▶   Watch Again" : "▶   Watch Movie";
                return;
            }

            // Find next unwatched episode
            var next = _allEpisodes
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstOrDefault(e => !e.IsWatched);

            if (next == null)
            {
                BtnPlay.Content = "▶   Watch Again";
                return;
            }

            bool hasResume = next.ResumePositionSeconds > 0;
            BtnPlay.Content = hasResume
                ? $"▶   Continue S{next.SeasonNumber:D2}E{next.EpisodeNumber:D2}"
                : $"▶   Play S{next.SeasonNumber:D2}E{next.EpisodeNumber:D2}";
        }

        private void UpdateNextEpisodePanel()
        {
            if (_isMovie) { NextEpisodePanel.Visibility = Visibility.Collapsed; return; }

            var next = _allEpisodes
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstOrDefault(e => !e.IsWatched);

            if (next == null)
            {
                NextEpisodePanel.Visibility = Visibility.Collapsed;
                return;
            }

            NextEpisodePanel.Visibility = Visibility.Visible;
            TxtNextEpisode.Text = $"S{next.SeasonNumber:D2}E{next.EpisodeNumber:D2}" +
                                  (next.Title != null ? $" — {next.Title}" : "");
            TxtNextEpisodeDesc.Text = next.Description ?? "";
        }

        private void RefreshProgressBar()
        {
            double pct = 0;
            if (_isMovie)
            {
                pct = _item.WatchStatus == "Completed" ? 100 :
                      _item.ResumePositionSeconds > 0 ? 50 : 0;
            }
            else if (_item.TotalEpisodes > 0)
            {
                pct = (_item.WatchedEpisodes / (double)_item.TotalEpisodes) * 100.0;
            }

            TxtProgressPct.Text = $"{pct:F0}%";

            Dispatcher.InvokeAsync(() =>
            {
                // Main progress bar
                double maxW = ProgressBarFill.ActualWidth > 0
                    ? ((FrameworkElement)ProgressBarFill.Parent).ActualWidth
                    : 400;
                ProgressBarFill.Width = maxW * pct / 100.0;

                // Poster progress bar
                double posterW = PosterProgressBar.ActualWidth > 0
                    ? ((FrameworkElement)PosterProgressBar.Parent).ActualWidth
                    : 300;
                PosterProgressBar.Width = posterW * pct / 100.0;

            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovie)
            {
                await PlayMovieAsync();
                return;
            }

            // Play next unwatched episode
            var next = _allEpisodes
                .OrderBy(ep => ep.SeasonNumber).ThenBy(ep => ep.EpisodeNumber)
                .FirstOrDefault(ep => !ep.IsWatched)
                ?? _allEpisodes.OrderBy(ep => ep.SeasonNumber)
                               .ThenBy(ep => ep.EpisodeNumber)
                               .FirstOrDefault();

            if (next != null)
                await PlayEpisodeAsync(next);
        }

        private async Task PlayMovieAsync()
        {
            string? filePath = FindMovieFile();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ShowMessage("File not found. Set the media folder first.", "#e17055");
                return;
            }

            string? vlcPath = FindVlc();
            if (vlcPath == null)
            {
                ShowMessage("VLC not found. Please install VLC.", "#e17055");
                return;
            }

            string vlcArgs = _item.ResumePositionSeconds > 0
                ? $"--start-time={_item.ResumePositionSeconds} \"{filePath}\""
                : $"\"{filePath}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = vlcPath,
                Arguments = vlcArgs,
                UseShellExecute = false
            });

            // Mark movie as watched
            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.WatchStatus = "Completed";
                _item.WatchStatus = "Completed";
                await db.SaveChangesAsync();
            }

            TxtStatus.Text = "Completed";
            StatusDot.Fill = new SolidColorBrush(GetStatusColor("Completed"));
            RefreshProgressBar();
            UpdatePlayButton();
        }

        private async void BtnSetFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = $"Select folder for {_item.Title}"
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.FolderPath = dialog.SelectedPath;
                _item.FolderPath = dialog.SelectedPath;
                await db.SaveChangesAsync();
            }

            ShowMessage("Folder saved!", "#00b894");
        }

        private async void BtnEditStatus_Click(object sender, RoutedEventArgs e)
        {
            string[] statuses = { "Not Started", "Watching", "Completed", "On Hold", "Dropped" };
            int idx = Array.IndexOf(statuses, _item.WatchStatus);
            string newStatus = statuses[(idx + 1) % statuses.Length];

            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem == null) return;

            dbItem.WatchStatus = newStatus;
            _item.WatchStatus = newStatus;
            await db.SaveChangesAsync();

            TxtStatus.Text = newStatus;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(newStatus));
            ShowMessage($"Status: {newStatus}", "#00b894");
        }

        private string? FindEpisodeFile(Episode episode)
        {
            if (string.IsNullOrEmpty(_item.FolderPath) ||
                !Directory.Exists(_item.FolderPath)) return null;

            string[] videoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov" };
            string epPattern = $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            string epPatternAlt = $"{episode.SeasonNumber}x{episode.EpisodeNumber:D2}";

            var files = Directory.GetFiles(_item.FolderPath, "*",
                SearchOption.AllDirectories)
                .Where(f => videoExts.Contains(
                    Path.GetExtension(f).ToLower()));

            return files.FirstOrDefault(f =>
                f.Contains(epPattern, StringComparison.OrdinalIgnoreCase) ||
                f.Contains(epPatternAlt, StringComparison.OrdinalIgnoreCase));
        }

        private string? FindMovieFile()
        {
            if (string.IsNullOrEmpty(_item.FolderPath) ||
                !Directory.Exists(_item.FolderPath)) return null;

            string[] videoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov" };
            return Directory.GetFiles(_item.FolderPath, "*",
                SearchOption.AllDirectories)
                .FirstOrDefault(f => videoExts.Contains(
                    Path.GetExtension(f).ToLower()));
        }

        private static string? FindVlc()
        {
            string[] commonPaths = {
                @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe"
            };
            return commonPaths.FirstOrDefault(File.Exists);
        }

        private void ShowMessage(string msg, string color)
        {
            TxtMessage.Text = msg;
            TxtMessage.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            TxtMessage.Visibility = Visibility.Visible;
        }

        private static BitmapImage? LoadBitmap(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                return bmp;
            }
            catch { return null; }
        }

        private static Color GetStatusColor(string status) =>
            status?.ToLower() switch
            {
                "watching" => (Color)ColorConverter.ConvertFromString("#00b894"),
                "completed" => (Color)ColorConverter.ConvertFromString("#0984e3"),
                "not started" => (Color)ColorConverter.ConvertFromString("#636e72"),
                "on hold" => (Color)ColorConverter.ConvertFromString("#fdcb6e"),
                "dropped" => (Color)ColorConverter.ConvertFromString("#d63031"),
                _ => (Color)ColorConverter.ConvertFromString("#636e72")
            };
    }
}