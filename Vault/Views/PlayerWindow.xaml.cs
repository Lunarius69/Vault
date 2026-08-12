using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private bool _closing = false;

        private CancellationTokenSource _fpCts = new();

        // Watch time tracking — records when the current episode started playing
        private DateTime? _episodePlayStartTime;

        // Minimize/restore fix — WPF can restore to Normal when Maximized+None was minimized
        private WindowState _stateBeforeMinimize = WindowState.Maximized;
        private bool _wasMinimized = false;
        private bool _overlayHiddenForMinimize = false;

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
            StateChanged += OnStateChanged;
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
            _overlay.AddSubtitleRequested += OnAddSubtitleRequested;
            _overlay.AudioTrackChangeRequested += OnAudioTrackChangeRequested;

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

        private void OnStateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                _wasMinimized = true;
                if (_overlay != null && _overlay.IsVisible)
                {
                    _overlayHiddenForMinimize = true;
                    _overlay.Hide();
                }
                return;
            }

            if (_wasMinimized)
            {
                _wasMinimized = false;
                // WPF bug: restoring a WindowStyle.None+Maximized window can land in Normal state
                if (_stateBeforeMinimize == WindowState.Maximized && WindowState != WindowState.Maximized)
                {
                    WindowStyle = WindowStyle.None;
                    WindowState = WindowState.Maximized;
                    return; // StateChanged fires again; overlay is restored in that next call
                }
                // WPF correctly restored to Maximized — ensure WindowStyle stayed None
                if (_stateBeforeMinimize == WindowState.Maximized)
                    WindowStyle = WindowStyle.None;
            }
            else
            {
                _stateBeforeMinimize = WindowState;
            }

            // Restore overlay after the window is in its final state
            if (_overlayHiddenForMinimize)
            {
                _overlayHiddenForMinimize = false;
                _overlay?.Show();
                OnMouseActivity(); // show controls and restart the auto-hide timer
            }

            // Defer until after layout pass so ActualWidth/Height/Left/Top reflect
            // the final Maximized state rather than the mid-transition snapshot.
            Dispatcher.InvokeAsync(PositionOverlay, DispatcherPriority.Background);
        }

        // ------------------------------------------------------------------ //
        //  Playback
        // ------------------------------------------------------------------ //
        private async Task EnsureWatchingStatusAsync()
        {
            if (_mediaItem.WatchStatus is "Watching" or "Completed") return;
            using var db = new VaultContext();
            var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);
            if (dbItem == null) return;
            dbItem.WatchStatus = _mediaItem.WatchStatus = "Watching";
            await db.SaveChangesAsync();
        }

        // FIX: mark which episode is "current" the moment it starts playing,
        // not only once it's finished. This is what makes the library tile
        // show the episode you're actually on while you're mid-episode.
        private async Task SetCurrentEpisodeAsync(Episode ep)
        {
            if (ep.Id < 0) return; // movies don't use CurrentSeason/CurrentEpisode
            try
            {
                using var db = new VaultContext();
                var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);
                if (dbItem == null) return;
                dbItem.CurrentSeason = ep.SeasonNumber;
                dbItem.CurrentEpisode = ep.EpisodeNumber;
                _mediaItem.CurrentSeason = ep.SeasonNumber;
                _mediaItem.CurrentEpisode = ep.EpisodeNumber;
                await db.SaveChangesAsync();
            }
            catch { }
        }

        private async Task PlayCurrentAsync()
        {
            if (_playlistIndex < 0 || _playlistIndex >= _playlist.Count) return;
            var ep = CurrentEpisode;
            _ = EnsureWatchingStatusAsync();
            _ = SetCurrentEpisodeAsync(ep);

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
            _overlay?.SetEpisodeInfo(ep.SeasonNumber, ep.EpisodeNumber, ep.Title ?? "", nextEp, ep.Id < 0);

            try
            {
                using var media = new LibVLCSharp.Shared.Media(_libVlc!, new Uri(ep.FilePath));
                _player!.Media = media;
                _player.Play();

                // Start tracking watch time for this episode
                _episodePlayStartTime = DateTime.Now;
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

            // Wait for Length to be available then update highlights + track menus
            await Task.Delay(1000);
            Dispatcher.Invoke(UpdateHighlights);
            _ = AutoEnableSubtitlesAsync();
            _ = PopulateAudioTracksAsync();

            // Persist movie runtime so the tile progress bar can calculate percentage
            if (!_closing && ep.Id < 0 && _mediaItem.RuntimeMinutes == 0)
            {
                long lenMs = 0;
                try { lenMs = _player?.Length ?? 0; } catch { }
                if (lenMs > 0)
                {
                    int runtimeMin = (int)(lenMs / 60000);
                    try
                    {
                        using var db = new VaultContext();
                        var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);
                        if (dbItem != null)
                        {
                            dbItem.RuntimeMinutes = runtimeMin;
                            _mediaItem.RuntimeMinutes = runtimeMin;
                            await db.SaveChangesAsync();
                        }
                    }
                    catch { }
                }
            }
        }

        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                await AccumulateWatchTimeAsync();
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
            // Movie — mark MediaItem as Completed and clear resume position
            if (ep.Id < 0)
            {
                using var movieDb = new VaultContext();
                var movieItem = await movieDb.MediaItems.FindAsync(_mediaItem.Id);
                if (movieItem != null)
                {
                    movieItem.WatchStatus = _mediaItem.WatchStatus = "Completed";
                    movieItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds = 0;
                    await movieDb.SaveChangesAsync();
                }
                return;
            }
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

                    // FIX: clear the mirrored mid-episode resume position now that
                    // this episode is fully finished, so the tile bar doesn't show
                    // a stale "80% through" line for an episode you already finished.
                    dbItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds = 0;
                }
                await db.SaveChangesAsync();
            }
        }

        // ------------------------------------------------------------------ //
        //  Watch time accumulation
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Calculates how many minutes have elapsed since playback started for
        /// the current episode and adds them to MediaItem.WatchTimeMinutes in the DB.
        /// Call this before every episode advance and on window close.
        /// </summary>
        private async Task AccumulateWatchTimeAsync()
        {
            if (_episodePlayStartTime == null) return;

            long elapsedMinutes = (long)(DateTime.Now - _episodePlayStartTime.Value).TotalMinutes;
            _episodePlayStartTime = null; // reset so we don't double-count

            if (elapsedMinutes <= 0) return;

            try
            {
                using var db = new VaultContext();
                var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);
                if (dbItem != null)
                {
                    dbItem.WatchTimeMinutes += elapsedMinutes;
                    _mediaItem.WatchTimeMinutes = dbItem.WatchTimeMinutes;
                    await db.SaveChangesAsync();
                }
            }
            catch { }
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
                // Fallback trigger when fingerprinting hasn't detected OutroStart yet.
                // Anime has long outro sequences (ED + omake), live-action TV has short credits,
                // movies shorter still. Fingerprint-detected OutroStart always takes priority.
                double fallback = _mediaItem.MediaType is "Anime" or "AnimeMovie" ? 180
                                : ep.Id < 0 ? 90   // movie
                                : 120;             // TV show / animated series
                bool showNext = (ep.OutroStart > 0 && posSec >= ep.OutroStart) ||
                                (remaining <= fallback && remaining > 5);

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
                if (_player == null || _player.Media == null) return;
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
            if (_player?.IsPlaying != true) return;
            _overlay?.ShowControls(false);
        }

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

        // FIX: WindowState/WindowStyle changes don't update ActualWidth/Height/
        // Left/Top synchronously — the layout pass happens after this method
        // returns. Calling PositionOverlay() immediately positioned the overlay
        // using the OLD (pre-toggle) window size, so switching from windowed
        // back to fullscreen left the overlay stuck at its smaller windowed
        // size/position, floating over part of the fullscreen video. Deferring
        // with the same pattern already used for minimize/restore fixes it.
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
            Dispatcher.InvokeAsync(PositionOverlay, DispatcherPriority.Background);
        }

        private async Task AdvanceEpisodeAsync()
        {
            if (_movedToNext) return;
            _movedToNext = true;
            _overlay?.ShowNextEpisode(false);
            await AccumulateWatchTimeAsync();
            await SaveProgressAsync();
            if (_playlistIndex + 1 >= _playlist.Count) { Close(); return; }
            _playlistIndex++;
            await PlayCurrentAsync();
        }

        private async Task PrevEpisodeAsync()
        {
            if (_playlistIndex <= 0) return;
            await AccumulateWatchTimeAsync();
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
            if (_player == null) return;

            long posMs = 0;
            try { posMs = _player.Time; } catch { return; }
            if (posMs <= 0) return;

            long posSec = posMs / 1000;

            // Movie path — ep.Id is -1 (fake episode), save to MediaItem directly
            if (ep.Id < 0)
            {
                long movieRuntime = ep.RuntimeMinutes > 0 ? ep.RuntimeMinutes * 60L : 7200;
                bool movieDone = posSec >= movieRuntime * 0.85;
                using var movieDb = new VaultContext();
                var movieItem = await movieDb.MediaItems.FindAsync(_mediaItem.Id);
                if (movieItem != null)
                {
                    movieItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds =
                        movieDone ? 0 : posSec;
                    if (movieDone)
                        movieItem.WatchStatus = _mediaItem.WatchStatus = "Completed";
                    await movieDb.SaveChangesAsync();
                }
                return;
            }

            long runtimeSec = ep.RuntimeMinutes > 0 ? ep.RuntimeMinutes * 60L : 1440;
            bool finished = posSec >= runtimeSec * 0.85;

            using var db = new VaultContext();
            var dbEp = await db.Episodes.FindAsync(ep.Id);
            if (dbEp != null)
            {
                // FIX: fetch dbItem regardless of finished/not-finished — we now
                // need to mirror mid-episode progress onto MediaItem too, not
                // just update it when an episode fully completes.
                var dbItem = await db.MediaItems.FindAsync(_mediaItem.Id);

                if (finished)
                {
                    dbEp.IsWatched = ep.IsWatched = true;
                    dbEp.WatchedDate = DateTime.Now;
                    dbEp.ResumePositionSeconds = ep.ResumePositionSeconds = 0;

                    if (dbItem != null)
                    {
                        dbItem.WatchedEpisodes = await db.Episodes
                            .CountAsync(e => e.MediaItemId == _mediaItem.Id && e.IsWatched);
                        dbItem.CurrentEpisode = ep.EpisodeNumber;
                        dbItem.CurrentSeason = ep.SeasonNumber;
                        dbItem.WatchStatus = dbItem.TotalEpisodes > 0 &&
                            dbItem.WatchedEpisodes >= dbItem.TotalEpisodes
                            ? "Completed" : "Watching";

                        // Episode is done — clear the mirrored resume position
                        // so the tile bar doesn't keep showing "almost done".
                        dbItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds = 0;
                    }
                }
                else
                {
                    dbEp.ResumePositionSeconds = ep.ResumePositionSeconds = posSec;

                    // FIX: mirror the current episode's resume position + runtime
                    // + which episode it is onto MediaItem. This is what the
                    // library tile reads to draw the Netflix-style progress line
                    // and "S01E05" label while you're still mid-episode.
                    if (dbItem != null)
                    {
                        dbItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds = posSec;
                        dbItem.RuntimeMinutes = _mediaItem.RuntimeMinutes = ep.RuntimeMinutes;
                        dbItem.CurrentSeason = ep.SeasonNumber;
                        dbItem.CurrentEpisode = ep.EpisodeNumber;
                    }
                }

                await db.SaveChangesAsync();
            }
        }

        // ------------------------------------------------------------------ //
        //  Subtitles
        // ------------------------------------------------------------------ //
        private async Task AutoEnableSubtitlesAsync()
        {
            await Task.Delay(1500);
            if (_player == null || _closing) return;
            try
            {
                var tracks = _player.SpuDescription.Where(t => t.Id != -1).ToArray();
                if (tracks.Length > 0)
                    _player.SetSpu(tracks[0].Id);
            }
            catch { }
        }

        // FIX: subtitle file picker now opens directly in the current episode's
        // own folder (the movie's folder, or the show's season folder) instead
        // of wherever Windows last remembered — since subtitle files always
        // live right next to the video file in this library.
        private void OnAddSubtitleRequested()
        {
            string? startDir = null;
            try
            {
                var ep = CurrentEpisode;
                if (!string.IsNullOrEmpty(ep.FilePath))
                    startDir = Path.GetDirectoryName(ep.FilePath);
            }
            catch { }

            if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir))
                startDir = _mediaItem.FolderPath;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select subtitle file",
                Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.sub;*.vtt;*.idx|All files|*.*",
                InitialDirectory = !string.IsNullOrEmpty(startDir) && Directory.Exists(startDir)
                    ? startDir : ""
            };
            if (dlg.ShowDialog() != true) return;
            try { _player?.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true); }
            catch { }
        }

        private async Task PopulateAudioTracksAsync()
        {
            await Task.Delay(1200);
            if (_player == null || _overlay == null || _closing) return;

            var tracks = Array.Empty<(int Id, string Name)>();
            int currentId = -1;
            try
            {
                tracks = _player.AudioTrackDescription
                    .Select(t => (Id: t.Id, Name: t.Name ?? "")).ToArray();
                currentId = _player.AudioTrack;
            }
            catch { return; }

            var settings = AppSettings.Load();
            string pref = settings.PreferredAudioLanguage;

            if (!string.IsNullOrEmpty(pref) && tracks.Length > 0)
            {
                bool found = false;
                foreach (var t in tracks)
                {
                    if (t.Id != -1 && (
                        t.Name.Contains(pref, StringComparison.OrdinalIgnoreCase) ||
                        pref.Contains(t.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Debug.WriteLine($"[Audio] Selected '{t.Name}' (matched preference '{pref}')");
                        try { _player.SetAudioTrack(t.Id); currentId = t.Id; } catch { }
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    string fallback = tracks.Length > 0 ? tracks[0].Name : "none";
                    Debug.WriteLine($"[Audio] Preferred '{pref}' not found, fell back to default: {fallback}");
                }
            }

            Dispatcher.Invoke(() => _overlay.SetAudioTracks(tracks, currentId, pref));
        }

        private void OnAudioTrackChangeRequested(int trackId, string trackName, bool setAsDefault)
        {
            try { if (_player != null) _player.SetAudioTrack(trackId); } catch { }

            var settings = AppSettings.Load();

            if (setAsDefault)
            {
                settings.PreferredAudioLanguage = trackId == -1 ? "" : trackName;
                settings.Save();
                Debug.WriteLine($"[Audio] Default set to: '{settings.PreferredAudioLanguage}'");
            }

            if (_overlay != null && _player != null)
            {
                try
                {
                    (int Id, string Name)[] tuples = _player.AudioTrackDescription
                        .Select(t => (Id: t.Id, Name: t.Name ?? "")).ToArray();
                    _overlay.SetAudioTracks(tuples, trackId, settings.PreferredAudioLanguage);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ //
        //  Fingerprinting
        // ------------------------------------------------------------------ //
        private async Task StartFingerprintingAsync()
        {
            _fpCts = new CancellationTokenSource();
            var svc = new FingerprintService();
            await svc.ProcessShowAsync(_mediaItem.Id, _playlist, _playlistIndex, _fpCts.Token);
            Dispatcher.Invoke(UpdateHighlights);
        }

        // ------------------------------------------------------------------ //
        //  Close
        // ------------------------------------------------------------------ //
        private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _closing = true;
            _fpCts.Cancel();
            _uiTimer.Stop();
            _hideTimer.Stop();
            _overlay?.Close();
            await AccumulateWatchTimeAsync();
            await SaveProgressAsync();
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();
        }
    }
}
