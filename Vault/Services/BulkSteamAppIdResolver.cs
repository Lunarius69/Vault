using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    /// <summary>
    /// One-shot bulk resolver that finds and caches Steam AppIDs for all PC games
    /// that don't have one yet. Run this once from Settings — after that, every game
    /// detail page loads achievements instantly from the cached SteamAppId.
    ///
    /// Strategy per game (in order):
    ///   1. steam_appid.txt next to the exe / rom (instant, no network)
    ///   2. "steam: 12345" pattern in the Notes field (no network)
    ///   3. Steam store search API by title (rate-limited to ~1.5 req/s)
    ///
    /// Games that resolve to an AppID but have no achievement schema are still
    /// saved — the watcher will just show "no achievements" rather than the
    /// "Steam AppID required" error banner.
    /// </summary>
    public class BulkSteamAppIdResolver
    {
        public record ResolveResult(
            int Total,
            int Resolved,
            int AlreadyHad,
            int Skipped,
            List<string> Failed);

        /// <summary>
        /// Runs the full bulk resolution. Saves to DB as it goes so progress
        /// is not lost if cancelled.
        /// </summary>
        public async Task<ResolveResult> ResolveAllAsync(
            IProgress<(int current, int total, string title)>? progress = null,
            CancellationToken ct = default)
        {
            List<Game> games;

            try
            {
                using var db = new VaultContext();
                // Fetch non-wishlist games from DB, then filter by platform in memory
                // (EF Core cannot translate the IsPcGame method call into SQL)
                var all = await db.Games
                    .Where(g => !g.IsWishlist)
                    .ToListAsync(ct);

                games = all.Where(IsPcGame).ToList();
            }
            catch (Exception ex)
            {
                // If we can't even load the games list, return a safe empty result
                return new ResolveResult(0, 0, 0, 0, new List<string> { $"Failed to load games: {ex.Message}" });
            }

            int total = games.Count;
            int resolved = 0;
            int alreadyHad = 0;
            int skipped = 0;
            var failed = new List<string>();

            for (int i = 0; i < games.Count; i++)
            {
                // Check for cancellation before each game — safe to stop at any point
                if (ct.IsCancellationRequested) break;

                var game = games[i];

                try
                {
                    progress?.Report((i + 1, total, game.Title ?? "Unknown"));
                }
                catch { /* progress reporting failure must never stop the loop */ }

                try
                {
                    // ── Already resolved ──────────────────────────────────────────
                    if (game.SteamAppId.HasValue)
                    {
                        alreadyHad++;
                        continue;
                    }

                    // ── 1. steam_appid.txt on disk ────────────────────────────────
                    int? appId = TryLocalFile(game);

                    // ── 2. Notes field pattern ────────────────────────────────────
                    if (appId == null)
                        appId = TryNotesField(game);

                    // ── 3. Steam store search API ─────────────────────────────────
                    if (appId == null)
                    {
                        try
                        {
                            appId = await AchievementWatcherService.SearchSteamAppIdAsync(game.Title ?? "");
                        }
                        catch (Exception ex)
                        {
                            failed.Add($"{game.Title}: {ex.Message}");
                        }

                        // Polite rate-limit: ~1.5 req/s — use the cancellation token
                        // so the delay itself is also cancellable
                        try { await Task.Delay(650, ct); }
                        catch (OperationCanceledException) { break; }
                    }

                    if (appId == null)
                    {
                        // Non-Steam game or title mismatch — skip silently
                        skipped++;
                        continue;
                    }

                    // ── Persist to DB ─────────────────────────────────────────────
                    try
                    {
                        using var db = new VaultContext();
                        var dbGame = await db.Games.FindAsync(new object[] { game.Id }, ct);
                        if (dbGame != null)
                        {
                            dbGame.SteamAppId = appId;
                            await db.SaveChangesAsync(ct);
                        }
                        game.SteamAppId = appId; // update in-memory list too
                        resolved++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{game.Title} (DB save): {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    // Per-game catch — one broken game must never stop the whole run
                    failed.Add($"{game.Title ?? "Unknown"} (unexpected): {ex.Message}");
                }
            }

            return new ResolveResult(total, resolved, alreadyHad, skipped, failed);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static bool IsPcGame(Game game)
        {
            try
            {
                string p = game?.Platform?.ToLower() ?? "";
                return p.Contains("pc") || p.Contains("windows");
            }
            catch { return false; }
        }

        private static int? TryLocalFile(Game game)
        {
            try
            {
                string? dir = null;
                if (!string.IsNullOrEmpty(game.ExePath))
                    dir = Path.GetDirectoryName(game.ExePath);
                else if (!string.IsNullOrEmpty(game.EmulatorPath))
                    dir = Path.GetDirectoryName(game.EmulatorPath);

                if (dir == null) return null;

                foreach (string candidate in new[]
                {
                    Path.Combine(dir, "steam_appid.txt"),
                    Path.Combine(Path.GetDirectoryName(dir) ?? dir, "steam_appid.txt"),
                })
                {
                    if (File.Exists(candidate) &&
                        int.TryParse(File.ReadAllText(candidate).Trim(), out int id))
                        return id;
                }
            }
            catch { }

            return null;
        }

        private static int? TryNotesField(Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.Notes)) return null;
                var m = Regex.Match(game.Notes, @"steam[:\s/]+(\d{4,8})", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int id))
                    return id;
            }
            catch { }

            return null;
        }
    }
}