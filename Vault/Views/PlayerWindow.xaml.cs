using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
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

        private OverlayWindow? _overlay;

        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(4) };

        private bool _nextShown = false;
        private bool _movedToNext = false;

        private CancellationTokenSource _fpCts = new();
        private const double NextTriggerSec = 90.0;

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
            LocationChanged += (s, e) => PositionOverlay();
            SizeChanged += (s, e) => PositionOverlay();
            StateChanged += (s, e) => PositionOverlay();
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

            // Give VideoView time to initialize its render surface
            await Task.Delay(200);

            _player.EndReached += OnEndReached;

            // Create overlay — hidden until video starts
            _overlay = new OverlayWindow(this);
            _overlay.Opacity = 0;
            _overlay.Show();
            await Task.Delay(100);
            PositionOverlay();

            // Wire overlay events
            _overlay.PlayPauseRequested += () => TogglePlayPause();
            _overlay.SeekRequested += pct => SeekToPct(pct);
            _overlay.SkipBackRequested += () => { try { _player?.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time - 10000))); } catch { } };
            _overlay.SkipForwardRequested += () => { try { _player?.SeekTo(TimeSpan.FromMilliseconds(Math.Min(_player.Length, _player.Time + 10000))); } catch { } };
            _overlay.SkipIntroRequested += () => { try { if (_player != null) _player.SeekTo(TimeSpan.FromSeconds(CurrentEpisode.IntroEnd)); } catch { } };
            _overlay.NextEpisodeRequested += async () => await AdvanceEpisodeAsync();
            _overlay.NextEpisodeButtonRequested += async () => await AdvanceEpisodeAsync();
            _overlay.PrevEpisodeRequested += async () => await PrevEpisodeAsync();
            _overlay.CloseRequested += () => Close();
            _overlay.VolumeChanged += v => { try { if (_player != null) _player.Volume = v; } catch { } };
            _overlay.MouseActivity += OnMouseActivity;
            _overlay.FullscreenRequested += ToggleFullscreen;

            _uiTimer.Tick += UiTimer_Tick;
            _hideTimer.Tick += HideTimer_Tick;
            _uiTimer.Start();
            _hideTimer.Start();

            await PlayCurrentAsync();
            _ = StartFingerprintingAsync();
        }

        private void PositionOverlay()
        {
            if (_overlay == null) return;
            _overlay.Left = Left;
            _overlay.Top = Top;
            _overlay.Width = ActualWidth;
            _overlay.Height = ActualHeight;
        }

        // ------------------------------------------------------------------ //
        //  Playback
        // ------------------------------------------------------------------ //
        private async Task PlayCurrentAsync()
        {
            if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count) return;
            var ep = CurrentEpisode;

            // Resolve file path — DB first, then folder scan
            if (string.IsNullOrEmpty(ep.FilePath) || !File.Exists(ep.FilePath))
            {
                using var db = new VaultContext();
                var dbEp = await db.Episodes.FindAsync(ep.Id);
                if (dbEp != null && !string.IsNullOrEmpty(dbEp.FilePath) &&
                    File.Exists(dbEp.FilePath))
                {
                    ep.FilePath = dbEp.FilePath;
                }
                else
                {
                    ep.FilePath = FindEpisodeFile(ep);
                    if (!string.IsNullOrEmpty(ep.FilePath) && dbEp != null)
                    {
                        dbEp.FilePath = ep.FilePath;
                        await db.SaveChangesAsync();
                    }
                }
            }

            if (string.IsNullOrEmpty(ep.FilePath) || !File.Exists(ep.FilePath))
            {
                MessageBox.Show(
                    $"File not found for S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}.\nSet the media folder first.");
                Close();
                return;
            }

            // Check chapter markers for instant intro/outro
            if (ep.IntroEnd < 0 || ep.OutroStart < 0)
            {
                var (ci, co) = await FingerprintService.ReadChapterMarkersAsync(ep.FilePath);
                if (ci > 0 && ep.IntroEnd < 0) { ep.IntroStart = 0; ep.IntroEnd = ci; }
                if (co > 0 && ep.OutroStart < 0) ep.OutroStart = co;
            }

            _nextShown = false;
            _movedToNext = false;
            _overlay?.ShowNextEpisode(false);
            _overlay?.ShowSkipIntro(false);

            var nextEp = _playlistIndex + 1 < _playlist.Count
                ? _playlist[_playlistIndex + 1] : null;
            _overlay?.SetEpisodeInfo(ep.SeasonNumber, ep.EpisodeNumber, ep.Title ?? "", nextEp);

            try
            {
                using var media = new LibVLCSharp.Shared.Media(_libVlc!, new Uri(ep.FilePath));
                _player!.Media = media;
                _player.Play();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to play file: {ex.Message}");
                return;
            }

            // Show overlay now that video is playing
            await Task.Delay(500);
            if (_overlay != null) _overlay.Opacity = 1;

            if (ep.ResumePositionSeconds > 0)
            {
                await Task.Delay(300);
                try { _player!.SeekTo(TimeSpan.FromSeconds(ep.ResumePositionSeconds)); }
                catch { }
            }

            // Wait for Length to be available then update highlights
            await Task.Delay(1000);
            Dispatcher.Invoke(UpdateHighlights);
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await MarkEpisodeWatchedDirectlyAsync(CurrentEpisode);
                if (_playlistIndex + 1 < _playlist.Count)
                {
                    _playlistIndex++;
                    await PlayCurrentAsync();
                }
                else Close();
            });
        }

        private async Task MarkEpisodeWatchedDirectlyAsync(Episode ep)
        {
            if (ep.Id < 0) return;
            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(ep.Id);
            if (dbEp != null)
            {
                dbEp.IsWatched = ep.IsWatched = true;
                dbEp.WatchedDate = DateTime.Now;
                dbEp.ResumePositionSeconds = ep.ResumePositionSeconds = 0;

                var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);
                if (dbItem != null)
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
        //  File resolution
        // ------------------------------------------------------------------ //
        private string? FindEpisodeFile(Episode episode)
        {
            if (string.IsNullOrEmpty(_mediaItem.FolderPath)) return null;

            var searchPaths = new List<string> { _mediaItem.FolderPath };
            string? parent = Path.GetDirectoryName(_mediaItem.FolderPath);
            if (!string.IsNullOrEmpty(parent) && parent != _mediaItem.FolderPath)
                searchPaths.Add(parent);

            string[] videoExts = { ".mkv", ".mp4", ".avi", ".m4v", ".mov" };
            var allFiles = new List<string>();
            foreach (string searchPath in searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                try
                {
                    allFiles.AddRange(
                        Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories)
                        .Where(f => videoExts.Contains(Path.GetExtension(f).ToLower())));
                }
                catch { }
            }

            if (allFiles.Count == 0) return null;

            var episodesInSeason = _playlist
                .Where(e => e.SeasonNumber == episode.SeasonNumber)
                .OrderBy(e => e.EpisodeNumber)
                .ToList();
            int localEpNumber = episodesInSeason.FindIndex(e => e.Id == episode.Id) + 1;
            if (localEpNumber == 0) localEpNumber = 1;

            string p1 = $"S{episode.SeasonNumber:D2}E{localEpNumber:D2}";
            var m1 = allFiles.FirstOrDefault(f => f.Contains(p1, StringComparison.OrdinalIgnoreCase));
            if (m1 != null) return m1;

            string p2 = $"S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}";
            var m2 = allFiles.FirstOrDefault(f => f.Contains(p2, StringComparison.OrdinalIgnoreCase));
            if (m2 != null) return m2;

            string p3 = $"{episode.SeasonNumber}x{localEpNumber:D2}";
            var m3 = allFiles.FirstOrDefault(f => f.Contains(p3, StringComparison.OrdinalIgnoreCase));
            if (m3 != null) return m3;

            if (!string.IsNullOrEmpty(episode.Title) && episode.Title.Length > 5)
            {
                string titleKey = episode.Title.Split(' ').First();
                var m4 = allFiles.FirstOrDefault(f =>
                    f.Contains(titleKey, StringComparison.OrdinalIgnoreCase));
                if (m4 != null) return m4;
            }

            return null;
        }

        // ------------------------------------------------------------------ //
        //  UI Timer
        // ------------------------------------------------------------------ //
        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_player == null) return;
                long pos = _player.Time;
                long len = _player.Length;
                if (len <= 0) return;

                double pct = (double)pos / len;
                double posSec = pos / 1000.0;
                double totSec = len / 1000.0;

                _overlay?.UpdateProgress(pct,
                    TimeSpan.FromMilliseconds(pos),
                    TimeSpan.FromMilliseconds(len),
                    _player.IsPlaying);

                var ep = CurrentEpisode;
                bool inIntro = ep.IntroStart >= 0 && ep.IntroEnd > ep.IntroStart &&
                               posSec >= ep.IntroStart && posSec < ep.IntroEnd;
                _overlay?.ShowSkipIntro(inIntro);

                double remaining = totSec - posSec;
                bool showNext = (ep.OutroStart > 0 && posSec >= ep.OutroStart) ||
                                (remaining <= NextTriggerSec && remaining > 5);

                if (showNext && !_nextShown && _playlistIndex + 1 < _playlist.Count)
                {
                    _nextShown = true;
                    _overlay?.ShowNextEpisode(true);
                }
                else if (!showNext && _nextShown)
                {
                    _nextShown = false;
                    _overlay?.ShowNextEpisode(false);
                }
            }
            catch { }
        }

        private void UpdateHighlights()
        {
            try
            {
                if (_player == null) return;
                long len = _player.Length;
                if (len <= 0) return;
                var ep = CurrentEpisode;
                _overlay?.UpdateHighlights(ep.IntroStart, ep.IntroEnd, ep.OutroStart, len / 1000.0);
            }
            catch { }
        }

        // ------------------------------------------------------------------ //
        //  Controls visibility
        // ------------------------------------------------------------------ //
        private void OnMouseActivity()
        {
            _overlay?.ShowControls(true);
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void HideTimer_Tick(object? sender, EventArgs e)
        {
            _hideTimer.Stop();
            // Uncomment to enable auto-hide once mouse tracking is confirmed working:
            // if (_player?.IsPlaying != true) return;
            // _overlay?.ShowControls(false);
        }

        // ------------------------------------------------------------------ //
        //  Actions
        // ------------------------------------------------------------------ //
        private void TogglePlayPause()
        {
            try
            {
                if (_player == null) return;
                if (_player.IsPlaying) _player.Pause();
                else _player.Play();
            }
            catch { }
        }

        private void SeekToPct(double pct)
        {
            try
            {
                if (_player == null || _player.Length <= 0) return;
                _player.SeekTo(TimeSpan.FromMilliseconds(_player.Length * pct));
            }
            catch { }
        }

        private void ToggleFullscreen()
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
            PositionOverlay();
        }

        private async Task AdvanceEpisodeAsync()
        {
            if (_movedToNext) return;
            _movedToNext = true;
            _overlay?.ShowNextEpisode(false);
            await SaveProgressAsync();
            if (_playlistIndex + 1 >= _playlist.Count) { Close(); return; }
            _playlistIndex++;
            await PlayCurrentAsync();
        }

        private async Task PrevEpisodeAsync()
        {
            if (_playlistIndex <= 0) return;
            await SaveProgressAsync();
            _playlistIndex--;
            await PlayCurrentAsync();
        }

        // ------------------------------------------------------------------ //
        //  Keyboard — volume is mouse only, arrows seek, space pauses
        // ------------------------------------------------------------------ //
        internal async void HandleKey(Key key)
        {
            switch (key)
            {
                case Key.Space:
                    TogglePlayPause();
                    break;
                case Key.Left:
                    try { _player?.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time - 10000))); } catch { }
                    break;
                case Key.Right:
                    try { _player?.SeekTo(TimeSpan.FromMilliseconds(Math.Min(_player.Length, _player.Time + 10000))); } catch { }
                    break;
                case Key.N:
                    await AdvanceEpisodeAsync();
                    break;
                case Key.P:
                    await PrevEpisodeAsync();
                    break;
                case Key.F:
                case Key.F11:
                    ToggleFullscreen();
                    break;
                case Key.Escape:
                    Close();
                    break;
            }
        }

        // ------------------------------------------------------------------ //
        //  DB — save progress
        // ------------------------------------------------------------------ //
        private async Task SaveProgressAsync()
        {
            var ep = CurrentEpisode;
            if (_player == null || ep.Id < 0) return;

            long posMs = 0;
            try { posMs = _player.Time; } catch { return; }
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
                    dbEp.WatchedDate = DateTime.Now;
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
            _overlay?.Close();
            await SaveProgressAsync();
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();
        }
    }
}