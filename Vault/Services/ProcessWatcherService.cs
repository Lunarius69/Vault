using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vault.Database;

namespace Vault.Services
{
    public sealed class ProcessWatcherService : IDisposable
    {
        // PIDs currently being tracked by GameLauncher.TrackPlaytimeAsync —
        // the watcher skips these to avoid double-counting.
        public static readonly HashSet<int> LauncherOwnedPids = new();

        // Fired on the thread-pool when a session ends and playtime is saved.
        public static event Action<int>? PlaytimeUpdated; // gameId

        public static void NotifyPlaytimeUpdated(int gameId) => PlaytimeUpdated?.Invoke(gameId);

        // FIX: the exe→game map was only ever built once, in the constructor,
        // at app startup. Refresh() existed to reload it after a game's
        // ExePath changes, but nothing ever actually called it — setting a
        // game's exe from GameDetailPage saved to the DB but the running
        // watcher never found out, so it could never detect that game's
        // process no matter how long you waited. Same static-event pattern as
        // PlaytimeUpdated above, so any page can trigger a refresh without
        // needing a direct reference to the one long-lived instance (which
        // lives in MainWindow).
        public static event Action? ExeMapRefreshRequested;

        public static void RequestExeMapRefresh() => ExeMapRefreshRequested?.Invoke();

        private struct Session
        {
            public int ProcessId;
            public DateTime StartTime;
        }

        // gameId → active session
        private readonly Dictionary<int, Session> _activeSessions = new();

        // process name (no extension, case-insensitive) → gameId
        private Dictionary<string, int> _exeToGameId =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Timer _pollTimer;
        private bool _disposed;

        public ProcessWatcherService()
        {
            _ = RefreshExeMapAsync();
            _pollTimer = new Timer(_ => Poll(), null,
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            ExeMapRefreshRequested += Refresh;
        }

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Reload the exe→game map from DB. Call after a game's ExePath changes.
        /// </summary>
        public void Refresh() => _ = RefreshExeMapAsync();

        /// <summary>
        /// Save all in-progress sessions and stop polling. Call on app close.
        /// </summary>
        public async Task FlushAsync()
        {
            _disposed = true;
            ExeMapRefreshRequested -= Refresh;
            _pollTimer.Dispose();

            foreach (var (gameId, session) in _activeSessions.ToList())
            {
                int minutes = (int)(DateTime.Now - session.StartTime).TotalMinutes;
                if (minutes >= 1)
                    await SavePlaytimeAsync(gameId, minutes);
            }
            _activeSessions.Clear();
        }

        // ------------------------------------------------------------------ //
        //  Internals
        // ------------------------------------------------------------------ //

        private async Task RefreshExeMapAsync()
        {
            try
            {
                using var db = new VaultContext();
                var games = db.Games
                    .Where(g => g.ExePath != null && !g.IsWishlist)
                    .Select(g => new { g.Id, g.ExePath })
                    .ToList();

                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in games)
                {
                    if (string.IsNullOrEmpty(g.ExePath)) continue;
                    // Skip ROM files — emulator process name ≠ game
                    if (GameLauncher.IsRomFile(g.ExePath)) continue;
                    string name = Path.GetFileNameWithoutExtension(g.ExePath);
                    if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                        map[name] = g.Id;
                }
                _exeToGameId = map;
            }
            catch { }
        }

        private void Poll()
        {
            if (_disposed) return;
            try
            {
                var allProcs = Process.GetProcesses();

                // FIX: .ProcessName (and .Id, in rare cases) throws if a process
                // exits in the moment between GetProcesses() snapshotting it and
                // this code reading its properties — which happens constantly on
                // a real machine due to normal background process churn. The old
                // code read these inside one big LINQ GroupBy with a single outer
                // try/catch around the whole Poll() body, so a single one of
                // these routine exceptions silently aborted detection for EVERY
                // process that cycle — including the game actually running.
                // That's why external launches were unreliable: on any given
                // 10-second poll, the odds that some unrelated process exits
                // mid-snapshot are high, and that alone was enough to skip
                // detecting your game for that whole cycle. Now each process is
                // read defensively so one exited process can't block the rest.
                var byName = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
                var activePids = new HashSet<int>();

                foreach (var p in allProcs)
                {
                    int pid;
                    string name;
                    try
                    {
                        pid = p.Id;
                        name = p.ProcessName;
                    }
                    catch
                    {
                        continue; // process exited mid-enumeration or is inaccessible — skip it, not the whole poll
                    }

                    activePids.Add(pid);
                    if (!byName.ContainsKey(name))
                        byName[name] = p;
                }

                // Detect newly started games
                foreach (var (exeName, gameId) in _exeToGameId)
                {
                    if (_activeSessions.ContainsKey(gameId)) continue;
                    if (!byName.TryGetValue(exeName, out var proc)) continue;

                    int procId;
                    try { procId = proc.Id; }
                    catch { continue; } // exited right as we tried to grab its Id

                    // Skip if launcher is already tracking this PID
                    if (LauncherOwnedPids.Contains(procId)) continue;

                    _activeSessions[gameId] = new Session
                    {
                        ProcessId = procId,
                        StartTime = DateTime.Now
                    };
                    Debug.WriteLine($"[Watcher] Session started — gameId={gameId} exe={exeName} pid={procId}");
                    _ = MarkPlayingAsync(gameId);
                }

                // Detect ended sessions
                var ended = _activeSessions
                    .Where(kv => !activePids.Contains(kv.Value.ProcessId))
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var gameId in ended)
                {
                    var session = _activeSessions[gameId];
                    _activeSessions.Remove(gameId);
                    int minutes = (int)(DateTime.Now - session.StartTime).TotalMinutes;
                    Debug.WriteLine($"[Watcher] Session ended — gameId={gameId} +{minutes} min");
                    if (minutes >= 1)
                        _ = SavePlaytimeAsync(gameId, minutes);
                }
            }
            catch { }
        }

        private static async Task MarkPlayingAsync(int gameId)
        {
            try
            {
                using var db = new VaultContext();
                var game = await db.Games.FindAsync(gameId);
                if (game == null) return;
                if (game.Status == "Not Started")
                {
                    game.Status = "Playing";
                    await db.SaveChangesAsync();
                    PlaytimeUpdated?.Invoke(gameId);
                }
            }
            catch { }
        }

        private static async Task SavePlaytimeAsync(int gameId, int minutes)
        {
            try
            {
                using var db = new VaultContext();
                var game = await db.Games.FindAsync(gameId);
                if (game == null) return;
                game.PlaytimeMinutes += minutes;
                game.LastPlayed = DateTime.Now;
                await db.SaveChangesAsync();
                PlaytimeUpdated?.Invoke(gameId);
            }
            catch { }
        }

        public void Dispose()
        {
            if (!_disposed)
                _ = FlushAsync();
        }
    }
}