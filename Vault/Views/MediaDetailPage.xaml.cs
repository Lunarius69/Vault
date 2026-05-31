using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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

        private List<string>? _cachedFolderFiles;
        private string? _cachedFolderPath;

        private static readonly Dictionary<string, SolidColorBrush> _statusBrushCache = new();

        public event EventHandler? BackRequested;

        private static readonly Dictionary<string, string[]> MultiTmdbSearchTerms =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "Naruto", new[] { "Naruto", "Naruto Shippuden" } },
            { "Bleach", new[] { "Bleach", "Bleach: Thousand-Year Blood War" } },
            { "Dragon Ball", new[] { "Dragon Ball", "Dragon Ball Z",
                                      "Dragon Ball GT", "Dragon Ball Super" } },
            { "Fairy Tail", new[] { "Fairy Tail", "Fairy Tail (2014)",
                                     "Fairy Tail: Final Series",
                                     "Fairy Tail: 100 Years Quest" } },
            { "Beyblade", new[] { "Beyblade", "Beyblade V-Force",
                                   "Beyblade G-Revolution", "Beyblade Metal Fusion",
                                   "Beyblade Metal Masters", "Beyblade Metal Fury",
                                   "Beyblade Burst", "Beyblade X" } },
            { "Initial D", new[] { "Initial D First Stage", "Initial D Second Stage",
                                    "Initial D Third Stage", "Initial D Fourth Stage",
                                    "Initial D Fifth Stage", "Initial D Final Stage" } },
            { "Mobile Suit Gundam", new[] { "Mobile Suit Gundam",
                                             "Mobile Suit Zeta Gundam",
                                             "Mobile Suit Gundam Wing",
                                             "Mobile Suit Gundam SEED",
                                             "Mobile Suit Gundam SEED Destiny" } },
        };

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
            StatusDot.Fill = GetStatusBrush(_item.WatchStatus);
            TxtYear.Text = _item.Year?.ToString() ?? "—";
            TxtGenre.Text = string.IsNullOrEmpty(_item.Genre) ? "—" : _item.Genre;
            TxtRating.Text = _item.TmdbRating.HasValue
                ? $"★ {_item.TmdbRating:F1}" : "—";
            TxtDescription.Text = string.IsNullOrEmpty(_item.Description)
                ? "No description available." : _item.Description;

            EpisodesSection.Visibility = _isMovie
                ? Visibility.Collapsed : Visibility.Visible;

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

            if (!string.IsNullOrEmpty(_item.FolderPath))
                ShowMessage($"Folder: {Path.GetFileName(_item.FolderPath)}", "#636e72");

            _ = Task.Run(async () =>
            {
                var posterBmp = !string.IsNullOrEmpty(_item.PosterPath) && File.Exists(_item.PosterPath)
                    ? LoadBitmapFromPath(_item.PosterPath) : null;
                var bannerBmp = !string.IsNullOrEmpty(_item.BannerPath) && File.Exists(_item.BannerPath)
                    ? LoadBitmapFromPath(_item.BannerPath) : null;

                await Dispatcher.InvokeAsync(() =>
                {
                    if (posterBmp != null)
                    {
                        ImgPoster.Source = posterBmp;
                        ImgPoster.Visibility = Visibility.Visible;
                        PlaceholderBg.Visibility = Visibility.Collapsed;
                        TxtPlaceholder.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ImgPoster.Visibility = Visibility.Collapsed;
                        PlaceholderBg.Visibility = Visibility.Visible;
                        TxtPlaceholder.Visibility = Visibility.Visible;
                        TxtPlaceholder.Text = _item.Title;
                    }

                    if (bannerBmp != null)
                        ImgBanner.Source = bannerBmp;
                });
            });

            RefreshProgressBar();

            bool needsTmdb = _item.TmdbId == 0 || string.IsNullOrEmpty(_item.Description);
            if (needsTmdb)
                await FetchTmdbDataAsync();

            if (_isMovie)
            {
                UpdatePlayButton();
            }
            else
            {
                await LoadEpisodesAsync();

                var nextEp = _allEpisodes
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                    .FirstOrDefault(e => !e.IsWatched && e.ResumePositionSeconds > 0)
                    ?? _allEpisodes
                    .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                    .FirstOrDefault(e => !e.IsWatched);

                _currentSeason = nextEp?.SeasonNumber
                    ?? _allEpisodes.Select(e => e.SeasonNumber).FirstOrDefault(1);

                BuildSeasonTabs();
                await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
                UpdateNextEpisodePanel();
                UpdatePlayButton();
            }
        }

        private static BitmapImage? LoadBitmapFromPath(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void LoadPoster()
        {
            if (!string.IsNullOrEmpty(_item.PosterPath) && File.Exists(_item.PosterPath))
            {
                var bmp = LoadBitmapFromPath(_item.PosterPath);
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
                // FIX: pass _item.MediaType so anime/animated items use genre 16
                // filtering and never match a live-action result with the same title.
                // e.g. "Bubble" (AnimeMovie) will now find the anime, not the drama.
                int? found = await _tmdb.SearchAsync(
                    _item.Title, !_isMovie, _item.Year, _item.MediaType);
                if (found == null) return;
                tmdbId = found.Value;
            }

            var details = await _tmdb.FetchDetailsAsync(tmdbId, !_isMovie);
            if (details == null) return;

            string? posterPath = _item.PosterPath;
            if (string.IsNullOrEmpty(posterPath) && details.PosterPath != null)
                posterPath = await _tmdb.DownloadPosterAsync(_item.Id, details.PosterPath, tmdbId);

            string? bannerPath = _item.BannerPath;
            if (string.IsNullOrEmpty(bannerPath) && details.BackdropPath != null)
                bannerPath = await _tmdb.DownloadBannerAsync(_item.Id, details.BackdropPath, tmdbId);

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

            _item.TmdbId = tmdbId;
            if (posterPath != null) { _item.PosterPath = posterPath; LoadPoster(); }
            if (bannerPath != null)
            {
                _item.BannerPath = bannerPath;
                _ = Task.Run(() =>
                {
                    var bmp = LoadBitmapFromPath(bannerPath);
                    if (bmp != null)
                        Dispatcher.Invoke(() => ImgBanner.Source = bmp);
                });
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

            if (_allEpisodes.Count == 0 && _item.TmdbId > 0 && _item.TotalSeasons > 0)
                await FetchAndSaveEpisodesAsync(db);

            // Sync WatchedEpisodes counter with actual episode flags
            await SyncWatchedCountAsync();
        }

        private async Task SyncWatchedCountAsync()
        {
            int actual = _allEpisodes.Count(e => e.IsWatched);
            if (actual == _item.WatchedEpisodes) return;

            _item.WatchedEpisodes = actual;

            string newStatus = _item.WatchedEpisodes >= _item.TotalEpisodes && _item.TotalEpisodes > 0
                ? "Completed"
                : _item.WatchedEpisodes > 0 ? "Watching" : _item.WatchStatus;

            bool statusChanged = newStatus != _item.WatchStatus;
            if (statusChanged) _item.WatchStatus = newStatus;

            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.WatchedEpisodes = actual;
                if (statusChanged) dbItem.WatchStatus = newStatus;
                await db.SaveChangesAsync();
            }

            TxtProgress.Text = _item.TotalEpisodes > 0
                ? $"{actual} / {_item.TotalEpisodes}" : "—";
            if (statusChanged)
            {
                TxtStatus.Text = newStatus;
                StatusDot.Fill = GetStatusBrush(newStatus);
            }
            RefreshProgressBar();
        }

        private async Task FetchAndSaveEpisodesAsync(VaultContext db)
        {
            if (!_tmdb.IsConfigured) return;

            TxtEpisodesLoading.Visibility = Visibility.Visible;

            var tmdbIds = await GetAllTmdbIdsAsync();

            if (tmdbIds.Count == 0)
            {
                TxtEpisodesLoading.Visibility = Visibility.Collapsed;
                return;
            }

            int globalEpisodeNumber = 1;
            int globalSeasonOffset = 0;

            foreach (int tmdbId in tmdbIds)
            {
                var details = await _tmdb.FetchDetailsAsync(tmdbId, true);
                if (details == null) continue;

                int totalSeasons = details.TotalSeasons ?? 1;

                for (int s = 1; s <= totalSeasons; s++)
                {
                    var tmdbEps = await _tmdb.FetchSeasonEpisodesAsync(tmdbId, s);
                    foreach (var ep in tmdbEps)
                    {
                        var episode = new Episode
                        {
                            MediaItemId = _item.Id,
                            SeasonNumber = s + globalSeasonOffset,
                            EpisodeNumber = globalEpisodeNumber,
                            Title = ep.Title,
                            Description = ep.Description,
                            RuntimeMinutes = ep.RuntimeMinutes
                        };

                        if (!string.IsNullOrEmpty(ep.ThumbnailPath))
                        {
                            int thumbId = _item.Id * 100000 +
                                          episode.SeasonNumber * 1000 +
                                          globalEpisodeNumber;
                            string? thumbPath = await _tmdb.DownloadThumbnailAsync(
                                thumbId, ep.ThumbnailPath);
                            episode.ThumbnailPath = thumbPath;
                        }

                        db.Episodes.Add(episode);
                        _allEpisodes.Add(episode);
                        globalEpisodeNumber++;
                    }
                }

                globalSeasonOffset += totalSeasons;
            }

            await db.SaveChangesAsync();
            TxtEpisodesLoading.Visibility = Visibility.Collapsed;
        }

        private async Task<List<int>> GetAllTmdbIdsAsync()
        {
            var ids = new List<int>();

            if (!string.IsNullOrEmpty(_item.TmdbIds))
            {
                foreach (var idStr in _item.TmdbIds.Split(','))
                    if (int.TryParse(idStr.Trim(), out int id))
                        ids.Add(id);
                return ids;
            }

            if (MultiTmdbSearchTerms.TryGetValue(_item.Title, out string[]? searchTerms))
            {
                foreach (string term in searchTerms)
                {
                    // FIX: pass mediaType for multi-series searches too
                    int? found = await _tmdb.SearchAsync(term, true, null, _item.MediaType);
                    if (found.HasValue && !ids.Contains(found.Value))
                        ids.Add(found.Value);
                }
            }
            else
            {
                int tmdbId = _item.TmdbId;
                if (tmdbId == 0)
                {
                    // FIX: pass mediaType here as well
                    int? found = await _tmdb.SearchAsync(
                        _item.Title, true, null, _item.MediaType);
                    if (found.HasValue) tmdbId = found.Value;
                }
                if (tmdbId > 0) ids.Add(tmdbId);
            }

            if (ids.Count > 0)
            {
                using var db = new VaultContext();
                var dbItem = await db.MediaItems.FindAsync(_item.Id);
                if (dbItem != null)
                {
                    dbItem.TmdbIds = string.Join(",", ids);
                    dbItem.TmdbId = ids[0];
                    _item.TmdbIds = dbItem.TmdbIds;
                    _item.TmdbId = ids[0];
                    await db.SaveChangesAsync();
                }
            }

            return ids;
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

            bool isMultiSeries = MultiTmdbSearchTerms.ContainsKey(_item.Title);
            if (isMultiSeries)
            {
                _currentSeason = -1;
                return;
            }

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
            List<Episode> episodes;

            if (season == -1)
            {
                episodes = _allEpisodes
                    .OrderBy(e => e.SeasonNumber)
                    .ThenBy(e => e.EpisodeNumber)
                    .ToList();
            }
            else
            {
                episodes = _allEpisodes
                    .Where(e => e.SeasonNumber == season)
                    .OrderBy(e => e.EpisodeNumber)
                    .ToList();
            }

            var viewModels = episodes
                .Select(e => new EpisodeViewModel(e))
                .ToList();

            if (EpisodesItemsControl.ItemsSource is ObservableCollection<EpisodeViewModel> existing)
            {
                existing.Clear();
                foreach (var vm in viewModels)
                    existing.Add(vm);
            }
            else
            {
                EpisodesItemsControl.ItemsSource =
                    new ObservableCollection<EpisodeViewModel>(viewModels);
            }

            await Task.CompletedTask;
        }

        private async void EpisodeCard_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button btn || btn.DataContext is not EpisodeViewModel vm) return;

            bool nowWatched = !vm.IsWatched;
            vm.IsWatched = nowWatched;

            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(vm.Id);
            if (dbEp != null)
            {
                dbEp.IsWatched = nowWatched;
                dbEp.WatchedDate = nowWatched ? DateTime.Now : null;
                if (!nowWatched) dbEp.ResumePositionSeconds = 0;
                await db.SaveChangesAsync();
            }

            // Update episode in-memory list then sync counter
            var ep = _allEpisodes.FirstOrDefault(ep => ep.Id == vm.Id);
            if (ep != null)
            {
                ep.IsWatched = nowWatched;
                if (!nowWatched) ep.ResumePositionSeconds = 0;
            }

            await SyncWatchedCountAsync();
            UpdateNextEpisodePanel();
            UpdatePlayButton();
        }

        private void EpisodeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EpisodeViewModel vm)
            {
                if (string.IsNullOrEmpty(_item.FolderPath))
                {
                    ShowMessage("Set the media folder first.", "#e17055");
                    return;
                }

                string? filePath = FindEpisodeFile(vm.Episode);
                if (filePath == null)
                {
                    ShowMessage("File not found for this episode.", "#e17055");
                    return;
                }

                vm.Episode.FilePath = filePath;
                OpenPlayer(vm.Episode);
            }
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovie)
            {
                string? movieFile = FindMovieFile();
                if (string.IsNullOrEmpty(movieFile))
                {
                    ShowMessage("File not found. Set the media folder first.", "#e17055");
                    return;
                }

                var movieEpisode = new Episode
                {
                    Id = -1,
                    MediaItemId = _item.Id,
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    Title = _item.Title,
                    FilePath = movieFile,
                    ResumePositionSeconds = _item.ResumePositionSeconds,
                    RuntimeMinutes = 120
                };
                OpenPlayer(movieEpisode, isMovie: true);
                return;
            }

            if (string.IsNullOrEmpty(_item.FolderPath))
            {
                ShowMessage("Set the media folder first.", "#e17055");
                return;
            }

            EnsureFolderFilesCache();

            var current = _allEpisodes
                .OrderBy(ep => ep.SeasonNumber).ThenBy(ep => ep.EpisodeNumber)
                .FirstOrDefault(ep => !ep.IsWatched && ep.ResumePositionSeconds > 0);

            current ??= _allEpisodes
                .OrderBy(ep => ep.SeasonNumber).ThenBy(ep => ep.EpisodeNumber)
                .FirstOrDefault(ep => !ep.IsWatched && FindEpisodeFile(ep) != null);

            current ??= _allEpisodes
                .OrderBy(ep => ep.SeasonNumber).ThenBy(ep => ep.EpisodeNumber)
                .FirstOrDefault(ep => FindEpisodeFile(ep) != null);

            if (current != null)
            {
                string? filePath = FindEpisodeFile(current);
                if (filePath != null) current.FilePath = filePath;
                OpenPlayer(current);
            }
            else
            {
                ShowMessage("No video files found in the selected folder.", "#e17055");
            }
        }

        private void OpenPlayer(Episode startEpisode, bool isMovie = false)
        {
            var playlist = isMovie
                ? new List<Episode> { startEpisode }
                : _allEpisodes;

            var player = new PlayerWindow(_item, startEpisode, playlist);
            player.Closed += (s, e) =>
            {
                _cachedFolderFiles = null;
                _cachedFolderPath = null;

                _ = Dispatcher.InvokeAsync(async () =>
                {
                    // Re-read MediaItem from DB to pick up RuntimeMinutes/ResumePositionSeconds
                    using (var db = new VaultContext())
                    {
                        var fresh = await db.MediaItems.FindAsync(_item.Id);
                        if (fresh != null)
                        {
                            _item.WatchStatus = fresh.WatchStatus;
                            _item.ResumePositionSeconds = fresh.ResumePositionSeconds;
                            _item.RuntimeMinutes = fresh.RuntimeMinutes;
                            _item.WatchedEpisodes = fresh.WatchedEpisodes;
                            _item.WatchTimeMinutes = fresh.WatchTimeMinutes;
                        }
                    }

                    await LoadEpisodesAsync();
                    BuildSeasonTabs();
                    await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
                    UpdateNextEpisodePanel();
                    UpdatePlayButton();
                    TxtProgress.Text = _isMovie
                        ? (_item.WatchStatus == "Completed" ? "Watched" : "Not Watched")
                        : $"{_item.WatchedEpisodes} / {_item.TotalEpisodes}";
                    RefreshProgressBar();
                });
            };
            player.Show();
        }

        private void UpdatePlayButton()
        {
            if (_isMovie)
            {
                if (_item.WatchStatus == "Completed")
                    BtnPlay.Content = "▶   Watch Again";
                else if (_item.ResumePositionSeconds > 0)
                {
                    var ts = TimeSpan.FromSeconds(_item.ResumePositionSeconds);
                    string t = ts.TotalHours >= 1
                        ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                        : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
                    BtnPlay.Content = $"▶   Continue ({t})";
                }
                else
                    BtnPlay.Content = "▶   Watch Movie";
                return;
            }

            var withResume = _allEpisodes
                .OrderByDescending(e => e.ResumePositionSeconds > 0)
                .ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstOrDefault(e => !e.IsWatched && e.ResumePositionSeconds > 0);

            if (withResume != null)
            {
                BtnPlay.Content =
                    $"▶   Continue S{withResume.SeasonNumber:D2}E{withResume.EpisodeNumber:D2}" +
                    $" ({TimeSpan.FromSeconds(withResume.ResumePositionSeconds):mm\\:ss})";
                return;
            }

            var next = _allEpisodes
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
                .FirstOrDefault(e => !e.IsWatched);

            if (next == null)
            {
                BtnPlay.Content = "▶   Watch Again";
                return;
            }

            bool anyWatched = _allEpisodes.Any(e => e.IsWatched);
            BtnPlay.Content = anyWatched
                ? $"▶   Play S{next.SeasonNumber:D2}E{next.EpisodeNumber:D2}"
                : "▶   Start Watching";
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
            TxtNextEpisode.Text =
                $"S{next.SeasonNumber:D2}E{next.EpisodeNumber:D2}" +
                (next.Title != null ? $" — {next.Title}" : "");
            TxtNextEpisodeDesc.Text = next.Description ?? "";
        }

        private void RefreshProgressBar()
        {
            double pct = 0;
            if (_isMovie)
            {
                if (_item.WatchStatus == "Completed")
                    pct = 100;
                else if (_item.RuntimeMinutes > 0 && _item.ResumePositionSeconds > 0)
                    pct = Math.Min(100, (_item.ResumePositionSeconds / (_item.RuntimeMinutes * 60.0)) * 100.0);
            }
            else if (_item.TotalEpisodes > 0)
            {
                pct = (_item.WatchedEpisodes / (double)_item.TotalEpisodes) * 100.0;
            }

            TxtProgressPct.Text = $"{pct:F0}%";

            Dispatcher.InvokeAsync(() =>
            {
                double maxW = ((FrameworkElement)ProgressBarFill.Parent).ActualWidth;
                if (maxW > 0) ProgressBarFill.Width = maxW * pct / 100.0;

                double posterW = ((FrameworkElement)PosterProgressBar.Parent).ActualWidth;
                if (posterW > 0) PosterProgressBar.Width = posterW * pct / 100.0;

            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private async void BtnSetFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = $"Select any file inside the folder for {_item.Title}",
                Filter = "Video files|*.mkv;*.mp4;*.avi;*.m4v;*.mov|Any file|*.*",
                CheckFileExists = false,
                FileName = "Select this folder"
            };

            if (dialog.ShowDialog() != true) return;

            string folderPath = Path.GetDirectoryName(dialog.FileName)!;

            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.FolderPath = folderPath;
                _item.FolderPath = folderPath;
                await db.SaveChangesAsync();
            }

            _cachedFolderFiles = null;
            _cachedFolderPath = null;

            ShowMessage($"Folder: {Path.GetFileName(folderPath)}", "#636e72");
            UpdatePlayButton();
        }

        private void BtnEditStatus_Click(object sender, RoutedEventArgs e)
        {
            var popup = new Popup
            {
                PlacementTarget = sender as Button,
                Placement = PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var button = sender as Button;
            var border = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16213e")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = button?.ActualWidth ?? 200
            };

            var stack = new StackPanel();
            string[] statuses = { "Not Started", "Watching", "Completed", "On Hold", "Dropped" };

            foreach (string status in statuses)
            {
                string statusColor = status switch
                {
                    "Watching"     => "#00b894",
                    "Completed"    => "#0984e3",
                    "Not Started"  => "#636e72",
                    "On Hold"      => "#fdcb6e",
                    "Dropped"      => "#d63031",
                    _              => "#636e72"
                };
                bool isCurrent = _item.WatchStatus == status;

                var btn = new Button
                {
                    Background = isCurrent
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561"))
                        : Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(12, 8, 16, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Left
                };

                var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
                btnPanel.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(statusColor)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });
                btnPanel.Children.Add(new TextBlock
                {
                    Text = status,
                    Foreground = isCurrent
                        ? Brushes.White
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = btnPanel;

                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetBinding(Border.BackgroundProperty,
                    new Binding("Background")
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                    });
                factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                factory.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                template.VisualTree = factory;
                btn.Template = template;

                string capturedStatus = status;
                btn.Click += async (s, args) =>
                {
                    popup.IsOpen = false;
                    await SetStatusAsync(capturedStatus);
                };
                btn.MouseEnter += (s, args) =>
                    btn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561"));
                btn.MouseLeave += (s, args) =>
                    btn.Background = isCurrent
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2d3561"))
                        : Brushes.Transparent;

                stack.Children.Add(btn);
            }

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }

        private async Task SetStatusAsync(string newStatus)
        {
            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem == null) return;

            dbItem.WatchStatus = newStatus;
            _item.WatchStatus = newStatus;
            await db.SaveChangesAsync();

            TxtStatus.Text = newStatus;
            StatusDot.Fill = GetStatusBrush(newStatus);
            ShowMessage($"Status: {newStatus}", "#00b894");
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"Delete '{_item.Title}' from your library?\nThis cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                db.MediaItems.Remove(dbItem);
                await db.SaveChangesAsync();
            }

            ImgPoster.Source = null;
            ImgBanner.Source = null;
            await Task.Delay(200);

            try
            {
                if (!string.IsNullOrEmpty(_item.PosterPath) && File.Exists(_item.PosterPath))
                    File.Delete(_item.PosterPath);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(_item.BannerPath) && File.Exists(_item.BannerPath))
                    File.Delete(_item.BannerPath);
            }
            catch { }

            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private async void BtnRefreshEpisodes_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Delete all episodes and re-fetch from TMDB?\nWatch progress will be lost.",
                "Refresh Episodes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            using (var db = new VaultContext())
            {
                var episodes = await db.Episodes
                    .Where(ep => ep.MediaItemId == _item.Id)
                    .ToListAsync();
                db.Episodes.RemoveRange(episodes);

                var dbItem = await db.MediaItems.FindAsync(_item.Id);
                if (dbItem != null)
                {
                    dbItem.TmdbIds = null;
                    _item.TmdbIds = null;
                }

                await db.SaveChangesAsync();
            }

            _allEpisodes.Clear();
            ShowMessage("Episodes cleared — re-fetching from TMDB...", "#636e72");

            using (var freshDb = new VaultContext())
            {
                await FetchAndSaveEpisodesAsync(freshDb);
            }

            BuildSeasonTabs();
            await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
            UpdateNextEpisodePanel();
            UpdatePlayButton();

            ShowMessage($"Done — {_allEpisodes.Count} episodes loaded", "#00b894");
        }

        private void EnsureFolderFilesCache()
        {
            if (_cachedFolderFiles != null && _cachedFolderPath == _item.FolderPath)
                return;

            _cachedFolderPath = _item.FolderPath;
            _cachedFolderFiles = new List<string>();

            if (string.IsNullOrEmpty(_item.FolderPath)) return;

            string[] videoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov" };
            var searchPaths = new List<string> { _item.FolderPath };
            string? parent = Path.GetDirectoryName(_item.FolderPath);
            if (!string.IsNullOrEmpty(parent) && parent != _item.FolderPath)
                searchPaths.Add(parent);

            foreach (string searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                try
                {
                    _cachedFolderFiles.AddRange(
                        Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories)
                        .Where(f => videoExts.Contains(Path.GetExtension(f).ToLower())));
                }
                catch { }
            }
        }

        private string? FindEpisodeFile(Episode episode)
        {
            if (string.IsNullOrEmpty(_item.FolderPath)) return null;

            EnsureFolderFilesCache();
            var allFiles = _cachedFolderFiles!;

            if (allFiles.Count == 0) return null;

            var episodesInSeason = _allEpisodes
                .Where(e => e.SeasonNumber == episode.SeasonNumber)
                .OrderBy(e => e.EpisodeNumber)
                .ToList();
            int localEpNumber = episodesInSeason.FindIndex(e => e.Id == episode.Id) + 1;
            if (localEpNumber == 0) localEpNumber = 1;

            string pattern1 = $"S{episode.SeasonNumber:D2}E{localEpNumber:D2}";
            var match = allFiles.FirstOrDefault(f =>
                f.Contains(pattern1, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            string pattern2 = $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            match = allFiles.FirstOrDefault(f =>
                f.Contains(pattern2, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            string pattern3 = $"{episode.SeasonNumber}x{localEpNumber:D2}";
            match = allFiles.FirstOrDefault(f =>
                f.Contains(pattern3, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            string pattern4 = $"{episode.SeasonNumber}x{episode.EpisodeNumber:D2}";
            match = allFiles.FirstOrDefault(f =>
                f.Contains(pattern4, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;

            if (!string.IsNullOrEmpty(episode.Title) && episode.Title.Length > 5)
            {
                string titleKey = episode.Title.Split(' ').First();
                match = allFiles.FirstOrDefault(f =>
                    f.Contains(titleKey, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            return null;
        }

        private string? FindMovieFile()
        {
            if (string.IsNullOrEmpty(_item.FolderPath) ||
                !Directory.Exists(_item.FolderPath)) return null;

            string[] videoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov" };
            return Directory.GetFiles(_item.FolderPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => videoExts.Contains(Path.GetExtension(f).ToLower()));
        }

        private void ShowMessage(string msg, string color)
        {
            TxtMessage.Text = msg;
            TxtMessage.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            TxtMessage.Visibility = Visibility.Visible;
        }

        private static Brush GetStatusBrush(string status)
        {
            string key = status ?? "";
            if (!_statusBrushCache.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush(GetStatusColor(key));
                brush.Freeze();
                _statusBrushCache[key] = brush;
            }
            return brush;
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