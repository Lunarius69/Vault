using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vault.Views
{
    public partial class PlayerOverlay : UserControl
    {
        public event Action? PlayPauseRequested;
        public event Action<double>? SeekRequested;
        public event Action? SkipBackRequested;
        public event Action? SkipForwardRequested;
        public event Action? SkipIntroRequested;
        public event Action? NextEpisodeRequested;
        public event Action? NextEpisodeButtonRequested;
        public event Action? PrevEpisodeRequested;
        public event Action? CloseRequested;
        public event Action<int>? VolumeChanged;
        public event Action? MouseActivity;
        public event Action? FullscreenRequested;
        public event Action? AddSubtitleRequested;
        public event Action<int, string, bool>? AudioTrackChangeRequested;

        private bool _isDragging = false;

        // Audio track state (compatibility with the old OverlayWindow)
        private (int Id, string Name)[] _audioTracks = Array.Empty<(int, string)>();
        private int _currentAudioId = -1;
        private string _preferredAudioLang = "";

        public PlayerOverlay()
        {
            InitializeComponent();

            // Basic input hookups
            SeekBarGrid.MouseLeftButtonDown += SeekBar_MouseLeftButtonDown;
            SeekBarGrid.MouseMove += SeekBar_MouseMove;
            SeekBarGrid.MouseLeftButtonUp += SeekBar_MouseLeftButtonUp;

            BtnPlayPause.Click += (s, e) => PlayPauseRequested?.Invoke();
            BtnSkipBack.Click += (s, e) => SkipBackRequested?.Invoke();
            BtnSkipForward.Click += (s, e) => SkipForwardRequested?.Invoke();
            BtnPrevEpisode.Click += (s, e) => PrevEpisodeRequested?.Invoke();
            BtnNextEpisodeBtn.Click += (s, e) => NextEpisodeButtonRequested?.Invoke();

            this.MouseMove += (s, e) => MouseActivity?.Invoke();
            this.MouseLeftButtonDown += (s, e) => {
                if (e.ClickCount == 2) FullscreenRequested?.Invoke(); else PlayPauseRequested?.Invoke();
            };
        }

        // Compatibility helpers for code that treated the overlay as a Window
        public void Show() => this.Visibility = Visibility.Visible;
        public void Hide() => this.Visibility = Visibility.Collapsed;
        public void Close() => Hide();

        // Simple Left/Top positioning properties — apply a TranslateTransform so
        // callers that previously set window.Left/Top still move the overlay.
        private double _left, _top;
        public double Left
        {
            get => _left;
            set { _left = value; ApplyTranslate(); }
        }
        public double Top
        {
            get => _top;
            set { _top = value; ApplyTranslate(); }
        }

        private void ApplyTranslate()
        {
            try
            {
                var tt = this.RenderTransform as TranslateTransform;
                if (tt == null)
                {
                    tt = new TranslateTransform();
                    this.RenderTransform = tt;
                }
                tt.X = _left;
                tt.Y = _top;
            }
            catch { }
        }

        // Mirror of OverlayWindow.SetAudioTracks
        public void SetAudioTracks((int Id, string Name)[] tracks, int currentId, string preferredLang)
        {
            // store internally if needed — overlay exposes AudioTrackChangeRequested event
            // no-op for now to satisfy callers
            _audioTracks = tracks;
            _currentAudioId = currentId;
            _preferredAudioLang = preferredLang ?? "";
        }

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

        public void UpdateHighlights(double introStart, double introEnd, double outroStart, double totalSec)
        {
            // Not implemented in simplified overlay; keep signature for compatibility
        }

        public void SetEpisodeInfo(int season, int ep, string title, object? nextEp, bool isMovie = false)
        {
            TxtTitle.Text = isMovie ? title : $"S{season:D2}E{ep:D2}  —  {title}";
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
            => VolumeChanged?.Invoke(delta);

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
    }
}
