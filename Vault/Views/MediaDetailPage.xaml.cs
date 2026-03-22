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
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(_item.WatchStatus));
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

            LoadPoster();
            RefreshProgressBar();

            if (!string.IsNullOrEmpty(_item.BannerPath) && File.Exists(_item.BannerPath))
            {
                var bmp = LoadBitmap(_item.BannerPath);
                if (bmp != null) ImgBanner.Source = bmp;
            }

            if (_item.TmdbId == 0 || string.IsNullOrEmpty(_item.Description))
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

                // If multi-series show, show all episodes flat
                await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
                UpdateNextEpisodePanel();
                UpdatePlayButton();
            }
        }

        private void LoadPoster()
        {
            if (!string.IsNullOrEmpty(_item.PosterPath) && File.Exists(_item.PosterPath))
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

            string? posterPath = _item.PosterPath;
            if (string.IsNullOrEmpty(posterPath) && details.PosterPath != null)
                posterPath = await _tmdb.DownloadPosterAsync(_item.Id, details.PosterPath);

            string? bannerPath = _item.BannerPath;
            if (string.IsNullOrEmpty(bannerPath) && details.BackdropPath != null)
                bannerPath = await _tmdb.DownloadBannerAsync(_item.Id, details.BackdropPath);

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

            if (_allEpisodes.Count == 0 && _item.TmdbId > 0 && _item.TotalSeasons > 0)
                await FetchAndSaveEpisodesAsync(db);
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

            int globalEpisodeNumber = 1; // Continuous episode counter across all series
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
                            EpisodeNumber = globalEpisodeNumber, // Continuous numbering
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
                    int? found = await _tmdb.SearchAsync(term, true);
                    if (found.HasValue && !ids.Contains(found.Value))
                        ids.Add(found.Value);
                }
            }
            else
            {
                int tmdbId = _item.TmdbId;
                if (tmdbId == 0)
                {
                    int? found = await _tmdb.SearchAsync(_item.Title, true);
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

            // Multi-series shows show all episodes flat — no season tabs
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
                // Show all episodes flat for merged multi-series shows
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

            EpisodesItemsControl.ItemsSource =
                new ObservableCollection<EpisodeViewModel>(viewModels);

            await Task.CompletedTask;
        }

        private async void EpisodeCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EpisodeViewModel vm)
                await PlayEpisodeAsync(vm.Episode);
        }

        private async void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovie) { await PlayMovieAsync(); return; }

            if (string.IsNullOrEmpty(_item.FolderPath))
            {
                ShowMessage("Set the media folder first.", "#e17055");
                return;
            }

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
                await PlayEpisodeAsync(current);
            else
                ShowMessage("No video files found in the selected folder.", "#e17055");
        }

        private async Task PlayEpisodeAsync(Episode episode)
        {
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

            string? vlcPath = FindVlc();
            if (vlcPath == null)
            {
                ShowMessage("VLC not found. Please install VLC.", "#e17055");
                return;
            }

            long startTime = episode.ResumePositionSeconds;
            string vlcArgs = startTime > 0
                ? $"--start-time={startTime} \"{filePath}\""
                : $"\"{filePath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = vlcPath,
                    Arguments = vlcArgs,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            process.Start();
            ShowMessage(
                $"Playing S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}" +
                (startTime > 0
                    ? $" — resuming from {TimeSpan.FromSeconds(startTime):mm\\:ss}"
                    : ""),
                "#00b894");

            string? capturedFilePath = filePath;
            _ = Task.Run(async () =>
            {
                await process.WaitForExitAsync();
                await Task.Delay(1000);

                await Dispatcher.InvokeAsync(async () =>
                {
                    long runtimeSeconds = episode.RuntimeMinutes > 0
                        ? episode.RuntimeMinutes * 60
                        : 1440;

                    long? vlcPosition = ReadVlcLastPosition(capturedFilePath);
                    if (vlcPosition == null || vlcPosition < 10) return;

                    bool finishedWatching = vlcPosition >= runtimeSeconds * 0.95;

                    if (finishedWatching)
                        await MarkEpisodeWatchedAsync(episode);
                    else
                        await SaveResumePositionAsync(episode, vlcPosition.Value);
                });
            });
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

            long startTime = _item.ResumePositionSeconds;
            string vlcArgs = startTime > 0
                ? $"--start-time={startTime} \"{filePath}\""
                : $"\"{filePath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = vlcPath,
                    Arguments = vlcArgs,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            process.Start();
            ShowMessage(
                startTime > 0
                    ? $"Resuming from {TimeSpan.FromSeconds(startTime):mm\\:ss}..."
                    : "Playing...",
                "#00b894");

            string? capturedFilePath = filePath;
            _ = Task.Run(async () =>
            {
                await process.WaitForExitAsync();
                await Task.Delay(1000);

                await Dispatcher.InvokeAsync(async () =>
                {
                    long? vlcPosition = ReadVlcLastPosition(capturedFilePath);
                    if (vlcPosition == null || vlcPosition < 10) return;

                    long runtimeSeconds = 7200;
                    bool finished = vlcPosition >= runtimeSeconds * 0.95;

                    using var db = new VaultContext();
                    var dbItem = await db.MediaItems.FindAsync(_item.Id);
                    if (dbItem != null)
                    {
                        if (finished)
                        {
                            dbItem.WatchStatus = "Completed";
                            dbItem.ResumePositionSeconds = 0;
                            _item.WatchStatus = "Completed";
                            _item.ResumePositionSeconds = 0;
                        }
                        else
                        {
                            dbItem.ResumePositionSeconds = vlcPosition.Value;
                            _item.ResumePositionSeconds = vlcPosition.Value;
                            ShowMessage(
                                $"Saved — resumes from {TimeSpan.FromSeconds(vlcPosition.Value):mm\\:ss}",
                                "#00b894");
                        }
                        await db.SaveChangesAsync();
                    }

                    TxtStatus.Text = _item.WatchStatus;
                    StatusDot.Fill = new SolidColorBrush(GetStatusColor(_item.WatchStatus));
                    RefreshProgressBar();
                    UpdatePlayButton();
                });
            });
        }

        private static long? ReadVlcLastPosition(string? filePath)
        {
            try
            {
                string iniPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "vlc", "vlc-qt-interface.ini");

                if (!File.Exists(iniPath) || filePath == null) return null;

                string content = File.ReadAllText(iniPath);

                int sectionIdx = content.IndexOf("[RecentsMRL]",
                    StringComparison.OrdinalIgnoreCase);
                if (sectionIdx < 0) return null;

                string section = content.Substring(sectionIdx);

                var listMatch = System.Text.RegularExpressions.Regex.Match(
                    section, @"list=(.+?)(\r?\n|$)");
                var timesMatch = System.Text.RegularExpressions.Regex.Match(
                    section, @"times=(.+?)(\r?\n|$)");

                if (!listMatch.Success || !timesMatch.Success) return null;

                string[] recentFiles = listMatch.Groups[1].Value
                    .Split(", ", StringSplitOptions.RemoveEmptyEntries);
                string[] times = timesMatch.Groups[1].Value
                    .Split(", ", StringSplitOptions.RemoveEmptyEntries);

                string filePathForward = filePath.Replace("\\", "/");

                for (int i = 0; i < recentFiles.Length; i++)
                {
                    string vlcDecoded = Uri.UnescapeDataString(
                        recentFiles[i].Replace("file:///", ""));

                    if (vlcDecoded.Equals(filePathForward,
                            StringComparison.OrdinalIgnoreCase) ||
                        vlcDecoded.Contains(Path.GetFileName(filePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (i < times.Length &&
                            long.TryParse(times[i].Trim(), out long ms))
                        {
                            return ms > 0 ? ms / 1000 : null;
                        }
                    }
                }

                return null;
            }
            catch { return null; }
        }

        private async Task SaveResumePositionAsync(Episode episode, long positionSeconds)
        {
            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(episode.Id);
            if (dbEp != null)
            {
                dbEp.ResumePositionSeconds = positionSeconds;
                episode.ResumePositionSeconds = positionSeconds;
                await db.SaveChangesAsync();
            }

            UpdatePlayButton();
            await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
            ShowMessage(
                $"Saved — resumes from {TimeSpan.FromSeconds(positionSeconds):mm\\:ss}",
                "#00b894");
        }

        private async Task MarkEpisodeWatchedAsync(Episode episode)
        {
            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(episode.Id);
            if (dbEp != null)
            {
                dbEp.IsWatched = true;
                dbEp.WatchedDate = DateTime.Now;
                dbEp.ResumePositionSeconds = 0;
                episode.IsWatched = true;
                episode.WatchedDate = DateTime.Now;
                episode.ResumePositionSeconds = 0;
            }

            var dbItem = await db.MediaItems.FindAsync(_item.Id);
            if (dbItem != null)
            {
                dbItem.WatchedEpisodes = await db.Episodes
                    .CountAsync(e => e.MediaItemId == _item.Id && e.IsWatched);
                dbItem.CurrentEpisode = episode.EpisodeNumber;
                dbItem.CurrentSeason = episode.SeasonNumber;

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

            TxtStatus.Text = _item.WatchStatus;
            StatusDot.Fill = new SolidColorBrush(GetStatusColor(_item.WatchStatus));
            TxtProgress.Text = $"{_item.WatchedEpisodes} / {_item.TotalEpisodes}";
            RefreshProgressBar();
            UpdatePlayButton();
            UpdateNextEpisodePanel();
            await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
        }

        private void UpdatePlayButton()
        {
            if (_isMovie)
            {
                BtnPlay.Content = _item.WatchStatus == "Completed"
                    ? "▶   Watch Again" : "▶   Watch Movie";
                return;
            }

            var withResume = _allEpisodes
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
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

            ShowMessage($"Folder: {Path.GetFileName(folderPath)}", "#636e72");
            UpdatePlayButton();
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
                if (!string.IsNullOrEmpty(_item.PosterPath) &&
                    File.Exists(_item.PosterPath))
                    File.Delete(_item.PosterPath);
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(_item.BannerPath) &&
                    File.Exists(_item.BannerPath))
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

            // Step 1 — Delete existing episodes and reset TmdbIds
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

            // Step 2 — Re-fetch with a fresh context
            using (var freshDb = new VaultContext())
            {
                await FetchAndSaveEpisodesAsync(freshDb);
            }

            // Step 3 — Rebuild UI
            BuildSeasonTabs();
            await ShowSeasonAsync(_currentSeason == -1 ? -1 : _currentSeason);
            UpdateNextEpisodePanel();
            UpdatePlayButton();

            ShowMessage($"Done — {_allEpisodes.Count} episodes loaded", "#00b894");
        }

        private string? FindEpisodeFile(Episode episode)
        {
            if (string.IsNullOrEmpty(_item.FolderPath)) return null;
            if (!Directory.Exists(_item.FolderPath)) return null;

            string[] videoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov" };
            string epPattern = $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            string epPatternAlt = $"{episode.SeasonNumber}x{episode.EpisodeNumber:D2}";

            var files = Directory.GetFiles(_item.FolderPath, "*",
                SearchOption.AllDirectories)
                .Where(f => videoExts.Contains(Path.GetExtension(f).ToLower()));

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