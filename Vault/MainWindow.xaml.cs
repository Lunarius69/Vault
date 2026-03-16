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

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            NavigateTo("Games");
        }

        private void NavigateTo(string section)
        {
            BtnGames.Style = (Style)FindResource("SidebarButton");
            BtnShows.Style = (Style)FindResource("SidebarButton");
            BtnMovies.Style = (Style)FindResource("SidebarButton");
            BtnAnime.Style = (Style)FindResource("SidebarButton");
            BtnWishlist.Style = (Style)FindResource("SidebarButton");

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
                case "Wishlist":
                    BtnWishlist.Style = (Style)FindResource("SidebarButtonActive");
                    MainContent.Content = new WishlistPage(_settings);
                    break;
                case "Settings":
                    MainContent.Content = new SettingsPage();
                    break;
            }
        }

        private void OnGameSelected(object? sender, Game game)
        {
            var detailPage = new GameDetailPage(game, _settings);
            detailPage.BackRequested += (s, e) =>
                MainContent.Content = _gamesPage;
            MainContent.Content = detailPage;
        }

        private void OnMediaItemSelected(object? sender, MediaItem item)
        {
            var detailPage = new MediaDetailPage(item, _settings);
            detailPage.BackRequested += (s, e) =>
                MainContent.Content = _currentMediaPage;
            MainContent.Content = detailPage;
        }

        private void BtnGames_Click(object sender, RoutedEventArgs e) => NavigateTo("Games");
        private void BtnShows_Click(object sender, RoutedEventArgs e) => NavigateTo("Shows");
        private void BtnMovies_Click(object sender, RoutedEventArgs e) => NavigateTo("Movies");
        private void BtnAnime_Click(object sender, RoutedEventArgs e) => NavigateTo("Anime");
        private void BtnWishlist_Click(object sender, RoutedEventArgs e) => NavigateTo("Wishlist");
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => NavigateTo("Settings");

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _gamesPage?.Search(SearchBox.Text);
            _currentMediaPage?.Search(SearchBox.Text);
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