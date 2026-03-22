using System.Windows;
using System.Windows.Controls;
using Vault.Models;
using Vault.Views;

namespace Vault
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings;
        private GamesPage? _gamesPage;
        private MediaPage? _currentMediaPage;
        private System.Threading.CancellationTokenSource? _searchCts;

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            NavigateTo("Games");
        }

        private void NavigateTo(string section)
        {
            // Clear search on every navigation
            SearchBox.Text = string.Empty;

            BtnGames.Style = (Style)FindResource("SidebarButton");
            BtnShows.Style = (Style)FindResource("SidebarButton");
            BtnMovies.Style = (Style)FindResource("SidebarButton");
            BtnAnime.Style = (Style)FindResource("SidebarButton");
            BtnAnimeMov.Style = (Style)FindResource("SidebarButton");
            BtnAnimated.Style = (Style)FindResource("SidebarButton");
            BtnAnimatedMov.Style = (Style)FindResource("SidebarButton");
            BtnStats.Style = (Style)FindResource("SidebarButton");

            _currentMediaPage = null;

            switch (section)
            {
                case "Games":
                    BtnGames.Style = (Style)FindResource("SidebarButtonActive");
                    _gamesPage = new GamesPage(_settings);
                    _gamesPage.GameSelected += OnGameSelected;
                    MainContent.Content = _gamesPage;
                    break;
                case "Shows":
                    BtnShows.Style = (Style)FindResource("SidebarButtonActive");
                    _currentMediaPage = new MediaPage(_settings, "Show");
                    _currentMediaPage.ItemSelected += OnMediaItemSelected;
                    MainContent.Content = _currentMediaPage;
                    break;
                case "Movies":
                    BtnMovies.Style = (Style)FindResource("SidebarButtonActive");
                    _currentMediaPage = new MediaPage(_settings, "Movie");
                    _currentMediaPage.ItemSelected += OnMediaItemSelected;
                    MainContent.Content = _currentMediaPage;
                    break;
                case "Anime":
                    BtnAnime.Style = (Style)FindResource("SidebarButtonActive");
                    _currentMediaPage = new MediaPage(_settings, "Anime");
                    _currentMediaPage.ItemSelected += OnMediaItemSelected;
                    MainContent.Content = _currentMediaPage;
                    break;
                case "AnimeMov":
                    BtnAnimeMov.Style = (Style)FindResource("SidebarButtonActive");
                    _currentMediaPage = new MediaPage(_settings, "AnimeMovie");
                    _currentMediaPage.ItemSelected += OnMediaItemSelected;
                    MainContent.Content = _currentMediaPage;
                    break;
                case "Animated":
                    BtnAnimated.Style = (Style)FindResource("SidebarButtonActive");
                    _currentMediaPage = new MediaPage(_settings, "AnimatedSeries");
                    _currentMediaPage.ItemSelected += OnMediaItemSelected;
                    MainContent.Content = _currentMediaPage;
                    break;
                case "AnimatedMov":
                    BtnAnimatedMov.Style = (Style)FindResource("SidebarButtonActive");
                    _currentMediaPage = new MediaPage(_settings, "AnimatedMovie");
                    _currentMediaPage.ItemSelected += OnMediaItemSelected;
                    MainContent.Content = _currentMediaPage;
                    break;
                case "Stats":
                    BtnStats.Style = (Style)FindResource("SidebarButtonActive");
                    MainContent.Content = new StatsPage();
                    break;
                case "Settings":
                    MainContent.Content = new SettingsPage();
                    break;
            }
        }

        private void OnGameSelected(object? sender, Game game)
        {
            SearchBox.Text = string.Empty;
            var detailPage = new GameDetailPage(game, _settings);
            detailPage.BackRequested += async (s, e) =>
            {
                MainContent.Content = _gamesPage;
                if (_gamesPage != null)
                    await _gamesPage.RefreshAsync();
            };
            detailPage.GameUpdated += async (s, e) =>
            {
                if (_gamesPage != null)
                    await _gamesPage.RefreshAsync();
            };
            MainContent.Content = detailPage;
        }

        private void OnMediaItemSelected(object? sender, MediaItem item)
        {
            SearchBox.Text = string.Empty;
            var detailPage = new MediaDetailPage(item, _settings);
            detailPage.BackRequested += (s, e) =>
                MainContent.Content = _currentMediaPage;
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
                await System.Threading.Tasks.Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                {
                    _gamesPage?.Search(SearchBox.Text);
                    _currentMediaPage?.Search(SearchBox.Text);
                }
            }
            catch (System.Threading.Tasks.TaskCanceledException) { }
        }

        private void BtnGridView_Click(object sender, RoutedEventArgs e)
        {
            var red = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom("#e94560")!;
            var dark = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom("#2d3561")!;
            BtnGridView.Background = red;
            BtnListView.Background = dark;
            _gamesPage?.SetViewMode(true);
        }

        private void BtnListView_Click(object sender, RoutedEventArgs e)
        {
            var red = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom("#e94560")!;
            var dark = (System.Windows.Media.Brush)
                new System.Windows.Media.BrushConverter().ConvertFrom("#2d3561")!;
            BtnGridView.Background = dark;
            BtnListView.Background = red;
            _gamesPage?.SetViewMode(false);
        }
    }
}