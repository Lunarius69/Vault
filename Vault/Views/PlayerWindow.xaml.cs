using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Vault.Database;
using Vault.Models;
using Vault.Services;

namespace Vault.Views
{
    public partial class PlayerWindow : Window
    {
        // ------------------------------------------------------------------ //
        //  Fields
        // ------------------------------------------------------------------ //
        private LibVLC? _libVlc;
        private MediaPlayer? _player;

        private readonly MediaItem _mediaItem;
        private readonly List<Episode> _playlist;
        private int _playlistIndex;
        private Episode CurrentEpisode => _playlist[_playlistIndex];

        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };

        private bool _isDragging = false;
        private bool _nextEpisodeShown = false;
        private bool _movedToNext = false;
        private bool _controlsVisible = true;

        private CancellationTokenSource _fpCts = new();

        private const double NextEpisodeTriggerSeconds = 90.0;

        // ------------------------------------------------------------------ //
        //  Constructor
        // ------------------------------------------------------------------ //
        public PlayerWindow(MediaItem mediaItem, Episode startEpisode, List<Episode> allEpisodes)
        {
            InitializeComponent();
            _mediaItem = mediaItem;
            _playlist = allEpisodes
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            _playlistIndex = _playlist.FindIndex(e => e.Id == startEpisode.Id);
            if (_playlistIndex < 0) _playlistIndex = 0;

            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        // ------------------------------------------------------------------ //
        //  Init
        // ------------------------------------------------------------------ //
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Core.Initialize();
            _libVlc = new LibVLC("--no-osd");
            _player = new MediaPlayer(_libVlc);
            VideoView.MediaPlayer = _player;

            _player.EndReached += OnEndReached;
            _player.Playing += (s, _) => Dispatcher.Invoke(() => BtnPlayPause.Content = "⏸");
            _player.Paused += (s, _) => Dispatcher.Invoke(() => BtnPlayPause.Content = "▶");

            _uiTimer.Tick += UiTimer_Tick;
            _hideTimer.Tick += HideTimer_Tick;
            _uiTimer.Start();
            _hideTimer.Start();

            await PlayCurrentAsync();
            _ = StartFingerprintingAsync();
        }

        // ------------------------------------------------------------------ //
        //  Playback
        // ------------------------------------------------------------------ //
        private async Task PlayCurrentAsync()
        {
            if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count) return;
            var ep = CurrentEpisode;

            if (string.IsNullOrEmpty(ep.FilePath) || !System.IO.File.Exists(ep.FilePath))
            {
                MessageBox.Show($"File not found for episode {ep.EpisodeNumber}.\nSet the media folder first.");
                Close();
                return;
            }

            // Check chapter markers
            if (ep.IntroEnd < 0 || ep.OutroStart < 0)
            {
                var (chapIntroEnd, chapOutroStart) =
                    await FingerprintService.ReadChapterMarkersAsync(ep.FilePath);
                if (chapIntroEnd > 0 && ep.IntroEnd < 0) { ep.IntroStart = 0; ep.IntroEnd = chapIntroEnd; }
                if (chapOutroStart > 0 && ep.OutroStart < 0) ep.OutroStart = chapOutroStart;
            }

            _nextEpisodeShown = false;
            _movedToNext = false;
            BtnNextEpisode.Visibility = Visibility.Collapsed;
            BtnSkipIntro.Visibility = Visibility.Collapsed;

            // Episode title
            TxtEpisodeTitle.Text = ep.Title != null
                ? $"S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}  —  {ep.Title}"
                : $"S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}";

            // Next episode label
            var nextEp = _playlistIndex + 1 < _playlist.Count ? _playlist[_playlistIndex + 1] : null;
            TxtNextEpisodeTitle.Text = nextEp != null
                ? $"S{nextEp.SeasonNumber:D2}E{nextEp.EpisodeNumber:D2}  {nextEp.Title}"
                : "";

            using var media = new LibVLCSharp.Shared.Media(_libVlc!, new Uri(ep.FilePath));
            _player!.Media = media;
            _player.Play();

            if (ep.ResumePositionSeconds > 0)
            {
                await Task.Delay(800);
                _player.SeekTo(TimeSpan.FromSeconds(ep.ResumePositionSeconds));
            }

