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
        public List<string> Errors { get; set; } = new();
    }

    public class ExcelImporter
    {
        private readonly VaultContext _db;

        private static readonly HashSet<string> CompleteLibrarySheets = new(StringComparer.OrdinalIgnoreCase)
        {
        };

        private static readonly HashSet<string> SkipSheets = new(StringComparer.OrdinalIgnoreCase)
        {
            "Summary", "SSD Games", "Storage Forecast", "Year-by-Year Timeline",
            "Assumptions", "Wishlist"
        };

        public ExcelImporter(VaultContext db)
        {
            _db = db;
            ExcelPackage.License.SetNonCommercialPersonal("Vault");
        }

        public async Task<ImportResult> ImportGamesAsync(string filePath)
        {
            var result = new ImportResult();
            if (!File.Exists(filePath))
            {
                result.Errors.Add("File not found: " + filePath);
                return result;
            }

            using var package = new ExcelPackage(new FileInfo(filePath));

            foreach (var sheet in package.Workbook.Worksheets)
            {
                if (SkipSheets.Contains(sheet.Name)) continue;
                if (sheet.Dimension == null) continue;

                string platform = sheet.Name;

                if (CompleteLibrarySheets.Contains(platform))
                {
                    if (_db.Games.Any(g => g.Platform == platform &&
                        g.LibraryType == "Complete Library"))
                        continue;

                    string sizeText = sheet.Cells[2, 4].Text.Trim();
                    var collectionGame = new Game
                    {
                        Title = $"{platform} — Complete Library",
                        Platform = platform,
                        LibraryType = "Complete Library",
                        FileSizeGB = ParseSize(sizeText),
                        Status = "Not Started",
                        IsWishlist = false,
                        IsDownloaded = false
                    };
                    _db.Games.Add(collectionGame);
                    result.GamesImported++;
                    continue;
                }

                int titleCol = -1, yearCol = -1, sizeCol = -1, statusCol = -1,
                    genreCol = -1, platformCol = -1, noteCol = -1;

                int headerRow = 1;
                for (int c = 1; c <= sheet.Dimension.Columns; c++)
                {
                    string h = sheet.Cells[headerRow, c].Text.Trim().ToLower();
                    if (h == "title" || h == "game") titleCol = c;
                    else if (h == "year") yearCol = c;
                    else if (h == "size" || h == "rom size" || h == "avg size (gb)") sizeCol = c;
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
                    if (string.IsNullOrWhiteSpace(gamePlatform)) gamePlatform = platform;

                    if (_db.Games.Any(g => g.Title == title && g.Platform == gamePlatform))
                        continue;

                    string rawStatus = statusCol > 0
                        ? sheet.Cells[row, statusCol].Text.Trim() : "";
                    string status = NormalizeStatus(rawStatus);

                    var game = new Game
                    {
                        Title = title,
                        Platform = gamePlatform,
                        Year = yearCol > 0
                            ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
                        FileSizeGB = sizeCol > 0
                            ? ParseSize(sheet.Cells[row, sizeCol].Text) : null,
                        Genre = genreCol > 0
                            ? sheet.Cells[row, genreCol].Text.Trim() : null,
                        Status = status,
                        LibraryType = "Owned",
                        IsWishlist = false,
                        IsDownloaded = false
                    };

                    _db.Games.Add(game);
                    result.GamesImported++;
                }
            }

            // Import Wishlist sheet
            var wishlistSheet = package.Workbook.Worksheets
                .FirstOrDefault(s => s.Name.ToLower() == "wishlist");
            if (wishlistSheet?.Dimension != null)
            {
                int titleCol = -1, platformCol = -1, sizeCol = -1, yearCol = -1;
                for (int c = 1; c <= wishlistSheet.Dimension.Columns; c++)
                {
                    string h = wishlistSheet.Cells[1, c].Text.Trim().ToLower();
                    if (h == "title" || h == "game") titleCol = c;
                    else if (h == "platform" || h == "console") platformCol = c;
                    else if (h == "size") sizeCol = c;
                    else if (h == "year") yearCol = c;
                }

                if (titleCol > 0)
                {
                    for (int row = 2; row <= wishlistSheet.Dimension.Rows; row++)
                    {
                        string title = wishlistSheet.Cells[row, titleCol].Text.Trim();
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        if (title.StartsWith("✅") || title.StartsWith("📥")) continue;

                        string plt = platformCol > 0
                            ? wishlistSheet.Cells[row, platformCol].Text.Trim()
                            : "Unknown";

                        if (_db.Games.Any(g => g.Title == title && g.IsWishlist))
                            continue;

                        _db.Games.Add(new Game
                        {
                            Title = title,
                            Platform = plt,
                            Year = yearCol > 0
                                ? ParseYear(wishlistSheet.Cells[row, yearCol].Text) : null,
                            FileSizeGB = sizeCol > 0
                                ? ParseSize(wishlistSheet.Cells[row, sizeCol].Text) : null,
                            Status = "Wishlist",
                            LibraryType = "Wishlist",
                            IsWishlist = true,
                            IsDownloaded = false
                        });
                        result.GamesImported++;
                    }
                }
            }

            await _db.SaveChangesAsync();
            return result;
        }

        public async Task<ImportResult> ImportMediaAsync(string filePath)
        {
            var result = new ImportResult();
            if (!File.Exists(filePath))
            {
                result.Errors.Add("File not found: " + filePath);
                return result;
            }

            // Wipe existing media before reimport
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

                    // Title is already clean in merged Excel — no stripping needed
                    string cleanTitle = rawTitle.Trim();

                    if (_db.MediaItems.Any(m => m.Title == cleanTitle &&
                        m.MediaType == mediaType))
                        continue;

                    int totalEpisodes = 0;
                    if (episodesCol > 0)
                        totalEpisodes = ParseInt(sheet.Cells[row, episodesCol].Text) ?? 0;

                    int? totalSeasons = null;
                    if (seasonsCol > 0)
                        totalSeasons = ParseInt(sheet.Cells[row, seasonsCol].Text);

                    _db.MediaItems.Add(new MediaItem
                    {
                        Title = cleanTitle,
                        MediaType = mediaType,
                        Year = yearCol > 0
                            ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
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

        private static string NormalizeStatus(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "Not Started";
            return raw.ToLower() switch
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
                : text.Contains("MB") ? 1.0 / 1024 : 1;
            string num = System.Text.RegularExpressions.Regex
                .Match(text, @"[\d.]+").Value;
            if (double.TryParse(num, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
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