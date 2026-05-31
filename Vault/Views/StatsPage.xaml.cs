using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Vault.Database;
using Vault.Models;

namespace Vault.Views
{
    public partial class StatsPage : UserControl
    {
        private List<Game> _allGames = new();
        private List<MediaItem> _allMedia = new();

        public StatsPage()
        {
            InitializeComponent();
            Loaded += StatsPage_Loaded;
        }

        private async void StatsPage_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            await RefreshAsync();
        }

        public async Task RefreshAsync()
        {
            using var db = new VaultContext();

            _allGames = await db.Games
                .Where(g => !g.IsWishlist)
                .AsNoTracking()
                .ToListAsync();

            _allMedia = await db.MediaItems
                .AsNoTracking()
                .ToListAsync();

            UpdateStats();
        }

        private static string FormatWatchTime(long minutes)
        {
            if (minutes <= 0) return "0m";
            long hours = minutes / 60;
            long mins = minutes % 60;
            if (hours == 0) return $"{mins}m";
            if (mins == 0) return $"{hours}h";
            return $"{hours}h {mins}m";
        }

        private void UpdateStats()
        {
            // ── Games ─────────────────────────────────────────────────────────
            TxtTotalGames.Text = _allGames.Count.ToString();
            TxtPlayingGames.Text = _allGames.Count(g => g.Status == "Playing").ToString();
            TxtCompletedGames.Text = _allGames.Count(g => g.Status == "Completed").ToString();
            TxtNotStartedGames.Text = _allGames.Count(g => g.Status == "Not Started").ToString();
            TxtDownloadedGames.Text = _allGames.Count(g => g.IsDownloaded).ToString();

            long totalPlaytimeMinutes = _allGames.Sum(g => g.PlaytimeMinutes);
            TxtTotalPlaytime.Text = FormatWatchTime(totalPlaytimeMinutes);

            // ── Platform distribution ─────────────────────────────────────────
            var platformStats = _allGames
                .GroupBy(g => g.Platform)
                .Select(g => new { Platform = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToList();
            PlatformListBox.ItemsSource = platformStats;

            // ── Media helpers ─────────────────────────────────────────────────
            var shows = _allMedia.Where(m => m.MediaType == "Show").ToList();
            var movies = _allMedia.Where(m => m.MediaType == "Movie").ToList();
            var anime = _allMedia.Where(m => m.MediaType == "Anime").ToList();
            var animeMovies = _allMedia.Where(m => m.MediaType == "AnimeMovie").ToList();
            var animatedSeries = _allMedia.Where(m => m.MediaType == "AnimatedSeries").ToList();
            var animatedMovies = _allMedia.Where(m => m.MediaType == "AnimatedMovie").ToList();

            long totalWatchTime = _allMedia.Sum(m => m.WatchTimeMinutes);

            // ── Media overview ────────────────────────────────────────────────
            TxtWatchingMedia.Text = _allMedia.Count(m => m.WatchStatus == "Watching").ToString();
            TxtCompletedMedia.Text = _allMedia.Count(m => m.WatchStatus == "Completed").ToString();
            TxtTotalWatchTime.Text = FormatWatchTime(totalWatchTime);

            // ── TV Shows ──────────────────────────────────────────────────────
            TxtTotalShows.Text = shows.Count.ToString();
            TxtWatchingShows.Text = shows.Count(m => m.WatchStatus == "Watching").ToString();
            TxtShowsEpisodes.Text = shows.Sum(m => m.WatchedEpisodes).ToString();
            TxtShowsWatchTime.Text = FormatWatchTime(shows.Sum(m => m.WatchTimeMinutes));

            // ── Movies ────────────────────────────────────────────────────────
            TxtTotalMovies.Text = movies.Count.ToString();
            TxtWatchedMovies.Text = movies.Count(m => m.WatchStatus == "Completed").ToString();
            TxtMoviesWatchTime.Text = FormatWatchTime(movies.Sum(m => m.WatchTimeMinutes));

            // ── Anime Series ──────────────────────────────────────────────────
            TxtTotalAnime.Text = anime.Count.ToString();
            TxtWatchingAnime.Text = anime.Count(m => m.WatchStatus == "Watching").ToString();
            TxtAnimeEpisodes.Text = anime.Sum(m => m.WatchedEpisodes).ToString();
            TxtAnimeWatchTime.Text = FormatWatchTime(anime.Sum(m => m.WatchTimeMinutes));

            // ── Anime Movies ──────────────────────────────────────────────────
            TxtTotalAnimeMovies.Text = animeMovies.Count.ToString();
            TxtWatchedAnimeMovies.Text = animeMovies.Count(m => m.WatchStatus == "Completed").ToString();
            TxtAnimeMoviesWatchTime.Text = FormatWatchTime(animeMovies.Sum(m => m.WatchTimeMinutes));

            // ── Animated Series ───────────────────────────────────────────────
            TxtTotalAnimatedSeries.Text = animatedSeries.Count.ToString();
            TxtWatchingAnimatedSeries.Text = animatedSeries.Count(m => m.WatchStatus == "Watching").ToString();
            TxtAnimatedSeriesEpisodes.Text = animatedSeries.Sum(m => m.WatchedEpisodes).ToString();
            TxtAnimatedSeriesWatchTime.Text = FormatWatchTime(animatedSeries.Sum(m => m.WatchTimeMinutes));

            // ── Animated Movies ───────────────────────────────────────────────
            TxtTotalAnimatedMovies.Text = animatedMovies.Count.ToString();
            TxtWatchedAnimatedMovies.Text = animatedMovies.Count(m => m.WatchStatus == "Completed").ToString();
            TxtAnimatedMoviesWatchTime.Text = FormatWatchTime(animatedMovies.Sum(m => m.WatchTimeMinutes));
        }
    }
}