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

        // ── Sheets to skip entirely ──────────────────────────────────────────────
        private static readonly HashSet<string> SkipSheets = new(StringComparer.OrdinalIgnoreCase)
        {
            "Summary", "Storage Forecast", "Year-by-Year Timeline",
            "Assumptions", "Wishlist"
        };

        // ── Horror game genres/titles ────────────────────────────────────────────
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

        // ── Titles that match a horror genre but should still be imported ────────
        // Alan Wake games are action/thriller — not horror — but their genre
        // column happens to read "Survival Horror" in the spreadsheet.
        private static readonly HashSet<string> HorrorExceptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "Alan Wake Remastered",
            "Alan Wake's American Nightmare",
            "Alan Wake 2",
        };

        // ── Platform name normaliser for SSD Games sheet ─────────────────────────
        private static readonly Dictionary<string, string> SsdPlatformMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["PS5"] = "PlayStation 5",
                ["PS4"] = "PlayStation 4",
                ["Switch 2"] = "Nintendo Switch 2",
                ["Switch"] = "Nintendo Switch",
                ["PC"] = "PC Games",
                ["Live Service"] = "Live Service",
                ["Xbox Series X"] = "Xbox Series X",
                ["Xbox One"] = "Xbox One",
            };

        public ExcelImporter(VaultContext db, BoxArtService boxArtService)
        {
            _db = db;
            _boxArtService = boxArtService;
            ExcelPackage.License.SetNonCommercialPersonal("Vault");
        }

        // ────────────────────────────────────────────────────────────────────────
        // CleanupMismatchedGamesAsync
        // ────────────────────────────────────────────────────────────────────────
        public static async Task<int> CleanupMismatchedGamesAsync()
        {
            using var db = new VaultContext();

            var badGames = await db.Games
                .Where(g => !g.IsWishlist)
                .ToListAsync();

            var toRemove = badGames
                .Where(g => IsMediaSheet(g.Platform))
                .ToList();

            if (toRemove.Count == 0) return 0;

            db.Games.RemoveRange(toRemove);
            await db.SaveChangesAsync();
            return toRemove.Count;
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

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext != ".xlsx" && ext != ".xlsm")
            {
                result.Errors.Add($"Unsupported file format '{ext}'. Please convert to .xlsx first.");
                return result;
            }

            // Guard: refuse media spreadsheets
            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                var sheetNames = package.Workbook.Worksheets.Select(s => s.Name).ToList();
                bool hasOnlyMediaSheets = sheetNames.All(n =>
                    IsMediaSheet(n) || SkipSheets.Contains(n));

                if (hasOnlyMediaSheets)
                {
                    result.Errors.Add(
                        "This file appears to be a media library, not a games library. " +
                        "Use 'Import Media' instead.");
                    return result;
                }
            }

            await CleanupMismatchedGamesAsync();

            var candidates = CollectCandidates(filePath, result);

            foreach (var game in candidates)
            {
                if (_db.Games.Any(g => g.Title == game.Title && g.Platform == game.Platform))
                    continue;

                _db.Games.Add(game);
                result.GamesImported++;
            }

            await ImportWishlistAsync(filePath, result);
            await _db.SaveChangesAsync();
            return result;
        }

        // ────────────────────────────────────────────────────────────────────────
        // FetchMissingArtAsync
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
        // ImportMediaAsync
        // ────────────────────────────────────────────────────────────────────────
        public async Task<ImportResult> ImportMediaAsync(string filePath)
        {
            var result = new ImportResult();

            if (!File.Exists(filePath))
            {
                result.Errors.Add("File not found: " + filePath);
                return result;
            }

            using (var package = new ExcelPackage(new FileInfo(filePath)))
            {
                var sheetNames = package.Workbook.Worksheets.Select(s => s.Name).ToList();
                bool hasOnlyGameSheets = sheetNames.All(n =>
                    !IsMediaSheet(n) || SkipSheets.Contains(n));

                if (hasOnlyGameSheets)
                {
                    result.Errors.Add(
                        "This file appears to be a games library, not a media library. " +
                        "Use 'Import Games' instead.");
                    return result;
                }
            }

            var existing = await _db.MediaItems.ToListAsync();
            _db.MediaItems.RemoveRange(existing);
            await _db.SaveChangesAsync();

            using var pkg = new ExcelPackage(new FileInfo(filePath));

            foreach (var sheet in pkg.Workbook.Worksheets)
            {
                if (sheet.Name == "Summary" || sheet.Name == "Storage Estimate") continue;
                if (sheet.Dimension == null) continue;
                if (!IsMediaSheet(sheet.Name)) continue;

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

        private List<Game> CollectCandidates(string filePath, ImportResult result)
        {
            using var package = new ExcelPackage(new FileInfo(filePath));

            var final = new List<Game>();

            // ── Pass 1: SSD Games sheet — always wins over platform sheets ────────
            // Process this first so its (platform, title) keys populate `seen`.
            // Any game from another sheet that matches an SSD entry is skipped,
            // giving SSD data (which has HLTB times and cleaner metadata) priority.
            var seen = new HashSet<(string platform, string title)>(
                new PlatformTitleComparer());

            var ssdSheet = package.Workbook.Worksheets
                .FirstOrDefault(s => s.Name.Equals("SSD Games",
                    StringComparison.OrdinalIgnoreCase));

            if (ssdSheet?.Dimension != null)
            {
                for (int row = 2; row <= ssdSheet.Dimension.Rows; row++)
                {
                    string rawConsole = ssdSheet.Cells[row, 2].Text.Trim();
                    string title = ssdSheet.Cells[row, 3].Text.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    string platform = SsdPlatformMap.TryGetValue(rawConsole, out string? mapped)
                        ? mapped : rawConsole;

                    string genre = ssdSheet.Cells[row, 4].Text.Trim();

                    if (IsHorror(title, genre)) { result.HorrorSkipped++; continue; }

                    var key = (platform.ToLowerInvariant(), title.ToLowerInvariant());
                    if (seen.Contains(key)) { result.DuplicatesSkipped++; continue; }
                    seen.Add(key);

                    double? hltbMain = ParseDouble(ssdSheet.Cells[row, 8].Text);
                    double? hltbSides = ParseDouble(ssdSheet.Cells[row, 9].Text);
                    double? hltbComplete = ParseDouble(ssdSheet.Cells[row, 10].Text);
                    string? note = NullIfEmpty(ssdSheet.Cells[row, 7].Text.Trim());

                    final.Add(new Game
                    {
                        Title = title,
                        Platform = platform,
                        Year = ParseYear(ssdSheet.Cells[row, 5].Text),
                        FileSizeGB = ParseSize(ssdSheet.Cells[row, 6].Text),
                        Genre = genre,
                        Status = "Not Started",
                        LibraryType = "Owned",
                        IsWishlist = false,
                        IsDownloaded = false,
                        Notes = note,
                        HltbMain = hltbMain,
                        HltbMainSides = hltbSides,
                        HltbComplete = hltbComplete,
                    });
                }
            }

            // ── Pass 2: All other sheets ──────────────────────────────────────────
            // Skip any (platform, title) already added from SSD Games.
            foreach (var sheet in package.Workbook.Worksheets)
            {
                if (SkipSheets.Contains(sheet.Name)) continue;
                if (IsMediaSheet(sheet.Name)) continue;
                if (sheet.Name.Equals("SSD Games", StringComparison.OrdinalIgnoreCase)) continue;
                if (sheet.Dimension == null) continue;

                string sheetPlatform = sheet.Name;

                int titleCol = -1, yearCol = -1, sizeCol = -1, statusCol = -1,
                    genreCol = -1, platformCol = -1, noteCol = -1, emulatorCol = -1,
                    hltbMainCol = -1, hltbSidesCol = -1, hltbCompleteCol = -1;

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
                    else if (h == "emulator") emulatorCol = c;
                    else if (h.Contains("main story")) hltbMainCol = c;
                    else if (h.Contains("main + side") || h.Contains("main+side")) hltbSidesCol = c;
                    else if (h.Contains("completionist")) hltbCompleteCol = c;
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

                    string gamePlatform = sheetPlatform;

                    string genre = genreCol > 0
                        ? sheet.Cells[row, genreCol].Text.Trim() : "";

                    if (IsHorror(title, genre)) { result.HorrorSkipped++; continue; }

                    // Skip if SSD Games already added this (platform, title)
                    var key = (gamePlatform.ToLowerInvariant(), title.ToLowerInvariant());
                    if (seen.Contains(key)) { result.DuplicatesSkipped++; continue; }
                    seen.Add(key);

                    string rawStatus = statusCol > 0
                        ? sheet.Cells[row, statusCol].Text.Trim() : "";
                    string? notes = noteCol > 0
                        ? NullIfEmpty(sheet.Cells[row, noteCol].Text.Trim()) : null;
                    string? emulator = emulatorCol > 0
                        ? NullIfEmpty(sheet.Cells[row, emulatorCol].Text.Trim()) : null;
                    double? hltbMain = hltbMainCol > 0 ? ParseDouble(sheet.Cells[row, hltbMainCol].Text) : null;
                    double? hltbSides = hltbSidesCol > 0 ? ParseDouble(sheet.Cells[row, hltbSidesCol].Text) : null;
                    double? hltbComplete = hltbCompleteCol > 0 ? ParseDouble(sheet.Cells[row, hltbCompleteCol].Text) : null;

                    final.Add(new Game
                    {
                        Title = title,
                        Platform = gamePlatform,
                        Year = yearCol > 0 ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
                        FileSizeGB = sizeCol > 0 ? ParseSize(sheet.Cells[row, sizeCol].Text) : null,
                        Genre = genre,
                        Status = NormalizeStatus(rawStatus),
                        LibraryType = "Owned",
                        IsWishlist = false,
                        IsDownloaded = false,
                        Notes = notes,
                        Emulator = emulator,
                        HltbMain = hltbMain,
                        HltbMainSides = hltbSides,
                        HltbComplete = hltbComplete,
                    });
                }
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

        // ── Horror check — returns true if the game should be filtered out ───────
        // Exception list overrides both genre and title filters so that games
        // like Alan Wake (tagged "Survival Horror" in the sheet but not actually
        // horror) are always imported.
        private static bool IsHorror(string title, string genre)
        {
            if (HorrorExceptions.Contains(title)) return false;
            return (!string.IsNullOrEmpty(genre) && HorrorGenres.Contains(genre)) ||
                   HorrorTitles.Contains(title);
        }

        // ── Media sheet detector ─────────────────────────────────────────────────
        private static bool IsMediaSheet(string sheetName)
        {
            string s = sheetName.ToLower();
            return s.Contains("movie") ||
                   s.Contains("anime") ||
                   s.Contains("animated") ||
                   s.Contains("western") ||
                   s.Contains("show") ||
                   s.Contains("series") ||
                   s.Contains("tv");
        }

        private static string NormalizeStatus(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Not Started";
            return raw.Trim().ToLower() switch
            {
                "playing" or "in progress" => "Playing",
                "completed" or "complete" or "finished" => "Completed",
                "on hold" or "paused" => "On Hold",
                "dropped" => "Dropped",
                "released" => "Not Started",
                _ => "Not Started"
            };
        }

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

        private static double? ParseDouble(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (double.TryParse(text.Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double val))
                return val;
            return null;
        }

        private static int? ParseInt(string text)
        {
            if (int.TryParse(text.Trim(), out int v)) return v;
            return null;
        }

        private static string? NullIfEmpty(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // ── Helper: case-insensitive (platform, title) tuple comparer ────────────────
    internal class PlatformTitleComparer : IEqualityComparer<(string platform, string title)>
    {
        public bool Equals((string platform, string title) x, (string platform, string title) y)
            => string.Equals(x.platform, y.platform, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.title, y.title, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string platform, string title) obj)
            => HashCode.Combine(
                obj.platform?.ToLowerInvariant(),
                obj.title?.ToLowerInvariant());
    }
}