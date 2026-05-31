using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
        public event Action? AddSubtitleRequested;
        public event Action<int, string, bool>? AudioTrackChangeRequested;

        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //
        private readonly PlayerWindow _owner;
        private bool _isDragging = false;

        // Audio track state
        private (int Id, string Name)[] _audioTracks = Array.Empty<(int, string)>();
        private int _currentAudioId = -1;
        private string _preferredAudioLang = "";

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

        public void SetEpisodeInfo(int season, int ep, string title, Episode? nextEp, bool isMovie = false)
        {
            TxtTitle.Text = isMovie ? title : $"S{season:D2}E{ep:D2}  —  {title}";
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

        // ------------------------------------------------------------------ //
        //  Subtitles
        // ------------------------------------------------------------------ //

        public void SetAudioTracks((int Id, string Name)[] tracks, int currentId, string preferredLang)
        {
            _audioTracks = tracks;
            _currentAudioId = currentId;
            _preferredAudioLang = preferredLang;
        }

        private void BtnSubtitles_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            AddSubtitleRequested?.Invoke();
        }
        private void BtnAudioTrack_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (_audioTracks.Length == 0) return;

            var popup = new Popup
            {
                PlacementTarget = sender as Button,
                Placement = PlacementMode.Top,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 10, 10, 20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                MinWidth = 240
            };

            var outerStack = new StackPanel();

            bool hasRealTracks = _audioTracks.Length > 0;

            if (!hasRealTracks)
            {
                outerStack.Children.Add(new TextBlock
                {
                    Text = "No audio tracks available",
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
                    Margin = new Thickness(14, 10, 14, 10)
                });
            }
            else
            {
                foreach (var track in _audioTracks)
                {
                    bool isCurrent = track.Id == _currentAudioId;
                    string displayName = track.Id == -1 ? "Disable audio" : track.Name;

                    var btn = new Button
                    {
                        Background = isCurrent
                            ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255))
                            : Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(12, 7, 16, 7),
                        HorizontalContentAlignment = HorizontalAlignment.Left
                    };

                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(new TextBlock
                    {
                        Text = isCurrent ? "●" : "○",
                        Foreground = isCurrent
                            ? Brushes.White
                            : new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                        FontSize = 9,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = displayName,
                        Foreground = isCurrent
                            ? Brushes.White
                            : new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    btn.Content = row;

                    var tmpl = new ControlTemplate(typeof(Button));
                    var fac = new FrameworkElementFactory(typeof(Border));
                    fac.SetBinding(Border.BackgroundProperty,
                        new Binding("Background")
                        {
                            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                        });
                    fac.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                    fac.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                    tmpl.VisualTree = fac;
                    btn.Template = tmpl;

                    int capturedId = track.Id;
                    string capturedName = track.Name ?? "";
                    bool capturedIsCurrent = isCurrent;

                    btn.Click += (s, args) =>
                    {
                        popup.IsOpen = false;
                        AudioTrackChangeRequested?.Invoke(capturedId, capturedName, true);
                    };
                    btn.MouseEnter += (s, args) =>
                        btn.Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
                    btn.MouseLeave += (s, args) =>
                        btn.Background = capturedIsCurrent
                            ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255))
                            : Brushes.Transparent;

                    outerStack.Children.Add(btn);
                }

            }

            border.Child = outerStack;
            popup.Child = border;
            popup.IsOpen = true;
        }
    }
}