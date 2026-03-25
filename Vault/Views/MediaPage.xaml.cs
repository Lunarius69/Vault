using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
                .Where(m => m.MediaType == _mediaType)
                .OrderBy(m => m.Title)
                .ToListAsync();

            LoadingOverlay.Visibility = Visibility.Collapsed;
            ApplyFilters();
        }

        // ------------------------------------------------------------------ //
        //  Async image loading
        // ------------------------------------------------------------------ //
        private static async Task LoadPostersAsync(List<MediaTileViewModel> tiles)
        {
            const int BatchSize = 20;
            for (int i = 0; i < tiles.Count; i += BatchSize)
            {
                var batch = tiles.Skip(i).Take(BatchSize).ToList();
                var tasks = batch.Select(async tile =>
                {
                    if (tile.HasPoster || string.IsNullOrEmpty(tile.PosterPath)) return;
                    if (!File.Exists(tile.PosterPath)) return;

                    try
                    {
                        var bmp = await Task.Run(() =>
                        {
                            var b = new BitmapImage();
                            b.BeginInit();
                            b.UriSource = new Uri(tile.PosterPath!);
                            b.CacheOption = BitmapCacheOption.OnLoad;
                            b.DecodePixelWidth = 150;
                            b.EndInit();
                            b.Freeze();
                            return b;
                        });

                        tile.LoadedBitmap = bmp;
                    }
                    catch { }
                });

                await Task.WhenAll(tasks);
                await Task.Delay(10);
            }
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

                        int tmdbId = tile.Item.TmdbId;
                        if (tmdbId == 0)
                        {
                            int? found = await tmdb.SearchAsync(
                                tile.Title, isSeries, tile.Item.Year);
                            if (found == null) return;
                            tmdbId = found.Value;
                        }

                        var details = await tmdb.FetchDetailsAsync(tmdbId, isSeries);
                        if (details == null) return;

                        string? posterPath = null;
                        if (isSeries)
                        {
                            int seasonNum = TmdbService.ExtractSeasonNumber(tile.Title);
                            posterPath = await tmdb.DownloadSeasonPosterAsync(
                                tile.Id, tmdbId, seasonNum);
                        }

                        if (posterPath == null && !string.IsNullOrEmpty(details.PosterPath))
                            posterPath = await tmdb.DownloadPosterAsync(
                                tile.Id, details.PosterPath);

                        string? bannerPath = null;
                        if (!string.IsNullOrEmpty(details.BackdropPath))
                            bannerPath = await tmdb.DownloadBannerAsync(
                                tile.Id, details.BackdropPath);

                        if (posterPath != null)
                        {
                            try
                            {
                                var bmp = await Task.Run(() =>
                                {
                                    var b = new BitmapImage();
                                    b.BeginInit();
                                    b.UriSource = new Uri(posterPath);
                                    b.CacheOption = BitmapCacheOption.OnLoad;
                                    b.DecodePixelWidth = 150;
                                    b.EndInit();
                                    b.Freeze();
                                    return b;
                                });
                                Dispatcher.Invoke(() =>
                                {
                                    tile.PosterPath = posterPath;
                                    tile.LoadedBitmap = bmp;
                                });
                            }
                            catch { }
                        }

                        lock (dbLock)
                        {
                            var dbItem = db.MediaItems.Find(tile.Id);
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
                                anyUpdated = true;
                            }
                        }
                    }
                    catch { }
                    finally { semaphore.Release(); }
                });

                await Task.WhenAll(tasks);
                if (anyUpdated) await db.SaveChangesAsync();
            }
            finally
            {
                _isFetchingPosters = false;
            }
        }

        // ------------------------------------------------------------------ //
        //  Filters / sorting
        // ------------------------------------------------------------------ //
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
            => ApplyFilters();

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

            // FIX — preserve already-loaded bitmaps across filter/sort changes,
            // then reuse the existing ObservableCollection instead of replacing it.
            // Previously this created a brand new collection on every filter click,
            // destroying and rebuilding all tile bindings unnecessarily.
            var existingBitmaps = _tiles.ToDictionary(t => t.Id, t => t.LoadedBitmap);

            var newTiles = list.Select(m =>
            {
                var tile = new MediaTileViewModel(m);
                if (existingBitmaps.TryGetValue(m.Id, out var bmp) && bmp != null)
                    tile.LoadedBitmap = bmp;
                return tile;
            }).ToList();

            if (MediaItemsControl.ItemsSource != _tiles)
                MediaItemsControl.ItemsSource = _tiles;

            _tiles.Clear();
            foreach (var tile in newTiles)
                _tiles.Add(tile);

            var tilesNeedingLoad = _tiles
                .Where(t => t.LoadedBitmap == null && t.HasPoster)
                .ToList();

            if (tilesNeedingLoad.Count > 0)
                _ = LoadPostersAsync(tilesNeedingLoad);

            var stillMissing = _tiles.Where(t => !t.HasPoster).ToList();
            if (stillMissing.Count > 0)
                _ = FetchMissingPostersAsync(stillMissing);
        }

        // ------------------------------------------------------------------ //
        //  Selection
        // ------------------------------------------------------------------ //
        private void MediaTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is MediaTileViewModel tile)
                ItemSelected?.Invoke(this, tile.Item);
        }

        // ------------------------------------------------------------------ //
        //  Search
        // ------------------------------------------------------------------ //
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

            // FIX — same collection reuse pattern as ApplyFilters
            var existingBitmaps = _tiles.ToDictionary(t => t.Id, t => t.LoadedBitmap);

            var newTiles = list.Select(m =>
            {
                var tile = new MediaTileViewModel(m);
                if (existingBitmaps.TryGetValue(m.Id, out var bmp) && bmp != null)
                    tile.LoadedBitmap = bmp;
                return tile;
            }).ToList();

            if (MediaItemsControl.ItemsSource != _tiles)
                MediaItemsControl.ItemsSource = _tiles;

            _tiles.Clear();
            foreach (var tile in newTiles)
                _tiles.Add(tile);

            var tilesNeedingLoad = _tiles
                .Where(t => t.LoadedBitmap == null && t.HasPoster)
                .ToList();
            if (tilesNeedingLoad.Count > 0)
                _ = LoadPostersAsync(tilesNeedingLoad);
        }
    }
}