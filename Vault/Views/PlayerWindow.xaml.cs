using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
        private LibVLCSharp.Shared.MediaPlayer? _player;

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

        private DateTime? _episodePlayStartTime;

        private WindowState _stateBeforeMinimize = WindowState.Maximized;
        private bool _wasMinimized = false;
        private bool _overlayHiddenForMinimize = false;

        // ------------------------------------------------------------------ //
        //  Win32 – used for accurate window bounds + fullscreen fix
        // ------------------------------------------------------------------ //
        private const int MONITOR_DEFAULTTONEAREST = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

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

        // ------------------------------------------------------------------ //
        //  WM_GETMINMAXINFO
        // ------------------------------------------------------------------ //
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

                RECT monitorArea = monitorInfo.rcMonitor;

                mmi.ptMaxPosition.X = 0;
                mmi.ptMaxPosition.Y = 0;
                mmi.ptMaxSize.X = Math.Abs(monitorArea.Right - monitorArea.Left);
                mmi.ptMaxSize.Y = Math.Abs(monitorArea.Bottom - monitorArea.Top);
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

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

            PreviewKeyDown += PlayerWindow_PreviewKeyDown;
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

            await Task.Delay(200);

            _player.EndReached += OnEndReached;

            _overlay = new OverlayWindow(this);
            _overlay.Opacity = 0;
            _overlay.Show();
            await Task.Delay(50);
            PositionOverlay();

            // Wire events
            _overlay.PlayPauseRequested += () => TogglePlayPause();
            _overlay.SeekRequested += pct => SeekToPct(pct);
            _overlay.SkipBackRequested += () => { try { _player?.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, _player.Time - 10000))); } catch { } };
            _overlay.SkipForwardRequested += () => { try { _player?.SeekTo(TimeSpan.FromMilliseconds(Math.Min(_player.Length, _player.Time + 10000))); } catch { } };
            _overlay.SkipIntroRequested += () => { try { if (_player != null) _player.SeekTo(TimeSpan.FromSeconds(CurrentEpisode.IntroEnd)); } catch { } };
            _overlay.NextEpisodeRequested += async () => await AdvanceEpisodeAsync();
            _overlay.NextEpisodeButtonRequested += async () => await AdvanceEpisodeAsync();
            _overlay.PrevEpisodeRequested += async () => await PrevEpisodeAsync();
            _overlay.CloseRequested += () => Close();
            _overlay.VolumeChanged += v => { try { if (_player != null) _player.Volume = Math.Clamp(v, 0, 100); } catch { } };
            _overlay.MouseActivity += OnMouseActivity;
            _overlay.FullscreenRequested += ToggleFullscreen;
            _overlay.AddSubtitleRequested += OnAddSubtitleRequested;
            _overlay.AudioTrackChangeRequested += OnAudioTrackChangeRequested;

            _uiTimer.Tick += UiTimer_Tick;
            _hideTimer.Tick += HideTimer_Tick;
            _uiTimer.Start();
            _hideTimer.Start();

            Focusable = true;
            Focus();

            await PlayCurrentAsync();
            _ = StartFingerprintingAsync();
        }

        // ------------------------------------------------------------------ //
        //  Overlay positioning – now uses real Win32 window rect
        // ------------------------------------------------------------------ //
        private void PositionOverlay()
        {
            if (_overlay == null || _closing) return;

            try
            {
                var helper = new WindowInteropHelper(this);
                if (helper.Handle == IntPtr.Zero) return;

                if (!GetWindowRect(helper.Handle, out RECT rect)) return;

                // Convert physical pixels → WPF DIPs (handles DPI scaling)
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget != null)
                {
                    var fromDevice = source.CompositionTarget.TransformFromDevice;
                    var topLeft = fromDevice.Transform(new System.Windows.Point(rect.Left, rect.Top));
                    var bottomRight = fromDevice.Transform(new System.Windows.Point(rect.Right, rect.Bottom));

                    double left = topLeft.X;
                    double top = topLeft.Y;
                    double width = bottomRight.X - topLeft.X;
                    double height = bottomRight.Y - topLeft.Y;

                    if (width < 50 || height < 50) return;

                    _overlay.Left = left;
                    _overlay.Top = top;
                    _overlay.Width = width;
                    _overlay.Height = height;
                }
                else
                {
                    // Fallback
                    _overlay.Left = rect.Left;
                    _overlay.Top = rect.Top;
                    _overlay.Width = rect.Right - rect.Left;
                    _overlay.Height = rect.Bottom - rect.Top;
                }
            }
            catch { }
        }

        private async void ForceRepositionOverlay()
        {
            PositionOverlay();

            Dispatcher.InvokeAsync(PositionOverlay, DispatcherPriority.Background);
            Dispatcher.InvokeAsync(PositionOverlay, DispatcherPriority.ContextIdle);
            Dispatcher.InvokeAsync(PositionOverlay, DispatcherPriority.ApplicationIdle);

            await Task.Delay(50);
            PositionOverlay();

            await Task.Delay(100);
            PositionOverlay();

            await Task.Delay(200);
            PositionOverlay();
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

                if (_stateBeforeMinimize == WindowState.Maximized && WindowState != WindowState.Maximized)
                {
                    WindowStyle = WindowStyle.None;
                    WindowState = WindowState.Maximized;
                    return;
                }

                if (_stateBeforeMinimize == WindowState.Maximized)
                    WindowStyle = WindowStyle.None;
            }
            else
            {
                _stateBeforeMinimize = WindowState;
            }

            if (_overlayHiddenForMinimize)
            {
                _overlayHiddenForMinimize = false;
                _overlay?.Show();
                OnMouseActivity();
            }

            ForceRepositionOverlay();
        }

        // ------------------------------------------------------------------ //
        //  Keyboard
        // ------------------------------------------------------------------ //
        private void PlayerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TextBox or System.Windows.Controls.PasswordBox)
                return;

            HandleKey(e.Key);

            switch (e.Key)
            {
                case Key.Space:
                case Key.Left:
                case Key.Right:
                case Key.N:
                case Key.P:
                case Key.F:
                case Key.F11:
                case Key.Escape:
                    e.Handled = true;
                    break;
            }
        }

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
        //  Fullscreen
        // ------------------------------------------------------------------ //
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

            ForceRepositionOverlay();
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

        private async Task SetCurrentEpisodeAsync(Episode ep)
        {
            if (ep.Id < 0) return;
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

            if (string.IsNullOrEmpty(ep.FilePath) || !File.Exists(ep.FilePath))
            {
                using var db = new VaultContext();
                var dbEp = await db.Episodes.FindAsync(ep.Id);
                if (dbEp != null && !string.IsNullOrEmpty(dbEp.FilePath) && File.Exists(dbEp.FilePath))
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
                MessageBox.Show($"File not found for S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2}.\nSet the media folder first.");
                Close();
                return;
            }

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

            var nextEp = _playlistIndex + 1 < _playlist.Count ? _playlist[_playlistIndex + 1] : null;
            _overlay?.SetEpisodeInfo(ep.SeasonNumber, ep.EpisodeNumber, ep.Title ?? "", nextEp, ep.Id < 0);

            try
            {
                using var media = new LibVLCSharp.Shared.Media(_libVlc!, new Uri(ep.FilePath));
                _player!.Media = media;
                _player.Play();
                _episodePlayStartTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to play file: {ex.Message}");
                return;
            }

            await Task.Delay(500);
            if (_overlay != null) _overlay.Opacity = 1;

            if (ep.ResumePositionSeconds > 0)
            {
                await Task.Delay(300);
                try { _player!.SeekTo(TimeSpan.FromSeconds(ep.ResumePositionSeconds)); }
                catch { }
            }

            await Task.Delay(1000);
            if (!_closing) Dispatcher.Invoke(UpdateHighlights);
            _ = AutoEnableSubtitlesAsync();
            _ = PopulateAudioTracksAsync();

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
                    dbItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds = 0;
                }
                await db.SaveChangesAsync();
            }
        }

        private async Task AccumulateWatchTimeAsync()
        {
            if (_episodePlayStartTime == null) return;

            long elapsedMinutes = (long)(DateTime.Now - _episodePlayStartTime.Value).TotalMinutes;
            _episodePlayStartTime = null;

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
                var m4 = allFiles.FirstOrDefault(f => f.Contains(titleKey, StringComparison.OrdinalIgnoreCase));
                if (m4 != null) return m4;
            }

            return null;
        }

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
                double fallback = _mediaItem.MediaType is "Anime" or "AnimeMovie" ? 180
                                : ep.Id < 0 ? 90
                                : 120;
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
            if (_closing || _player == null) return;
            try
            {
                if (_player.Media == null) return;
                long len = _player.Length;
                if (len <= 0) return;
                var ep = CurrentEpisode;
                _overlay?.UpdateHighlights(ep.IntroStart, ep.IntroEnd, ep.OutroStart, len / 1000.0);
            }
            catch { }
        }

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

        private async Task SaveProgressAsync()
        {
            var ep = CurrentEpisode;
            if (_player == null) return;

            long posMs = 0;
            try { posMs = _player.Time; } catch { return; }
            if (posMs <= 0) return;

            long posSec = posMs / 1000;

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
                        dbItem.ResumePositionSeconds = _mediaItem.ResumePositionSeconds = 0;
                    }
                }
                else
                {
                    dbEp.ResumePositionSeconds = ep.ResumePositionSeconds = posSec;

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
                InitialDirectory = !string.IsNullOrEmpty(startDir) && Directory.Exists(startDir) ? startDir : ""
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
                        try { _player.SetAudioTrack(t.Id); currentId = t.Id; } catch { }
                        found = true;
                        break;
                    }
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

        private async Task StartFingerprintingAsync()
        {
            _fpCts = new CancellationTokenSource();
            var svc = new FingerprintService();
            await svc.ProcessShowAsync(_mediaItem.Id, _playlist, _playlistIndex, _fpCts.Token);
            if (!_closing) Dispatcher.Invoke(UpdateHighlights);
        }

        private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _closing = true;
            _fpCts.Cancel();
            _uiTimer.Stop();
            _hideTimer.Stop();

            if (_overlay != null)
            {
                _overlay.Close();
                _overlay = null;
            }

            await AccumulateWatchTimeAsync();
            await SaveProgressAsync();
            _player?.Stop();
            _player?.Dispose();
            _libVlc?.Dispose();
        }
    }
}