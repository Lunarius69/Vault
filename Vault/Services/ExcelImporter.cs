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

        public ExcelImporter(VaultContext db)
        {
            _db = db;
            ExcelPackage.License.SetNonCommercialPersonal("Vault");
        }

        // ─── GAMES ───────────────────────────────────────────────────────────

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
                // Skip non-game sheets
                if (sheet.Name == "Summary" || sheet.Name == "Storage Forecast" ||
                    sheet.Name == "Year-by-Year Timeline" || sheet.Name == "Assumptions" ||
                    sheet.Name == "Wishlist")
                    continue;

                string platform = sheet.Name;
                int rows = sheet.Dimension?.Rows ?? 0;
                if (rows < 2) continue;

                // Detect columns by header
                int titleCol = -1, yearCol = -1, sizeCol = -1, statusCol = -1, genreCol = -1;
                for (int c = 1; c <= sheet.Dimension.Columns; c++)
                {
                    string header = sheet.Cells[1, c].Text.Trim().ToLower();
                    if (header.Contains("title") || header.Contains("game")) titleCol = c;
                    else if (header.Contains("year")) yearCol = c;
                    else if (header.Contains("size")) sizeCol = c;
                    else if (header.Contains("status")) statusCol = c;
                    else if (header.Contains("genre")) genreCol = c;
                }

                if (titleCol == -1) continue;

                for (int row = 2; row <= rows; row++)
                {
                    string title = sheet.Cells[row, titleCol].Text.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    // Skip if already imported
                    if (_db.Games.Any(g => g.Title == title && g.Platform == platform))
                        continue;

                    var game = new Game
                    {
                        Title = title,
                        Platform = platform,
                        Year = yearCol > 0 ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
                        Genre = genreCol > 0 ? sheet.Cells[row, genreCol].Text.Trim() : null,
                        Status = statusCol > 0 ? sheet.Cells[row, statusCol].Text.Trim() : "Not Started",
                        IsWishlist = false,
                        IsDownloaded = false,
                    };

                    _db.Games.Add(game);
                    result.GamesImported++;
                }
            }

            // Import Wishlist sheet if present
            var wishlistSheet = package.Workbook.Worksheets
                .FirstOrDefault(s => s.Name.ToLower().Contains("wishlist"));
            if (wishlistSheet != null)
            {
                int rows = wishlistSheet.Dimension?.Rows ?? 0;
                int titleCol = -1, platformCol = -1, sizeCol = -1, yearCol = -1;
                for (int c = 1; c <= wishlistSheet.Dimension.Columns; c++)
                {
                    string header = wishlistSheet.Cells[1, c].Text.Trim().ToLower();
                    if (header.Contains("title") || header.Contains("game")) titleCol = c;
                    else if (header.Contains("platform") || header.Contains("console")) platformCol = c;
                    else if (header.Contains("size")) sizeCol = c;
                    else if (header.Contains("year")) yearCol = c;
                }

                if (titleCol > 0)
                {
                    for (int row = 2; row <= rows; row++)
                    {
                        string title = wishlistSheet.Cells[row, titleCol].Text.Trim();
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        string platform = platformCol > 0 ? wishlistSheet.Cells[row, platformCol].Text.Trim() : "Unknown";

                        if (_db.Games.Any(g => g.Title == title && g.IsWishlist)) continue;

                        var game = new Game
                        {
                            Title = title,
                            Platform = platform,
                            Year = yearCol > 0 ? ParseYear(wishlistSheet.Cells[row, yearCol].Text) : null,
                            Status = "Wishlist",
                            IsWishlist = true,
                        };

                        _db.Games.Add(game);
                        result.GamesImported++;
                    }
                }
            }

            await _db.SaveChangesAsync();
            return result;
        }

        // ─── MEDIA ───────────────────────────────────────────────────────────

        public async Task<ImportResult> ImportMediaAsync(string filePath)
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
                if (sheet.Name == "Summary" || sheet.Name == "Storage Estimate") continue;
                if (sheet.Dimension == null) continue;

                string mediaType = DetectMediaType(sheet.Name);
                bool isSeries = mediaType == "Show" || mediaType == "Anime" || mediaType == "AnimatedSeries";

                int titleCol = -1, yearCol = -1, sizeCol = -1, episodesCol = -1, seasonsCol = -1;
                for (int c = 1; c <= sheet.Dimension.Columns; c++)
                {
                    string header = sheet.Cells[1, c].Text.Trim().ToLower();
                    if (header.Contains("title")) titleCol = c;
                    else if (header.Contains("year") || header.Contains("release")) yearCol = c;
                    else if (header.Contains("size")) sizeCol = c;
                    else if (header.Contains("episode")) episodesCol = c;
                    else if (header.Contains("season")) seasonsCol = c;
                }

                if (titleCol == -1) continue;

                int rows = sheet.Dimension.Rows;
                for (int row = 2; row <= rows; row++)
                {
                    string rawTitle = sheet.Cells[row, titleCol].Text.Trim();
                    if (string.IsNullOrWhiteSpace(rawTitle)) continue;

                    // Clean title and extract note
                    var (cleanTitle, note) = CleanTitle(rawTitle);

                    if (_db.MediaItems.Any(m => m.Title == cleanTitle && m.MediaType == mediaType))
                        continue;

                    var item = new MediaItem
                    {
                        Title = cleanTitle,
                        MediaType = mediaType,
                        Year = yearCol > 0 ? ParseYear(sheet.Cells[row, yearCol].Text) : null,
                        TotalEpisodes = episodesCol > 0 ? (ParseInt(sheet.Cells[row, episodesCol].Text) ?? 0) : 0,
                        WatchStatus = "Not Started",
                    };

                    _db.MediaItems.Add(item);
                    result.MediaImported++;
                }
            }

            await _db.SaveChangesAsync();
            return result;
        }

        // ─── HELPERS ─────────────────────────────────────────────────────────

        private static string DetectMediaType(string sheetName)
        {
            string s = sheetName.ToLower();
            if (s.Contains("anime") && s.Contains("movie")) return "AnimeMovie";
            if (s.Contains("anime")) return "Anime";
            if (s.Contains("animated") && s.Contains("movie")) return "AnimatedMovie";
            if (s.Contains("animated") || s.Contains("western")) return "AnimatedSeries";
            if (s.Contains("movie")) return "Movie";
            if (s.Contains("show") || s.Contains("tv")) return "Show";
            return "Movie";
        }

        private static (string cleanTitle, string? note) CleanTitle(string raw)
        {
            string note = null;
            string title = raw;

            // Extract dash notes: "Doctor Who – David Tennant Era (Series 2–4)"
            int dashIdx = title.IndexOf(" \u2013 ");
            if (dashIdx == -1) dashIdx = title.IndexOf(" - ");
            if (dashIdx > 0)
            {
                note = title.Substring(dashIdx).Trim(' ', '-', '\u2013').Trim();
                title = title.Substring(0, dashIdx).Trim();
            }

            // Extract parenthetical notes but keep country hints like (US)
            int parenIdx = title.IndexOf(" (");
            if (parenIdx > 0)
            {
                string paren = title.Substring(parenIdx + 2).TrimEnd(')');
                // Keep short country/year codes attached, move longer phrases to note
                if (paren.Length > 5 && !paren.All(char.IsDigit))
                {
                    note = (note != null ? note + " " : "") + paren.Trim();
                    title = title.Substring(0, parenIdx).Trim();
                }
            }

            return (title.Trim(), string.IsNullOrEmpty(note) ? null : note.Trim());
        }

        private static int? ParseYear(string text)
        {
            if (int.TryParse(text.Trim(), out int y) && y > 1900 && y < 2100) return y;
            return null;
        }

        private static double? ParseSize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Replace(",", ".").ToUpper();
            double multiplier = 1;
            if (text.Contains("TB")) multiplier = 1024;
            else if (text.Contains("GB")) multiplier = 1;
            else if (text.Contains("MB")) multiplier = 1.0 / 1024;
            string num = System.Text.RegularExpressions.Regex.Match(text, @"[\d.]+").Value;
            if (double.TryParse(num, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
                return Math.Round(val * multiplier, 2);
            return null;
        }

        private static int? ParseInt(string text)
        {
            if (int.TryParse(text.Trim(), out int v)) return v;
            return null;
        }
    }
}
