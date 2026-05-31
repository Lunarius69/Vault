using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vault.Models;
using Vault.Services;
using Vault.Views;

namespace Vault
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly ProcessWatcherService _processWatcher;
        private GamesPage? _gamesPage;
        private MediaPage? _showsPage;
        private MediaPage? _moviesPage;
        private MediaPage? _animePage;
        private MediaPage? _animeMovPage;
        private MediaPage? _animatedPage;
        private MediaPage? _animatedMovPage;
        private StatsPage? _statsPage;
        private MediaPage? _currentMediaPage;
        private System.Threading.CancellationTokenSource? _searchCts;

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();

            _gamesPage = new GamesPage(_settings);
            _gamesPage.GameSelected += OnGameSelected;

            _processWatcher = new ProcessWatcherService();
            ProcessWatcherService.PlaytimeUpdated += OnPlaytimeUpdated;

            Closing += async (s, e) =>
            {
                ProcessWatcherService.PlaytimeUpdated -= OnPlaytimeUpdated;
                await _processWatcher.FlushAsync();
            };

            _ = CleanupOnStartupAsync();

            NavigateTo("Games");
        }

        private void OnPlaytimeUpdated(int gameId)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (MainContent?.Content == _gamesPage && _gamesPage != null)
                    await _gamesPage.RefreshAsync();
            });
        }

        private static async Task CleanupOnStartupAsync()
        {
            try
            {
                int removed = await ExcelImporter.CleanupMismatchedGamesAsync();
                if (removed > 0)
                    System.Diagnostics.Debug.WriteLine(
                        $"[Startup] Removed {removed} mismatched game rows from DB.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Startup] CleanupMismatchedGamesAsync failed: {ex.Message}");
            }

            // Reset any previously wrong TMDB matches so they re-fetch with the
            // corrected genre filter. Safe to leave here permanently — once TmdbId
            // is re-set correctly the items won't be in this list anymore and the
            // call becomes a no-op. Add any new wrong titles to this list as needed.
            try
            {
                int reset = await TmdbService.ResetWrongTmdbMatchesAsync(
                    "Bubble",
                    "Belle",
                    "My Happy Marriage",
                    "Princess Mononoke 2",
                    "Black Clover: Sword of the Wizard King",
                    "Fullmetal Alchemist (2003)"
                );
                if (reset > 0)
                    System.Diagnostics.Debug.WriteLine(
                        $"[Startup] Reset {reset} wrong TMDB matches.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Startup] ResetWrongTmdbMatchesAsync failed: {ex.Message}");
            }
        }

        private void NavigateTo(string section)
        {
            if (SearchBox != null)
                SearchBox.Text = string.Empty;

            if (BtnGames != null) BtnGames.Style = (Style)FindResource("SidebarButton");
            if (BtnShows != null) BtnShows.Style = (Style)FindResource("SidebarButton");
            if (BtnMovies != null) BtnMovies.Style = (Style)FindResource("SidebarButton");
            if (BtnAnime != null) BtnAnime.Style = (Style)FindResource("SidebarButton");
            if (BtnAnimeMov != null) BtnAnimeMov.Style = (Style)FindResource("SidebarButton");
            if (BtnAnimated != null) BtnAnimated.Style = (Style)FindResource("SidebarButton");
            if (BtnAnimatedMov != null) BtnAnimatedMov.Style = (Style)FindResource("SidebarButton");
            if (BtnStats != null) BtnStats.Style = (Style)FindResource("SidebarButton");

            _currentMediaPage = null;

            switch (section)
            {
                case "Games":
                    if (BtnGames != null) BtnGames.Style = (Style)FindResource("SidebarButtonActive");
                    if (MainContent != null && _gamesPage != null)
                        MainContent.Content = _gamesPage;
                    break;

                case "Shows":
                    if (BtnShows != null) BtnShows.Style = (Style)FindResource("SidebarButtonActive");
                    _showsPage ??= CreateMediaPage("Show");
                    _currentMediaPage = _showsPage;
                    if (MainContent != null) MainContent.Content = _showsPage;
                    break;

                case "Movies":
                    if (BtnMovies != null) BtnMovies.Style = (Style)FindResource("SidebarButtonActive");
                    _moviesPage ??= CreateMediaPage("Movie");
                    _currentMediaPage = _moviesPage;
                    if (MainContent != null) MainContent.Content = _moviesPage;
                    break;

                case "Anime":
                    if (BtnAnime != null) BtnAnime.Style = (Style)FindResource("SidebarButtonActive");
                    _animePage ??= CreateMediaPage("Anime");
                    _currentMediaPage = _animePage;
                    if (MainContent != null) MainContent.Content = _animePage;
                    break;

                case "AnimeMov":
                    if (BtnAnimeMov != null) BtnAnimeMov.Style = (Style)FindResource("SidebarButtonActive");
                    _animeMovPage ??= CreateMediaPage("AnimeMovie");
                    _currentMediaPage = _animeMovPage;
                    if (MainContent != null) MainContent.Content = _animeMovPage;
                    break;

                case "Animated":
                    if (BtnAnimated != null) BtnAnimated.Style = (Style)FindResource("SidebarButtonActive");
                    _animatedPage ??= CreateMediaPage("AnimatedSeries");
                    _currentMediaPage = _animatedPage;
                    if (MainContent != null) MainContent.Content = _animatedPage;
                    break;

                case "AnimatedMov":
                    if (BtnAnimatedMov != null) BtnAnimatedMov.Style = (Style)FindResource("SidebarButtonActive");
                    _animatedMovPage ??= CreateMediaPage("AnimatedMovie");
                    _currentMediaPage = _animatedMovPage;
                    if (MainContent != null) MainContent.Content = _animatedMovPage;
                    break;

                case "Stats":
                    if (BtnStats != null) BtnStats.Style = (Style)FindResource("SidebarButtonActive");
                    if (_statsPage == null)
                        _statsPage = new StatsPage();
                    else
                        _ = _statsPage.RefreshAsync();
                    if (MainContent != null) MainContent.Content = _statsPage;
                    break;

                case "Settings":
                    if (MainContent != null)
                        MainContent.Content = new SettingsPage();
                    break;
            }
        }

        private MediaPage CreateMediaPage(string mediaType)
        {
            var page = new MediaPage(_settings, mediaType);
            page.ItemSelected += OnMediaItemSelected;
            return page;
        }

        private void OnGameSelected(object? sender, Game game)
        {
            if (SearchBox != null)
                SearchBox.Text = string.Empty;

            var detailPage = new GameDetailPage(game, _settings);

            detailPage.BackRequested += async (s, e) =>
            {
                if (MainContent != null && _gamesPage != null)
                {
                    MainContent.Content = _gamesPage;
                    await _gamesPage.RefreshAsync();
                }
            };

            detailPage.GameUpdated += async (s, e) =>
            {
                if (_gamesPage != null)
                    await _gamesPage.RefreshAsync();
            };

            // ── When user moves a mis-categorised game to a media category: ──────
            // 1. Instantly remove the tile from the games list (no full reload).
            // 2. Force-refresh the destination MediaPage so it shows the new item
            //    next time the user navigates there.
            detailPage.MovedToMedia += (s, mediaType) =>
            {
                // Remove tile immediately from the in-memory games list
                _gamesPage?.RemoveGameById(game.Id);

                // Invalidate the cached destination page so it reloads fresh
                // next time the user clicks that sidebar button.
                switch (mediaType)
                {
                    case "Movie":
                        _moviesPage = null;
                        break;
                    case "AnimeMovie":
                        _animeMovPage = null;
                        break;
                    case "Show":
                        _showsPage = null;
                        break;
                    case "Anime":
                        _animePage = null;
                        break;
                    case "AnimatedMovie":
                        _animatedMovPage = null;
                        break;
                    case "AnimatedSeries":
                        _animatedPage = null;
                        break;
                }
            };

            if (MainContent != null)
                MainContent.Content = detailPage;
        }

        private void OnMediaItemSelected(object? sender, MediaItem item)
        {
            if (SearchBox != null)
                SearchBox.Text = string.Empty;

            var detailPage = new MediaDetailPage(item, _settings);
            detailPage.BackRequested += (s, e) =>
            {
                if (MainContent != null && _currentMediaPage != null)
                {
                    MainContent.Content = _currentMediaPage;
                    _currentMediaPage.Refresh();
                }
            };
            if (MainContent != null)
                MainContent.Content = detailPage;
        }

        private void BtnGames_Click(object sender, RoutedEventArgs e) => NavigateTo("Games");
        private void BtnShows_Click(object sender, RoutedEventArgs e) => NavigateTo("Shows");
        private void BtnMovies_Click(object sender, RoutedEventArgs e) => NavigateTo("Movies");
        private void BtnAnime_Click(object sender, RoutedEventArgs e) => NavigateTo("Anime");
        private void BtnAnimeMov_Click(object sender, RoutedEventArgs e) => NavigateTo("AnimeMov");
        private void BtnAnimated_Click(object sender, RoutedEventArgs e) => NavigateTo("Animated");
        private void BtnAnimatedMov_Click(object sender, RoutedEventArgs e) => NavigateTo("AnimatedMov");
        private void BtnStats_Click(object sender, RoutedEventArgs e) => NavigateTo("Stats");
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => NavigateTo("Settings");

        private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchCts?.Cancel();
            _searchCts = new System.Threading.CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested && SearchBox != null)
                {
                    _gamesPage?.Search(SearchBox.Text);
                    _currentMediaPage?.Search(SearchBox.Text);
                }
            }
            catch (TaskCanceledException) { }
        }
    }
}