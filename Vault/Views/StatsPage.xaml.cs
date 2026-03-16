using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vault.Database;

namespace Vault.Views
{
    public partial class StatsPage : UserControl
    {
        public StatsPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadStatsAsync();
        }

        private async System.Threading.Tasks.Task LoadStatsAsync()
        {
            using var db = new VaultContext();

            // ── Games ──────────────────────────────────────────────
            var games = await db.Games
                .Where(g => !g.IsWishlist)
                .ToListAsync();

            int totalGames = games.Count;
            int downloadedGames = games.Count(g => g.IsDownloaded);
            int totalPlaytimeMinutes = games.Sum(g => g.PlaytimeMinutes);
            double totalLibrarySizeGB = games
                .Where(g => g.FileSizeGB.HasValue)
                .Sum(g => g.FileSizeGB!.Value);

            TxtTotalGames.Text = totalGames.ToString();
            TxtTotalGamesSub.Text = $"{downloadedGames} downloaded";

            TxtTotalPlaytime.Text = totalPlaytimeMinutes >= 60
                ? $"{totalPlaytimeMinutes / 60}h"
                : $"{totalPlaytimeMinutes}m";
            TxtTotalPlaytimeSub.Text = $"{totalPlaytimeMinutes / 60} hours total";

            // ── Media ──────────────────────────────────────────────
            var media = await db.MediaItems.ToListAsync();

            int totalMediaWatched = media.Count(m => m.WatchStatus == "Completed");
            int totalMediaWatching = media.Count(m => m.WatchStatus == "Watching");
            double mediaLibrarySizeGB = 0; // Media size not tracked yet

            TxtTotalMedia.Text = totalMediaWatched.ToString();
            TxtTotalMediaSub.Text = $"{totalMediaWatching} currently watching";

            // Library size (games + media)
            double totalGB = totalLibrarySizeGB;
            TxtLibrarySize.Text = totalGB >= 1024
                ? $"{totalGB / 1024:F1} TB"
                : $"{totalGB:F0} GB";
            TxtLibrarySizeSub.Text = $"Games library";

            // Watch time estimate (avg 45min per episode)
            int totalEpisodesWatched = media
                .Where(m => m.MediaType == "Show" || m.MediaType == "Anime" ||
                            m.MediaType == "AnimatedSeries")
                .Sum(m => m.WatchedEpisodes);
            int estimatedWatchMinutes = totalEpisodesWatched * 45;

            TxtWatchTime.Text = estimatedWatchMinutes >= 60
                ? $"{estimatedWatchMinutes / 60}h"
                : $"{estimatedWatchMinutes}m";
            TxtWatchTimeSub.Text = $"{totalEpisodesWatched} episodes watched";

            // Shows and anime
            int showsCompleted = media.Count(m =>
                (m.MediaType == "Show" || m.MediaType == "Anime" ||
                 m.MediaType == "AnimatedSeries") &&
                m.WatchStatus == "Completed");
            int showsWatching = media.Count(m =>
                (m.MediaType == "Show" || m.MediaType == "Anime" ||
                 m.MediaType == "AnimatedSeries") &&
                m.WatchStatus == "Watching");

            TxtShowsWatched.Text = showsCompleted.ToString();
            TxtShowsWatchedSub.Text = $"{showsWatching} in progress";

            // Movies
            int moviesWatched = media.Count(m =>
                (m.MediaType == "Movie" || m.MediaType == "AnimeMovie" ||
                 m.MediaType == "AnimatedMovie") &&
                m.WatchStatus == "Completed");
            int moviesTotal = media.Count(m =>
                m.MediaType == "Movie" || m.MediaType == "AnimeMovie" ||
                m.MediaType == "AnimatedMovie");

            TxtMoviesWatched.Text = moviesWatched.ToString();
            TxtMoviesWatchedSub.Text = $"of {moviesTotal} total";

            // ── Games by status bars ───────────────────────────────
            GameStatusPanel.Children.Clear();
            var statusGroups = games
                .GroupBy(g => g.Status)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var group in statusGroups)
            {
                double pct = totalGames > 0
                    ? (group.Count() / (double)totalGames) * 100.0 : 0;
                string color = group.Key switch
                {
                    "Playing" => "#00b894",
                    "Completed" => "#0984e3",
                    "Not Started" => "#636e72",
                    "On Hold" => "#fdcb6e",
                    "Dropped" => "#d63031",
                    _ => "#2d3561"
                };
                GameStatusPanel.Children.Add(
                    MakeBarRow(group.Key, group.Count(), pct, color));
            }

