using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Vault.Database;
using Vault.Models;
using Vault.Services;
using Vault.ViewModels;

namespace Vault.Views
{
    public partial class MediaPage : UserControl
    {
        private readonly AppSettings _settings;
        private readonly string _mediaType;
        private List<MediaItem> _allItems = new();
        private ObservableCollection<MediaTileViewModel> _tiles = new();
        private string _currentStatus = "All";
        private bool _isFetchingPosters = false;

        public event EventHandler<MediaItem>? ItemSelected;

        public MediaPage(AppSettings settings, string mediaType)
        {
            InitializeComponent();
            _settings = settings;
            _mediaType = mediaType;
            Loaded += MediaPage_Loaded;
        }

        private async void MediaPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            using var db = new VaultContext();
            _allItems = await db.MediaItems
                .Where(m => m.MediaType == _mediaType ||
                            // Group animated/anime movies under movies page
                            (_mediaType == "Movie" && (m.MediaType == "AnimeMovie" ||
                                                       m.MediaType == "AnimatedMovie")) ||
                            // Group animated series under shows page
                            (_mediaType == "Show" && m.MediaType == "AnimatedSeries") ||
                            // Anime movies show under anime page
                            (_mediaType == "Anime" && m.MediaType == "AnimeMovie"))
                .OrderBy(m => m.Title)
                .ToListAsync();

            LoadingOverlay.Visibility = Visibility.Collapsed;
            ApplyFilters();
        }

        private async Task FetchMissingPostersAsync(List<MediaTileViewModel> tiles)
        {
            if (!new TmdbService(_settings).IsConfigured) return;
            if (_isFetchingPosters) return;

            var missing = tiles.Where(t => !t.HasPoster).ToList();
            if (missing.Count == 0) return;

            _isFetchingPosters = true;

            try
            {
                var tmdb = new TmdbService(_settings);
                using var db = new VaultContext();
                bool anyUpdated = false;
                var dbLock = new object();
                var semaphore = new System.Threading.SemaphoreSlim(5);

                var tasks = missing.Select(async tile =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        bool isSeries = tile.MediaType != "Movie" &&
                                        tile.MediaType != "AnimeMovie" &&
                                        tile.MediaType != "AnimatedMovie";

                        // Search TMDB if no ID yet
                        int tmdbId = tile.Item.TmdbId;
                        if (tmdbId == 0)
                        {
                            int? found = await tmdb.SearchAsync(tile.Title, isSeries);
                            if (found == null) return;
                            tmdbId = found.Value;
                        }

                        // Fetch full details
                        var details = await tmdb.FetchDetailsAsync(tmdbId, isSeries);
                        if (details == null) return;

                        // Download poster
                        string? posterPath = null;
                        if (!string.IsNullOrEmpty(details.PosterPath))
                            posterPath = await tmdb.DownloadPosterAsync(
                                tile.Id, details.PosterPath);

                        // Download banner
                        string? bannerPath = null;
                        if (!string.IsNullOrEmpty(details.BackdropPath))
                            bannerPath = await tmdb.DownloadBannerAsync(
                                tile.Id, details.BackdropPath);

                        if (posterPath != null)
                            Dispatcher.Invoke(() => tile.PosterPath = posterPath);

                        lock (dbLock)
                        {
                            var dbItem = db.MediaItems.Find(tile.Id);
                            if (dbItem != null)
                            {
                                dbItem.TmdbId = tmdbId;
                                if (posterPath != null) dbItem.PosterPath = posterPath;
                                if (bannerPath != null) dbItem.BannerPath = bannerPath;
                                if (details.Description != null)
                                    dbItem.Description = details.Description;
                                if (details.Rating.HasValue)
                                    dbItem.TmdbRating = details.Rating;
                                if (details.Year.HasValue && dbItem.Year == null)
                                    dbItem.Year = details.Year;
                                if (details.Genre != null && dbItem.Genre == null)
                                    dbItem.Genre = details.Genre;
                                if (details.TotalSeasons.HasValue)
                                    dbItem.TotalSeasons = details.TotalSeasons;
                                if (details.TotalEpisodes.HasValue && dbItem.TotalEpisodes == 0)
                                    dbItem.TotalEpisodes = details.TotalEpisodes.Value;
                                anyUpdated = true;
                            }
                        }
                    }
                    catch { }
                    finally { semaphore.Release(); }
                });

