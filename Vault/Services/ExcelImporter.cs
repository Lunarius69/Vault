using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class ImportResult
    {
        public int GamesImported { get; set; }
        public int MediaImported { get; set; }
        public int DuplicatesSkipped { get; set; }
        public int HorrorSkipped { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class ExcelImporter
    {
        private readonly VaultContext _db;
        private readonly BoxArtService _boxArtService;

        // ── Sheets that represent a single "Complete Library" collection entry ──
        private static readonly HashSet<string> CompleteLibrarySheets = new(StringComparer.OrdinalIgnoreCase)
        {
        };

        // ── Sheets to skip entirely ──────────────────────────────────────────────
        private static readonly HashSet<string> SkipSheets = new(StringComparer.OrdinalIgnoreCase)
        {
            "Summary", "SSD Games", "Storage Forecast", "Year-by-Year Timeline",
            "Assumptions", "Wishlist"
        };

        // ── Horror game genres ───────────────────────────────────────────────────
        // All entries whose genre matches any of these will be skipped at import.
        // Also catches games with no genre that match known horror titles below.
        private static readonly HashSet<string> HorrorGenres = new(StringComparer.OrdinalIgnoreCase)
        {
            "Survival Horror",
            "Survival Horror / Action",
            "Survival Horror / First-Person",
            "Survival Horror / Action / First-Person",
            "Survival Horror / Psychological",
            "Action-Adventure / Survival Horror",
            "Action / Psychological Horror",
        };

        // Games with no genre column populated but known to be horror
        private static readonly HashSet<string> HorrorTitles = new(StringComparer.OrdinalIgnoreCase)
        {
            "Resident Evil (Remake)",
            "Resident Evil 0",
            "Resident Evil 4",
            "Silent Hill 2",
            "Silent Hill 3",
            "Silent Hill: Shattered Memories",
            "Forbidden Siren",
            "Resident Evil Requiem",
            "Fatal Frame: Crimson Butterfly Remake",
        };

        // ── Platform priority for duplicate resolution ───────────────────────────
        // When the same title exists on multiple platforms, the entry on the
        // highest-priority platform is kept; all others are skipped.
        // Higher number = higher priority (more modern / better version).
        private static readonly Dictionary<string, int> PlatformPriority =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Atari 2600"] = 1,
                ["NES"] = 2,
                ["Sega Master System"] = 3,
                ["Game Boy & GBC"] = 4,
                ["SNES"] = 5,
                ["Sega Genesis"] = 6,
                ["Game Boy Advance"] = 7,
                ["Nintendo 64"] = 8,
                ["Sega Saturn"] = 9,
                ["Sega Dreamcast"] = 10,
                ["PlayStation 1"] = 11,
                ["PlayStation 2"] = 12,
                ["Xbox Original"] = 13,
                ["GameCube"] = 14,
                ["Nintendo DS"] = 15,
                ["PSP"] = 16,
                ["Wii"] = 17,
                ["Xbox 360"] = 18,
                ["PlayStation 3"] = 19,
                ["Nintendo 3DS"] = 20,
                ["PS Vita"] = 21,
                ["Wii U"] = 22,
                ["Nintendo Switch"] = 23,
                ["Xbox One"] = 24,
                ["PlayStation 4"] = 25,
                ["Nintendo Switch 2"] = 26,
                ["PlayStation 5"] = 27,
                ["PC Games"] = 28,
                ["PC"] = 28,
                ["Live Service"] = 29,
            };

        // ── Titles where every platform version is intentionally distinct ────────
        // These will NOT be deduplicated — every platform entry is kept as-is.
        // (e.g. Aladdin on Genesis and SNES are genuinely different games)
        // Add titles here when you want to override the duplicate removal logic.
        private static readonly HashSet<string> AllowMultiplePlatforms =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // intentionally empty — all duplicates currently removed by platform priority
            };

        public ExcelImporter(VaultContext db, BoxArtService boxArtService)
        {
            _db = db;
            _boxArtService = boxArtService;
            ExcelPackage.License.SetNonCommercialPersonal("Vault");
        }

        // ────────────────────────────────────────────────────────────────────────
        // ImportGamesAsync
        // ────────────────────────────────────────────────────────────────────────
        public async Task<ImportResult> ImportGamesAsync(string filePath)
        {
            var result = new ImportResult();
            if (!File.Exists(filePath))
            {
                result.Errors.Add("File not found: " + filePath);
                return result;
            }

            // Build a deduplicated candidate list from the whole workbook first,
            // so that cross-sheet duplicate resolution works correctly.
            var candidates = CollectCandidates(filePath, result);

            foreach (var game in candidates)
            {
                if (_db.Games.Any(g => g.Title == game.Title && g.Platform == game.Platform))
                    continue;

                _db.Games.Add(game);
                result.GamesImported++;
            }

            // Import Wishlist sheet (never deduplicated against main library)
            await ImportWishlistAsync(filePath, result);

            await _db.SaveChangesAsync();
            return result;
        }

        // ────────────────────────────────────────────────────────────────────────
        // FetchMissingArtAsync
        // Fetches box art + hero for every game that is missing either.
        // Call this after ImportGamesAsync, or on demand from the UI.
        // ────────────────────────────────────────────────────────────────────────
        public async Task<int> FetchMissingArtAsync(
    System.Threading.CancellationToken ct = default)
        {
            var missing = await _db.Games
                .Where(g =>
                    !g.IsWishlist &&
                    (string.IsNullOrEmpty(g.BoxArtPath) || !File.Exists(g.BoxArtPath)))
                .ToListAsync(ct);

            int fetched = 0;
            foreach (var game in missing)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    string? boxArt = await _boxArtService.GetBoxArtAsync(game, ct);

                    if (boxArt != null)
                    {
                        game.BoxArtPath = boxArt;
                        fetched++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    File.AppendAllText(
                        Path.Combine(AppContext.BaseDirectory, "missing_art.log"),
                        $"{DateTime.UtcNow:u} | [{game.Platform}] {game.Title} | {ex.Message}\n");
                }
            }

            await _db.SaveChangesAsync(ct);
            return fetched;
        }

        // ────────────────────────────────────────────────────────────────────────
        // ImportMediaAsync  (unchanged logic, result type corrected)
        // ────────────────────────────────────────────────────────────────────────
        public async Task<ImportResult> ImportMediaAsync(string filePath)
        {
            var result = new ImportResult();
            if (!File.Exists(filePath))
            {
                result.Errors.Add("File not found: " + filePath);
                return result;
            }

            var existing = await _db.MediaItems.ToListAsync();
            _db.MediaItems.RemoveRange(existing);
            await _db.SaveChangesAsync();

            using var package = new ExcelPackage(new FileInfo(filePath));

            foreach (var sheet in package.Workbook.Worksheets)
            {
                if (sheet.Name == "Summary" || sheet.Name == "Storage Estimate") continue;
                if (sheet.Dimension == null) continue;

                string mediaType = DetectMediaType(sheet.Name);

                int titleCol = -1, yearCol = -1, sizeCol = -1,
                    episodesCol = -1, seasonsCol = -1;

                for (int c = 1; c <= sheet.Dimension.Columns; c++)
                {
                    string h = sheet.Cells[1, c].Text.Trim().ToLower();
                    if (h == "title") titleCol = c;
                    else if (h.Contains("year") || h.Contains("release")) yearCol = c;
                    else if (h.Contains("size")) sizeCol = c;
                    else if (h.Contains("episode")) episodesCol = c;
                    else if (h.Contains("season")) seasonsCol = c;
                }

                if (titleCol == -1) continue;

                for (int row = 2; row <= sheet.Dimension.Rows; row++)
                {
                    string rawTitle = sheet.Cells[row, titleCol].Text.Trim();
                    if (string.IsNullOrWhiteSpace(rawTitle)) continue;

                    string cleanTitle = rawTitle.Trim();
                    if (_db.MediaItems.Any(m =>
                            m.Title == cleanTitle && m.MediaType == mediaType))
                        continue;

                    int totalEpisodes = episodesCol > 0
                        ? ParseInt(sheet.Cells[row, episodesCol].Text) ?? 0 : 0;

                    int? totalSeasons = seasonsCol > 0
                        ? ParseInt(sheet.Cells[row, seasonsCol].Text) : null;

                    _db.MediaItems.Add(new MediaItem
                    {
                        Title = cleanTitle,
                        MediaType = mediaType,
                        Year = yearCol > 0 ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
                        TotalEpisodes = totalEpisodes,
                        TotalSeasons = totalSeasons,
                        WatchStatus = "Not Started",
                        TmdbId = 0
                    });
                    result.MediaImported++;
                }
            }

            await _db.SaveChangesAsync();
            return result;
        }

        // ════════════════════════════════════════════════════════════════════════
        // Private helpers
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Reads every non-skipped sheet, filters horror, then resolves
        /// cross-sheet duplicates by platform priority.
        /// Returns the final de-duplicated list ready for DB insertion.
        /// </summary>
        private List<Game> CollectCandidates(string filePath, ImportResult result)
        {
            using var package = new ExcelPackage(new FileInfo(filePath));

            // Step 1 — read all rows from all sheets into a flat list
            var all = new List<(string platform, string title, Game game)>();

            foreach (var sheet in package.Workbook.Worksheets)
            {
                if (SkipSheets.Contains(sheet.Name)) continue;
                if (sheet.Dimension == null) continue;

                string platform = sheet.Name;

                // Complete Library shortcut
                if (CompleteLibrarySheets.Contains(platform))
                {
                    string sizeText = sheet.Cells[2, 4].Text.Trim();
                    all.Add((platform, $"{platform} — Complete Library", new Game
                    {
                        Title = $"{platform} — Complete Library",
                        Platform = platform,
                        LibraryType = "Complete Library",
                        FileSizeGB = ParseSize(sizeText),
                        Status = "Not Started",
                        IsWishlist = false,
                        IsDownloaded = false
                    }));
                    continue;
                }

                // Discover columns
                int titleCol = -1, yearCol = -1, sizeCol = -1, statusCol = -1,
                    genreCol = -1, platformCol = -1, noteCol = -1;

                for (int c = 1; c <= sheet.Dimension.Columns; c++)
                {
                    string h = sheet.Cells[1, c].Text.Trim().ToLower();
                    if (h == "title" || h == "game") titleCol = c;
                    else if (h == "year") yearCol = c;
                    else if (h == "size" || h == "rom size" ||
                             h == "avg size (gb)") sizeCol = c;
                    else if (h == "status") statusCol = c;
                    else if (h == "genre") genreCol = c;
                    else if (h == "platform") platformCol = c;
                    else if (h == "note" || h == "notes") noteCol = c;
                }

                if (titleCol == -1) continue;

                for (int row = 2; row <= sheet.Dimension.Rows; row++)
                {
                    string title = sheet.Cells[row, titleCol].Text.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (title.StartsWith("✅") || title.StartsWith("📥") ||
                        title.StartsWith("Complete Library") ||
                        title.StartsWith("Download from"))
                        continue;

                    string gamePlatform = platformCol > 0
                        ? sheet.Cells[row, platformCol].Text.Trim()
                        : platform;
                    if (string.IsNullOrWhiteSpace(gamePlatform))
                        gamePlatform = platform;

                    // ── Horror filter ────────────────────────────────────────
                    string genre = genreCol > 0
                        ? sheet.Cells[row, genreCol].Text.Trim() : "";

                    if (IsHorror(title, genre))
                    {
                        result.HorrorSkipped++;
                        continue;
                    }

                    string rawStatus = statusCol > 0
                        ? sheet.Cells[row, statusCol].Text.Trim() : "";

                    all.Add((gamePlatform, title, new Game
                    {
                        Title = title,
                        Platform = gamePlatform,
                        Year = yearCol > 0 ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
                        FileSizeGB = sizeCol > 0 ? ParseSize(sheet.Cells[row, sizeCol].Text) : null,
                        Genre = genre,
                        Status = NormalizeStatus(rawStatus),
                        LibraryType = "Owned",
                        IsWishlist = false,
                        IsDownloaded = false
                    }));
                }
            }

            // Step 2 — deduplicate by title, keeping the highest-priority platform
            var final = new List<Game>();
            var byTitle = all
                .GroupBy(x => x.title.Trim().ToLowerInvariant())
                .ToList();

            foreach (var group in byTitle)
            {
                var entries = group.ToList();

                if (entries.Count == 1 || AllowMultiplePlatforms.Contains(entries[0].title))
                {
                    final.AddRange(entries.Select(e => e.game));
                    continue;
                }

                // Keep the entry with the highest platform priority
                var best = entries
                    .OrderByDescending(e =>
                        PlatformPriority.TryGetValue(e.platform, out int p) ? p : 0)
                    .First();

                final.Add(best.game);
                result.DuplicatesSkipped += entries.Count - 1;
            }

            return final;
        }

        private async Task ImportWishlistAsync(string filePath, ImportResult result)
        {
            using var package = new ExcelPackage(new FileInfo(filePath));

            var wishlistSheet = package.Workbook.Worksheets
                .FirstOrDefault(s => s.Name.Equals("Wishlist",
                    StringComparison.OrdinalIgnoreCase));

            if (wishlistSheet?.Dimension == null) return;

            int titleCol = -1, platformCol = -1, sizeCol = -1, yearCol = -1;
            for (int c = 1; c <= wishlistSheet.Dimension.Columns; c++)
            {
                string h = wishlistSheet.Cells[1, c].Text.Trim().ToLower();
                if (h == "title" || h == "game") titleCol = c;
                else if (h == "platform" || h == "console") platformCol = c;
                else if (h == "size") sizeCol = c;
                else if (h == "year") yearCol = c;
            }

            if (titleCol < 0) return;

            for (int row = 2; row <= wishlistSheet.Dimension.Rows; row++)
            {
                string title = wishlistSheet.Cells[row, titleCol].Text.Trim();
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (title.StartsWith("✅") || title.StartsWith("📥")) continue;

                string plt = platformCol > 0
                    ? wishlistSheet.Cells[row, platformCol].Text.Trim()
                    : "Unknown";

                if (_db.Games.Any(g => g.Title == title && g.IsWishlist)) continue;

                _db.Games.Add(new Game
                {
                    Title = title,
                    Platform = plt,
                    Year = yearCol > 0 ? ParseYear(wishlistSheet.Cells[row, yearCol].Text) : null,
                    FileSizeGB = sizeCol > 0 ? ParseSize(wishlistSheet.Cells[row, sizeCol].Text) : null,
                    Status = "Wishlist",
                    LibraryType = "Wishlist",
                    IsWishlist = true,
                    IsDownloaded = false
                });
                result.GamesImported++;
            }
        }

        // ── Horror check ─────────────────────────────────────────────────────────
        private static bool IsHorror(string title, string genre) =>
            (!string.IsNullOrEmpty(genre) && HorrorGenres.Contains(genre)) ||
            HorrorTitles.Contains(title);

        // ── Status normalizer ────────────────────────────────────────────────────
        private static string NormalizeStatus(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Not Started";
            return raw.Trim().ToLower() switch
            {
                "playing" or "in progress" => "Playing",
                "completed" or "complete" or
                "finished" => "Completed",
                "on hold" or "paused" => "On Hold",
                "dropped" => "Dropped",
                "released" => "Not Started",
                _ => "Not Started"
            };
        }

        // ── Media type detector ──────────────────────────────────────────────────
        private static string DetectMediaType(string sheetName)
        {
            string s = sheetName.ToLower();
            if (s.Contains("anime") && s.Contains("movie")) return "AnimeMovie";
            if (s.Contains("anime")) return "Anime";
            if (s.Contains("animated") && s.Contains("movie")) return "AnimatedMovie";
            if (s.Contains("animated") || s.Contains("western")) return "AnimatedSeries";
            if (s.Contains("movie")) return "Movie";
            return "Show";
        }

        // ── Parsers ──────────────────────────────────────────────────────────────
        private static int? ParseYear(string text)
        {
            if (int.TryParse(text.Trim(), out int y) && y > 1900 && y < 2100) return y;
            return null;
        }

        private static double? ParseSize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Replace(",", ".").Replace("~", "").Trim().ToUpper();
            double mult = text.Contains("TB") ? 1024
                        : text.Contains("MB") ? 1.0 / 1024
                        : 1;
            string num = System.Text.RegularExpressions.Regex
                .Match(text, @"[\d.]+").Value;
            if (double.TryParse(num,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double val))
                return Math.Round(val * mult, 2);
            return null;
        }

        private static int? ParseInt(string text)
        {
            if (int.TryParse(text.Trim(), out int v)) return v;
            return null;
        }
    }
}