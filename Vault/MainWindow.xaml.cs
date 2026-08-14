using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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

        // FIX: WindowStyle="None" + WindowState="Maximized" together is a known
        // WPF gotcha — without native window chrome, Windows doesn't
        // automatically shrink the maximized window to sit above the taskbar,
        // so it maximizes to the FULL monitor bounds and covers the taskbar
        // instead. That pushed content (like the bottom of Settings) down
        // below the visible screen. This hooks the raw WM_GETMINMAXINFO
        // window message — the same mechanism Windows itself uses to compute
        // maximize bounds — and constrains it to the monitor's actual work
        // area (screen minus taskbar), which is the standard fix for
        // chromeless WPF windows.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var source = (HwndSource)PresentationSource.FromVisual(this);
            source?.AddHook(WindowProc);
        }

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                GetMonitorInfo(monitor, ref monitorInfo);

                RECT workArea = monitorInfo.rcWork;
                RECT monitorArea = monitorInfo.rcMonitor;

                mmi.ptMaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
                mmi.ptMaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
                mmi.ptMaxSize.X = Math.Abs(workArea.Right - workArea.Left);
                mmi.ptMaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();

            // FIX: custom title bar buttons — the maximize/restore icon needs
            // to reflect the actual window state, including when the user
            // double-clicks the title bar or drags to a monitor edge (Aero
            // Snap), not just when the button itself is clicked.
            StateChanged += (s, e) => UpdateMaximizeRestoreIcon();
            UpdateMaximizeRestoreIcon();

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

        private void UpdateMaximizeRestoreIcon()
        {
            if (BtnMaximizeRestore == null) return;
            bool isMaximized = WindowState == WindowState.Maximized;
            // Segoe MDL2 Assets: E922 = maximize glyph, E923 = restore-down glyph
            BtnMaximizeRestore.Content = isMaximized ? "\uE923" : "\uE922";
            BtnMaximizeRestore.ToolTip = isMaximized ? "Restore Down" : "Maximize";
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void BtnMaximizeRestore_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

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