                await Task.WhenAll(tasks);
                if (anyUpdated)
                    await db.SaveChangesAsync();
            }
            finally
            {
                _isFetchingPosters = false;
            }
        }

        private void FilterBtn_Click(object sender, RoutedEventArgs e)
        {
            _currentStatus = (sender as Button)?.Tag?.ToString() ?? "All";

            BtnAll.Style = (Style)FindResource("FilterButton");
            BtnWatching.Style = (Style)FindResource("FilterButton");
            BtnCompleted.Style = (Style)FindResource("FilterButton");
            BtnNotStarted.Style = (Style)FindResource("FilterButton");

            var active = (Style)FindResource("FilterButtonActive");
            switch (_currentStatus)
            {
                case "All": BtnAll.Style = active; break;
                case "Watching": BtnWatching.Style = active; break;
                case "Completed": BtnCompleted.Style = active; break;
                case "Not Started": BtnNotStarted.Style = active; break;
            }

            ApplyFilters();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (MediaItemsControl == null || _allItems == null) return;

            var filtered = _allItems.AsEnumerable();

            if (_currentStatus != "All")
                filtered = filtered.Where(m => m.WatchStatus == _currentStatus);

            int sortIdx = SortCombo?.SelectedIndex ?? 0;
            filtered = sortIdx switch
            {
                0 => filtered.OrderBy(m => m.Title),
                1 => filtered.OrderByDescending(m => m.Title),
                2 => filtered.OrderByDescending(m => m.Year),
                3 => filtered.OrderBy(m => m.Year),
                4 => filtered.OrderByDescending(m => m.TmdbRating),
                5 => filtered.OrderBy(m => m.WatchStatus),
                _ => filtered
            };

            var list = filtered.ToList();

            if (TxtItemCount != null)
                TxtItemCount.Text = $"{list.Count} titles";

            // Preserve already-loaded posters so re-sorting is instant
            var existingPosters = _tiles.ToDictionary(t => t.Id, t => t.PosterPath);

            _tiles = new ObservableCollection<MediaTileViewModel>(
                list.Select(m =>
                {
                    var tile = new MediaTileViewModel(m);
                    if (existingPosters.TryGetValue(m.Id, out string? cached)
                        && cached != null)
                        tile.PosterPath = cached;
                    return tile;
                }));

            MediaItemsControl.ItemsSource = _tiles;

            var stillMissing = _tiles.Where(t => !t.HasPoster).ToList();
            if (stillMissing.Count > 0)
                _ = FetchMissingPostersAsync(stillMissing);
        }

        private void MediaTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is MediaTileViewModel tile)
                ItemSelected?.Invoke(this, tile.Item);
        }

        public void Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { ApplyFilters(); return; }
            query = query.ToLower();
            var list = _allItems
                .Where(m => m.Title.ToLower().Contains(query) ||
                            (m.Genre != null && m.Genre.ToLower().Contains(query)))
                .ToList();

            if (TxtItemCount != null)
                TxtItemCount.Text = $"{list.Count} titles";

            var existingPosters = _tiles.ToDictionary(t => t.Id, t => t.PosterPath);
            _tiles = new ObservableCollection<MediaTileViewModel>(
                list.Select(m =>
                {
                    var tile = new MediaTileViewModel(m);
                    if (existingPosters.TryGetValue(m.Id, out string? cached)
                        && cached != null)
                        tile.PosterPath = cached;
                    return tile;
                }));

            MediaItemsControl.ItemsSource = _tiles;
        }
    }
}