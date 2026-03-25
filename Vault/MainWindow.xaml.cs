using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vault.Models;
using Vault.Views;

namespace Vault
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
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

            NavigateTo("Games");
        }

        private void NavigateTo(string section)
        {
            if (SearchBox != null)
                SearchBox.Text = string.Empty;

            // Reset all sidebar buttons
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
                    if (MainContent != null && _showsPage != null)
                        MainContent.Content = _showsPage;
                    break;

                case "Movies":
                    if (BtnMovies != null) BtnMovies.Style = (Style)FindResource("SidebarButtonActive");
                    _moviesPage ??= CreateMediaPage("Movie");
                    _currentMediaPage = _moviesPage;
                    if (MainContent != null && _moviesPage != null)
                        MainContent.Content = _moviesPage;
                    break;

                case "Anime":
                    if (BtnAnime != null) BtnAnime.Style = (Style)FindResource("SidebarButtonActive");
                    _animePage ??= CreateMediaPage("Anime");
                    _currentMediaPage = _animePage;
                    if (MainContent != null && _animePage != null)
                        MainContent.Content = _animePage;
                    break;

                case "AnimeMov":
                    if (BtnAnimeMov != null) BtnAnimeMov.Style = (Style)FindResource("SidebarButtonActive");
                    _animeMovPage ??= CreateMediaPage("AnimeMovie");
                    _currentMediaPage = _animeMovPage;
                    if (MainContent != null && _animeMovPage != null)
                        MainContent.Content = _animeMovPage;
                    break;

                case "Animated":
                    if (BtnAnimated != null) BtnAnimated.Style = (Style)FindResource("SidebarButtonActive");
                    _animatedPage ??= CreateMediaPage("AnimatedSeries");
                    _currentMediaPage = _animatedPage;
                    if (MainContent != null && _animatedPage != null)
                        MainContent.Content = _animatedPage;
                    break;

                case "AnimatedMov":
                    if (BtnAnimatedMov != null) BtnAnimatedMov.Style = (Style)FindResource("SidebarButtonActive");
                    _animatedMovPage ??= CreateMediaPage("AnimatedMovie");
                    _currentMediaPage = _animatedMovPage;
                    if (MainContent != null && _animatedMovPage != null)
                        MainContent.Content = _animatedMovPage;
                    break;

                case "Stats":
                    if (BtnStats != null) BtnStats.Style = (Style)FindResource("SidebarButtonActive");
                    if (_statsPage == null)
                        _statsPage = new StatsPage();
                    else
                        _ = _statsPage.RefreshAsync();
                    if (MainContent != null && _statsPage != null)
                        MainContent.Content = _statsPage;
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
                    MainContent.Content = _currentMediaPage;
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

        private void BtnGridView_Click(object sender, RoutedEventArgs e)
        {
            var red = (Brush)new BrushConverter().ConvertFrom("#e94560")!;
            var dark = (Brush)new BrushConverter().ConvertFrom("#2d3561")!;
            if (BtnGridView != null) BtnGridView.Background = red;
            if (BtnListView != null) BtnListView.Background = dark;
            _gamesPage?.SetViewMode(true);
        }

        private void BtnListView_Click(object sender, RoutedEventArgs e)
        {
            var red = (Brush)new BrushConverter().ConvertFrom("#e94560")!;
            var dark = (Brush)new BrushConverter().ConvertFrom("#2d3561")!;
            if (BtnGridView != null) BtnGridView.Background = dark;
            if (BtnListView != null) BtnListView.Background = red;
            _gamesPage?.SetViewMode(false);
        }
    }
}