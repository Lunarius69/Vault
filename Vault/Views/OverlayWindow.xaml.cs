using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Vault.Models;

namespace Vault.Views
{
    public partial class OverlayWindow : Window
    {
        // ------------------------------------------------------------------ //
        //  Win32
        // ------------------------------------------------------------------ //
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        // ------------------------------------------------------------------ //
        //  Events fired back to PlayerWindow
        // ------------------------------------------------------------------ //
        public event Action? PlayPauseRequested;
        public event Action<double>? SeekRequested;
        public event Action? SkipBackRequested;
        public event Action? SkipForwardRequested;
        public event Action? SkipIntroRequested;
        public event Action? NextEpisodeRequested;      // overlay button near end
        public event Action? NextEpisodeButtonRequested; // ⏭ button in controls
        public event Action? PrevEpisodeRequested;      // ⏮ button in controls
        public event Action? CloseRequested;
        public event Action<int>? VolumeChanged;
        public event Action? MouseActivity;
        public event Action? FullscreenRequested;

        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //
        private readonly PlayerWindow _owner;
        private bool _isDragging = false;

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //
        public OverlayWindow(PlayerWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Owner = owner;

            // Ensure the overlay captures mouse input (not click-through)
            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                style &= ~WS_EX_TRANSPARENT;
                SetWindowLong(hwnd, GWL_EXSTYLE, style);
            };
        }

        // ------------------------------------------------------------------ //
        //  Public API called from PlayerWindow
        // ------------------------------------------------------------------ //

        public void UpdateProgress(double pct, TimeSpan pos, TimeSpan len, bool isPlaying)
        {
            double maxW = SeekBarGrid.ActualWidth;
            if (!_isDragging && maxW > 0)
            {
                ProgressFill.Width = maxW * pct;
                SeekThumb.Margin = new Thickness(Math.Max(0, maxW * pct - 7), 0, 0, 0);
            }
            TxtTime.Text = $"{pos:h\\:mm\\:ss} / {len:h\\:mm\\:ss}";
            BtnPlayPause.Content = isPlaying ? "⏸" : "▶";
        }

        public void UpdateHighlights(double introStart, double introEnd,
                                      double outroStart, double totalSec)
        {
            double maxW = SeekBarGrid.ActualWidth;
            if (maxW <= 0 || totalSec <= 0) return;

            if (introStart >= 0 && introEnd > introStart)
            {
                IntroHighlight.Margin = new Thickness((introStart / totalSec) * maxW, 0, 0, 0);
                IntroHighlight.Width = ((introEnd - introStart) / totalSec) * maxW;
                IntroHighlight.Visibility = Visibility.Visible;
            }
            else IntroHighlight.Visibility = Visibility.Collapsed;

            if (outroStart > 0)
            {
                double left = (outroStart / totalSec) * maxW;
                OutroHighlight.Margin = new Thickness(left, 0, 0, 0);
                OutroHighlight.Width = Math.Max(0, maxW - left);
                OutroHighlight.Visibility = Visibility.Visible;
            }
            else OutroHighlight.Visibility = Visibility.Collapsed;
        }

        public void SetEpisodeInfo(int season, int ep, string title, Episode? nextEp)
        {
            TxtTitle.Text = $"S{season:D2}E{ep:D2}  —  {title}";
            TxtNextTitle.Text = nextEp != null
                ? $"S{nextEp.SeasonNumber:D2}E{nextEp.EpisodeNumber:D2}  {nextEp.Title}"
                : "";
        }

        public void ShowControls(bool visible)
        {
            TopBar.Opacity = visible ? 1 : 0;
            BottomBar.Opacity = visible ? 1 : 0;
            Cursor = visible ? Cursors.Arrow : Cursors.None;
        }

        public void ShowSkipIntro(bool visible)
            => BtnSkipIntro.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        public void ShowNextEpisode(bool visible)
            => BtnNextEpisode.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        public void ChangeVolume(int delta)
            => VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);

        // ------------------------------------------------------------------ //
        //  Mouse / keyboard input
        // ------------------------------------------------------------------ //

        private void Window_MouseMove(object sender, MouseEventArgs e)
            => MouseActivity?.Invoke();

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            MouseActivity?.Invoke();
            if (e.ClickCount == 2) FullscreenRequested?.Invoke();
            else PlayPauseRequested?.Invoke();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
            => _owner.HandleKey(e.Key);

        // ------------------------------------------------------------------ //
        //  Seek bar
        // ------------------------------------------------------------------ //

        private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _isDragging = true;
            SeekBarGrid.CaptureMouse();
            FireSeek(e.GetPosition(SeekBarGrid).X);
        }

        private void SeekBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging) FireSeek(e.GetPosition(SeekBarGrid).X);
        }

        private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            e.Handled = true;
            FireSeek(e.GetPosition(SeekBarGrid).X);
            _isDragging = false;
            SeekBarGrid.ReleaseMouseCapture();
        }

        private void FireSeek(double x)
        {
            double pct = Math.Clamp(x / SeekBarGrid.ActualWidth, 0, 1);
            double maxW = SeekBarGrid.ActualWidth;
            ProgressFill.Width = maxW * pct;
            SeekThumb.Margin = new Thickness(maxW * pct - 7, 0, 0, 0);
            SeekRequested?.Invoke(pct);
        }

        // ------------------------------------------------------------------ //
        //  Button handlers
        // ------------------------------------------------------------------ //

        private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
            => PlayPauseRequested?.Invoke();

        private void BtnSkipBack_Click(object sender, RoutedEventArgs e)
            => SkipBackRequested?.Invoke();

        private void BtnSkipForward_Click(object sender, RoutedEventArgs e)
            => SkipForwardRequested?.Invoke();

        private void BtnPrevEpisode_Click(object sender, RoutedEventArgs e)
            => PrevEpisodeRequested?.Invoke();

        private void BtnNextEpisodeBtn_Click(object sender, RoutedEventArgs e)
            => NextEpisodeButtonRequested?.Invoke();

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => CloseRequested?.Invoke();

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
            => FullscreenRequested?.Invoke();

        private void BtnSkipIntro_Click(object sender, MouseButtonEventArgs e)
            => SkipIntroRequested?.Invoke();

        private void BtnNextEpisode_Click(object sender, MouseButtonEventArgs e)
            => NextEpisodeRequested?.Invoke();

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => VolumeChanged?.Invoke((int)e.NewValue);
    }
}