            // ── Games by platform bars ─────────────────────────────
            PlatformPanel.Children.Clear();
            var platformGroups = games
                .GroupBy(g => g.Platform)
                .OrderByDescending(g => g.Count())
                .Take(8)
                .ToList();

            foreach (var group in platformGroups)
            {
                double pct = totalGames > 0
                    ? (group.Count() / (double)totalGames) * 100.0 : 0;
                PlatformPanel.Children.Add(
                    MakeBarRow(group.Key, group.Count(), pct, "#e94560"));
            }

            // ── Most played games ──────────────────────────────────
            MostPlayedPanel.Children.Clear();
            var mostPlayed = games
                .Where(g => g.PlaytimeMinutes > 0)
                .OrderByDescending(g => g.PlaytimeMinutes)
                .Take(6)
                .ToList();

            if (mostPlayed.Count == 0)
            {
                MostPlayedPanel.Children.Add(MakeEmptyLabel("No playtime recorded yet"));
            }
            else
            {
                int maxMinutes = mostPlayed.First().PlaytimeMinutes;
                foreach (var game in mostPlayed)
                {
                    double pct = maxMinutes > 0
                        ? (game.PlaytimeMinutes / (double)maxMinutes) * 100.0 : 0;
                    string time = game.PlaytimeMinutes >= 60
                        ? $"{game.PlaytimeMinutes / 60}h {game.PlaytimeMinutes % 60}m"
                        : $"{game.PlaytimeMinutes}m";
                    MostPlayedPanel.Children.Add(
                        MakeBarRow(game.Title, 0, pct, "#e94560",
                            rightLabel: time, showCount: false));
                }
            }

            // ── Recently played / watched ──────────────────────────
            RecentPanel.Children.Clear();

            var recentGames = games
                .Where(g => g.LastPlayed.HasValue)
                .OrderByDescending(g => g.LastPlayed)
                .Take(4)
                .Select(g => (
                    Name: g.Title,
                    Sub: g.LastPlayed!.Value.ToString("MMM d, yyyy"),
                    Icon: "🎮"))
                .ToList();

            var recentMedia = media
                .Where(m => m.WatchStatus == "Watching" || m.WatchStatus == "Completed")
                .OrderByDescending(m => m.Id)
                .Take(4)
                .Select(m => (
                    Name: m.Title,
                    Sub: m.WatchStatus == "Watching"
                        ? $"EP {m.WatchedEpisodes}/{m.TotalEpisodes}"
                        : "Completed",
                    Icon: m.MediaType == "Movie" || m.MediaType == "AnimeMovie"
                        ? "🎬" : "📺"))
                .ToList();

            var allRecent = recentGames
                .Concat(recentMedia)
                .Take(8)
                .ToList();

            if (allRecent.Count == 0)
            {
                RecentPanel.Children.Add(MakeEmptyLabel("Nothing recently played"));
            }
            else
            {
                foreach (var item in allRecent)
                    RecentPanel.Children.Add(MakeRecentRow(item.Icon, item.Name, item.Sub));
            }
        }

        // Makes a labeled bar row used for status/platform/most played
        private static StackPanel MakeBarRow(
            string label, int count, double pct, string color,
            string? rightLabel = null, bool showCount = true)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#b2bec3")),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameText, 0);

            var countText = new TextBlock
            {
                Text = rightLabel ?? (showCount ? count.ToString() : ""),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#636e72")),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(countText, 1);

            headerGrid.Children.Add(nameText);
            headerGrid.Children.Add(countText);
            panel.Children.Add(headerGrid);

            // Bar track
            var track = new Grid
            {
                Height = 6,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var bg = new Border
            {
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#2d3561")),
                CornerRadius = new CornerRadius(3)
            };

            var fill = new Border
            {
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(color)),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0 // set after layout
            };

            track.Children.Add(bg);
            track.Children.Add(fill);
            panel.Children.Add(track);

            // Set bar width after layout
            track.Loaded += (s, e) =>
            {
                fill.Width = track.ActualWidth * pct / 100.0;
            };

            return panel;
        }

        private static TextBlock MakeEmptyLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#636e72")),
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        private static Grid MakeRecentRow(string icon, string name, string sub)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var iconBlock = new TextBlock
            {
                Text = icon,
                FontSize = 18,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconBlock, 0);

            var infoPanel = new StackPanel();
            infoPanel.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#ffffff")),
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            infoPanel.Children.Add(new TextBlock
            {
                Text = sub,
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#636e72")),
                FontSize = 11,
                FontFamily = new FontFamily("Segoe UI")
            });
            Grid.SetColumn(infoPanel, 1);

            grid.Children.Add(iconBlock);
            grid.Children.Add(infoPanel);
            return grid;
        }
    }
}