            // Update progress bar highlights after a short delay so Length is available
            await Task.Delay(1500);
            Dispatcher.Invoke(UpdateHighlights);
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await SaveProgressAsync();
                if (_playlistIndex + 1 < _playlist.Count)
                {
                    _playlistIndex++;
                    await PlayCurrentAsync();
                }
                else Close();
            });
        }

        // ------------------------------------------------------------------ //
        //  UI Timer — updates seek bar, time label, skip buttons
        // ------------------------------------------------------------------ //
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            if (_player == null) return;
            long pos = _player.Time;
            long len = _player.Length;
            if (len <= 0) return;

            double pct = (double)pos / len;
            double maxW = SeekBarGrid.ActualWidth;

            if (!_isDragging && maxW > 0)
            {
                ProgressFill.Width = maxW * pct;
                double thumbLeft = maxW * pct - 7;
                SeekThumb.Margin = new Thickness(Math.Max(0, thumbLeft), 0, 0, 0);
            }

            var posTs = TimeSpan.FromMilliseconds(pos);
            var lenTs = TimeSpan.FromMilliseconds(len);
            TxtTime.Text = $"{posTs:h\\:mm\\:ss} / {lenTs:h\\:mm\\:ss}";

            double posSec = pos / 1000.0;
            double totalSec = len / 1000.0;
            UpdateSkipButtons(posSec, totalSec);
        }

        private void UpdateSkipButtons(double posSec, double totalSec)
        {
            var ep = CurrentEpisode;

            // Skip intro
            bool inIntro = ep.IntroStart >= 0 && ep.IntroEnd > ep.IntroStart &&
                           posSec >= ep.IntroStart && posSec < ep.IntroEnd;
            BtnSkipIntro.Visibility = inIntro ? Visibility.Visible : Visibility.Collapsed;

            // Next episode
            double remaining = totalSec - posSec;
            bool showNext = (ep.OutroStart > 0 && posSec >= ep.OutroStart) ||
                            (remaining <= NextEpisodeTriggerSeconds && remaining > 5);

            if (showNext && !_nextEpisodeShown && _playlistIndex + 1 < _playlist.Count)
            {
                _nextEpisodeShown = true;
                BtnNextEpisode.Visibility = Visibility.Visible;
            }
            else if (!showNext && _nextEpisodeShown)
            {
                _nextEpisodeShown = false;
                BtnNextEpisode.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateHighlights()
        {
            if (_player == null) return;
            var ep = CurrentEpisode;
            long len = _player.Length;
            if (len <= 0) return;

            double maxW = SeekBarGrid.ActualWidth;
            if (maxW <= 0) return;
            double totalSec = len / 1000.0;

            if (ep.IntroStart >= 0 && ep.IntroEnd > ep.IntroStart)
            {
                IntroHighlight.Margin = new Thickness((ep.IntroStart / totalSec) * maxW, 0, 0, 0);
                IntroHighlight.Width = ((ep.IntroEnd - ep.IntroStart) / totalSec) * maxW;
                IntroHighlight.Visibility = Visibility.Visible;
            }
            else IntroHighlight.Visibility = Visibility.Collapsed;

            if (ep.OutroStart > 0)
            {
                double left = (ep.OutroStart / totalSec) * maxW;
                OutroHighlight.Margin = new Thickness(left, 0, 0, 0);
                OutroHighlight.Width = Math.Max(0, maxW - left);
                OutroHighlight.Visibility = Visibility.Visible;
            }
            else OutroHighlight.Visibility = Visibility.Collapsed;
        }

        // ------------------------------------------------------------------ //
        //  Seek bar dragging
        // ------------------------------------------------------------------ //
        private void SeekBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            SeekBarGrid.CaptureMouse();
            SeekToMousePosition(e.GetPosition(SeekBarGrid).X);
        }

        private void SeekBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
                SeekToMousePosition(e.GetPosition(SeekBarGrid).X);
        }

        private void SeekBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                SeekToMousePosition(e.GetPosition(SeekBarGrid).X);
                _isDragging = false;
                SeekBarGrid.ReleaseMouseCapture();
            }
        }

        private void SeekToMousePosition(double x)
        {
            if (_player == null || _player.Length <= 0) return;
            double pct = Math.Clamp(x / SeekBarGrid.ActualWidth, 0, 1);
            double maxW = SeekBarGrid.ActualWidth;
            ProgressFill.Width = maxW * pct;
            SeekThumb.Margin = new Thickness(maxW * pct - 7, 0, 0, 0);
            _player.SeekTo(TimeSpan.FromMilliseconds(_player.Length * pct));
        }

        // ------------------------------------------------------------------ //
        //  Controls auto-hide
        // ------------------------------------------------------------------ //
        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_controlsVisible)
            {
                TopBar.Opacity = 1;
                BottomBar.Opacity = 1;
                Cursor = Cursors.Arrow;
                _controlsVisible = true;
            }
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            if (_player?.IsPlaying != true) return;
            TopBar.Opacity = 0;
            BottomBar.Opacity = 0;
            Cursor = Cursors.None;
            _controlsVisible = false;
        }

        // ------------------------------------------------------------------ //
        //  Button handlers
        // ------------------------------------------------------------------ //
        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
        private void BtnSkipBack_Click(object sender, RoutedEventArgs e)
        {
            if (_player != null)
                _player.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time - 10000)));
        }
        private void BtnSkipForward_Click(object sender, RoutedEventArgs e)
        {
            if (_player != null)
                _player.SeekTo(TimeSpan.FromMilliseconds(Math.Min(_player.Length, _player.Time + 10000)));
        }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
        }

        private void BtnSkipIntro_Click(object sender, MouseButtonEventArgs e)
        {
            if (_player == null) return;
            _player.SeekTo(TimeSpan.FromSeconds(CurrentEpisode.IntroEnd));
            BtnSkipIntro.Visibility = Visibility.Collapsed;
        }

        private async void BtnNextEpisode_Click(object sender, MouseButtonEventArgs e)
        {
            if (_movedToNext) return;
            _movedToNext = true;
            BtnNextEpisode.Visibility = Visibility.Collapsed;
            await SaveProgressAsync();
            _playlistIndex++;
            await PlayCurrentAsync();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_player != null) _player.Volume = (int)e.NewValue;
        }

        private void ClickOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) BtnFullscreen_Click(this, null!);
            else TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause();
            else _player.Play();
        }

        // ------------------------------------------------------------------ //
        //  Keyboard
        // ------------------------------------------------------------------ //
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Space: TogglePlayPause(); break;
                case Key.Left:
                    if (_player != null)
                        _player.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time - 10000)));
                    break;
                case Key.Right:
                    if (_player != null)
                        _player.SeekTo(TimeSpan.FromMilliseconds(Math.Min(_player.Length, _player.Time + 10000)));
                    break;
                case Key.Up: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); break;
                case Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); break;
                case Key.F: case Key.F11: BtnFullscreen_Click(this, null!); break;
                case Key.Escape: Close(); break;
            }
        }

        // ------------------------------------------------------------------ //
        //  DB — save progress
        // ------------------------------------------------------------------ //
        private async Task SaveProgressAsync()
        {
            var ep = CurrentEpisode;
            if (_player == null || ep.Id < 0) return;

            long posMs = _player.Time;
            if (posMs <= 0) return;

            long posSec = posMs / 1000;
            long runtimeSec = ep.RuntimeMinutes > 0 ? ep.RuntimeMinutes * 60L : 1440;
            bool finished = posSec >= runtimeSec * 0.85;

            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(ep.Id);
            if (dbEp != null)
            {
                if (finished)
                {
                    dbEp.IsWatched = ep.IsWatched = true;
                    dbEp.WatchedDate = ep.WatchedDate = DateTime.Now;
                    dbEp.ResumePositionSeconds = ep.ResumePositionSeconds = 0;
                }
                else
                {
                    dbEp.ResumePositionSeconds = ep.ResumePositionSeconds = posSec;
                }

                var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);
                if (dbItem != null && finished)
                {
                    dbItem.WatchedEpisodes = await db.Episodes
                        .CountAsync(e => e.MediaItemId == _mediaItem.Id && e.IsWatched);
                    dbItem.CurrentEpisode = ep.EpisodeNumber;
                    dbItem.CurrentSeason = ep.SeasonNumber;
                    dbItem.WatchStatus = dbItem.TotalEpisodes > 0 &&
                        dbItem.WatchedEpisodes >= dbItem.TotalEpisodes
                        ? "Completed" : "Watching";
                }

                await db.SaveChangesAsync();
            }
        }

        // ------------------------------------------------------------------ //
        //  Fingerprinting
        // ------------------------------------------------------------------ //
        private async Task StartFingerprintingAsync()
        {
            _fpCts = new CancellationTokenSource();
            var svc = new FingerprintService();
            await svc.ProcessShowAsync(_mediaItem.Id, _playlist, _fpCts.Token);
            Dispatcher.Invoke(UpdateHighlights);
        }

        // ------------------------------------------------------------------ //
        //  Close
        // ------------------------------------------------------------------ //
        private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _fpCts.Cancel();
            _uiTimer.Stop();
            _hideTimer.Stop();
            await SaveProgressAsync();
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();
        }
    }
}
