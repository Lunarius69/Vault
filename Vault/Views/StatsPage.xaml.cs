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

            // Load all games and media
            _allGames = await db.Games
                .Where(g => !g.IsWishlist)
                .AsNoTracking()
                .ToListAsync();

            _allMedia = await db.MediaItems
                .AsNoTracking()
                .ToListAsync();

            // Update UI with stats
            UpdateStats();
        }

        private void UpdateStats()
        {
            // Games Statistics
            int totalGames = _allGames.Count;
            int playingGames = _allGames.Count(g => g.Status == "Playing");
            int completedGames = _allGames.Count(g => g.Status == "Completed");
            int notStartedGames = _allGames.Count(g => g.Status == "Not Started");
            int downloadedGames = _allGames.Count(g => g.IsDownloaded);
            long totalPlaytimeMinutes = _allGames.Sum(g => g.PlaytimeMinutes);
            string totalPlaytime = $"{totalPlaytimeMinutes / 60}h {totalPlaytimeMinutes % 60}m";

            // Platform distribution
            var platformStats = _allGames
                .GroupBy(g => g.Platform)
                .Select(g => new { Platform = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToList();

            // Media Statistics
            int totalMovies = _allMedia.Count(m => m.MediaType == "Movie" || m.MediaType == "AnimeMovie" || m.MediaType == "AnimatedMovie");
            int totalShows = _allMedia.Count(m => m.MediaType == "Show" || m.MediaType == "Anime" || m.MediaType == "AnimatedSeries");
            int watchingMedia = _allMedia.Count(m => m.WatchStatus == "Watching");
            int completedMedia = _allMedia.Count(m => m.WatchStatus == "Completed");

            // Update UI elements (assuming you have these TextBlocks in your XAML)
            // If you don't have these controls, create them or remove these lines
            if (FindName("TxtTotalGames") is TextBlock txtTotalGames)
                txtTotalGames.Text = totalGames.ToString();

            if (FindName("TxtPlayingGames") is TextBlock txtPlayingGames)
                txtPlayingGames.Text = playingGames.ToString();

            if (FindName("TxtCompletedGames") is TextBlock txtCompletedGames)
                txtCompletedGames.Text = completedGames.ToString();

            if (FindName("TxtNotStartedGames") is TextBlock txtNotStartedGames)
                txtNotStartedGames.Text = notStartedGames.ToString();

            if (FindName("TxtDownloadedGames") is TextBlock txtDownloadedGames)
                txtDownloadedGames.Text = downloadedGames.ToString();

            if (FindName("TxtTotalPlaytime") is TextBlock txtTotalPlaytime)
                txtTotalPlaytime.Text = totalPlaytime;

            if (FindName("TxtTotalMovies") is TextBlock txtTotalMovies)
                txtTotalMovies.Text = totalMovies.ToString();

            if (FindName("TxtTotalShows") is TextBlock txtTotalShows)
                txtTotalShows.Text = totalShows.ToString();

            if (FindName("TxtWatchingMedia") is TextBlock txtWatchingMedia)
                txtWatchingMedia.Text = watchingMedia.ToString();

            if (FindName("TxtCompletedMedia") is TextBlock txtCompletedMedia)
                txtCompletedMedia.Text = completedMedia.ToString();

            // Update platform list if you have a ListBox for platforms
            if (FindName("PlatformListBox") is ListBox platformListBox)
            {
                platformListBox.ItemsSource = platformStats;
            }
        }
    }
}