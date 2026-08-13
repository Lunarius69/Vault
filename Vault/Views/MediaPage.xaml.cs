using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        private ListCollectionView? _view;
        private string _currentStatus = "All";
        private bool _isFetchingPosters = false;
        private static readonly Random _rng = new();

        public event EventHandler<MediaItem>? ItemSelected;

        public MediaPage(AppSettings settings, string mediaType)
        {
            InitializeComponent();
            _settings = settings;
            _mediaType = mediaType;
            Loaded += MediaPage_Loaded;
        }

        public void Refresh() => ApplyFilters();

        private async void MediaPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadingOverlay.Visibility = Visibility.Visible;

            // Clear stale bitmaps for this type so that tiles always get
            // their own correct poster from disk rather than a leftover
            // bitmap that was cached under a different tile's ID last session.
            // This is per-type so navigating to Movies doesn't wipe Anime cache.
            MediaTileViewModel.ClearCacheForType(_mediaType);

            using var db = new VaultContext();
            _allItems = await db.MediaItems
                .Where(m => m.MediaType == _mediaType)
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

                        int tmdbId = tile.Item.TmdbId;
                        if (tmdbId == 0)
                        {
                            int? found = await tmdb.SearchAsync(
                                tile.Title, isSeries, tile.Item.Year, tile.MediaType);
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
                                tile.Id, details.PosterPath, tmdbId);

                        string? bannerPath = null;
                        if (!string.IsNullOrEmpty(details.BackdropPath))
                            bannerPath = await tmdb.DownloadBannerAsync(
                                tile.Id, details.BackdropPath, tmdbId);

                        if (posterPath != null)
                        {
                            try
                            {
                                var capturedPath = posterPath;
                                var capturedId = tile.Id;
                                var bmp = await Task.Run(() =>
                                {
                                    var b = new BitmapImage();
                                    b.BeginInit();
                                    b.UriSource = new Uri(capturedPath);
                                    b.CacheOption = BitmapCacheOption.OnLoad;
                                    b.DecodePixelWidth = 150;
                                    b.EndInit();
                                    b.Freeze();
                                    return b;
                                });

                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    var liveTile = _tiles.FirstOrDefault(t => t.Id == capturedId);
                                    if (liveTile != null)
                                    {
                                        liveTile.PosterPath = capturedPath;
                                        liveTile.LoadedBitmap = bmp;
                                    }
                                    MediaTileViewModel.InvalidateCache(capturedId);
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
            if (_view == null) { ApplyFilters(); return; }
            ApplySortToView();
        }

        private void ApplyFilters()
        {
            if (MediaItemsControl == null || _allItems == null) return;

            var filtered = _allItems.AsEnumerable();

            if (_currentStatus != "All")
                filtered = filtered.Where(m => m.WatchStatus == _currentStatus);

            int sortIdx = SortCombo?.SelectedIndex ?? 0;
            var list = sortIdx switch
            {
                0 => filtered.OrderBy(m => m.Title),
                1 => filtered.OrderByDescending(m => m.Title),
                2 => filtered.OrderByDescending(m => m.Year),
                3 => filtered.OrderBy(m => m.Year),
                4 => filtered.OrderByDescending(m => m.TmdbRating),
                5 => filtered.OrderBy(m => m.WatchStatus),
                _ => filtered.OrderBy(m => m.Title)
            };

            var sortedList = list.ToList();

            if (TxtItemCount != null)
                TxtItemCount.Text = $"{sortedList.Count} titles";

            var newTiles = sortedList.Select(m => new MediaTileViewModel(m)).ToList();

            _tiles = new ObservableCollection<MediaTileViewModel>(newTiles);

            // No SortDescriptions on the view — list is already sorted at source.
            // Adding SortDescriptions causes WPF to re-sort on every
            // OnPropertyChanged (including LoadedBitmap), making tiles jump.
            _view = new ListCollectionView(_tiles);
            _view.IsLiveSorting = false;

            MediaItemsControl.ItemsSource = _view;

            var stillMissing = _tiles.Where(t => !t.HasPoster).ToList();
            if (stillMissing.Count > 0)
                _ = FetchMissingPostersAsync(stillMissing);
        }

        private void ApplySortToView()
        {
            if (_view == null) return;
            ApplyFilters();
        }

        private void MediaTile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is MediaTileViewModel tile)
                ItemSelected?.Invoke(this, tile.Item);
        }

        // FIX: opens a random title's detail page from whatever's currently
        // visible in _tiles — respects the active status filter and search box,
        // e.g. filter to "Not Started" first, then hit Random, to only roll
        // from your backlog.
        private void BtnRandom_Click(object sender, RoutedEventArgs e)
        {
            if (_tiles.Count == 0) return;
            var pick = _tiles[_rng.Next(_tiles.Count)];
            ItemSelected?.Invoke(this, pick.Item);
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

            var newTiles = list.Select(m => new MediaTileViewModel(m)).ToList();

            _tiles = new ObservableCollection<MediaTileViewModel>(newTiles);
            _view = new ListCollectionView(_tiles);
            _view.IsLiveSorting = false;

            MediaItemsControl.ItemsSource = _view;
        }
    }
}
