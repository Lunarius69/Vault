using System.Configuration;
using System.Windows;
using System.Windows.Controls;
using Vault.Models;
using Vault.Views;

namespace Vault
{
    public partial class MainWindow : Window
    {
        private AppSettings _settings;

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            NavigateTo("Games");
        }

        private void NavigateTo(string section)
        {
            // Reset all sidebar buttons
            BtnGames.Style = (Style)FindResource("SidebarButton");
            BtnShows.Style = (Style)FindResource("SidebarButton");
            BtnMovies.Style = (Style)FindResource("SidebarButton");
            BtnAnime.Style = (Style)FindResource("SidebarButton");
            BtnWishlist.Style = (Style)FindResource("SidebarButton");

            switch (section)
            {
                case "Games":
                    BtnGames.Style = (Style)FindResource("SidebarButtonActive");
                    MainFrame.Navigate(new GamesPage(_settings));
                    break;
                case "Shows":
                    BtnShows.Style = (Style)FindResource("SidebarButtonActive");
                    MainFrame.Navigate(new MediaPage(_settings, "Show"));
                    break;
                case "Movies":
                    BtnMovies.Style = (Style)FindResource("SidebarButtonActive");
                    MainFrame.Navigate(new MediaPage(_settings, "Movie"));
                    break;
                case "Anime":
                    BtnAnime.Style = (Style)FindResource("SidebarButtonActive");
                    MainFrame.Navigate(new MediaPage(_settings, "Anime"));
                    break;
                case "Wishlist":
                    BtnWishlist.Style = (Style)FindResource("SidebarButtonActive");
                    MainFrame.Navigate(new WishlistPage(_settings));
                    break;
                case "Settings":
                    MainFrame.Navigate(new SettingsPage());
                    break;
            }
        }

        private void BtnGames_Click(object sender, RoutedEventArgs e) => NavigateTo("Games");
        private void BtnShows_Click(object sender, RoutedEventArgs e) => NavigateTo("Shows");
        private void BtnMovies_Click(object sender, RoutedEventArgs e) => NavigateTo("Movies");
        private void BtnAnime_Click(object sender, RoutedEventArgs e) => NavigateTo("Anime");
        private void BtnWishlist_Click(object sender, RoutedEventArgs e) => NavigateTo("Wishlist");
        private void BtnSettings_Click(object sender, RoutedEventArgs e) => NavigateTo("Settings");

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Will be wired up to each page's search method
        }

        private void BtnGridView_Click(object sender, RoutedEventArgs e)
        {
            BtnGridView.Background = System.Windows.Media.Brushes.Transparent;
            BtnListView.Background = System.Windows.Media.Brushes.Transparent;
            var redBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#e94560")!;
            var darkBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#2d3561")!;
            BtnGridView.Background = redBrush;
            BtnListView.Background = darkBrush;
        }

        private void BtnListView_Click(object sender, RoutedEventArgs e)
        {
            var redBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#e94560")!;
            var darkBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#2d3561")!;
            BtnGridView.Background = darkBrush;
            BtnListView.Background = redBrush;
        }
    }
}