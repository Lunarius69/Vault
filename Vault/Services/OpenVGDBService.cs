using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Vault.Models;

namespace Vault.Services
{
    public class OpenVGDBService
    {
        private readonly string _dbPath;
        private readonly string _cacheFolder;
        private readonly HttpClient _http = new();

        // Map your platform names to OpenVGDB system names
        private static readonly System.Collections.Generic.Dictionary<string, string[]>
            PlatformMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "NES", new[] { "Nintendo Entertainment System (NES)" } },
            { "SNES", new[] { "Super Nintendo Entertainment System (SNES)" } },
            { "Nintendo 64", new[] { "Nintendo 64" } },
            { "GameCube", new[] { "Nintendo GameCube" } },
            { "Wii", new[] { "Nintendo Wii" } },
            { "Wii U", new[] { "Nintendo Wii U" } },
            { "Nintendo Switch", new[] { "Nintendo Switch" } },
            { "Game Boy & GBC", new[] { "Game Boy", "Game Boy Color" } },
            { "Game Boy Advance", new[] { "Game Boy Advance" } },
            { "Nintendo DS", new[] { "Nintendo DS" } },
            { "Nintendo 3DS", new[] { "Nintendo 3DS" } },
            { "PlayStation 1", new[] { "Sony PlayStation" } },
            { "PlayStation 2", new[] { "Sony PlayStation 2" } },
            { "PlayStation 3", new[] { "Sony PlayStation 3" } },
            { "PlayStation 4", new[] { "Sony PlayStation 4" } },
            { "PlayStation 5", new[] { "Sony PlayStation 5" } },
            { "PSP", new[] { "Sony PlayStation Portable" } },
            { "PS Vita", new[] { "Sony PlayStation Vita" } },
            { "Xbox Original", new[] { "Microsoft Xbox" } },
            { "Xbox 360", new[] { "Microsoft Xbox 360" } },
            { "Xbox One", new[] { "Microsoft Xbox One" } },
            { "Sega Genesis", new[] { "Sega Genesis" } },
            { "Sega Master System", new[] { "Sega Master System" } },
            { "Sega Saturn", new[] { "Sega Saturn" } },
            { "Sega Dreamcast", new[] { "Sega Dreamcast" } },
            { "Atari 2600", new[] { "Atari 2600" } },
        };

        public OpenVGDBService(AppSettings settings)
        {
            _dbPath = settings.OpenVGDBPath ?? "";
            _cacheFolder = Path.Combine(
                string.IsNullOrEmpty(settings.DataFolderPath)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Vault")
                    : settings.DataFolderPath,
                "cache", "boxart");
            Directory.CreateDirectory(_cacheFolder);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public bool IsConfigured => !string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath);

        public async Task<string?> GetBoxArtAsync(Game game)
        {
            if (!IsConfigured) return null;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_cacheFolder,
                $"{game.Id}_{safeName}_vgdb.jpg");
            if (File.Exists(cachedPath)) return cachedPath;

            try
            {
                string? imageUrl = await QueryDatabaseAsync(game);
                if (imageUrl == null) return null;

                byte[] data = await _http.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(cachedPath, data);
                return cachedPath;
            }
            catch { return null; }
        }

        private Task<string?> QueryDatabaseAsync(Game game)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
                    conn.Open();

                    // Get system IDs for this platform
                    string[]? systemNames = null;
                    PlatformMap.TryGetValue(game.Platform, out systemNames);

                    string systemFilter = "";
                    if (systemNames != null && systemNames.Length > 0)
                    {
                        var quoted = string.Join(",",
                            Array.ConvertAll(systemNames, s => $"'{s.Replace("'", "''")}'"));
                        systemFilter = $@"AND s.systemName IN ({quoted})";
                    }

                    // Search by title with fuzzy matching
                    string[] titleVariants = GetTitleVariants(game.Title);

                    foreach (string variant in titleVariants)
                    {
                        string sql = $@"
                            SELECT r.releaseCoverFront
                            FROM RELEASES r
                            JOIN SYSTEMS s ON r.systemID = s.systemID
                            WHERE r.releaseCoverFront IS NOT NULL
                            AND r.releaseCoverFront != ''
                            {systemFilter}
                            AND (
                                LOWER(r.releaseTitleName) = LOWER(@title)
                                OR LOWER(r.TEMPRomFileName) LIKE LOWER(@titleLike)
                            )
                            LIMIT 1";

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = sql;
                        cmd.Parameters.AddWithValue("@title", variant);
                        cmd.Parameters.AddWithValue("@titleLike", $"%{variant}%");

                        var result = cmd.ExecuteScalar();
                        if (result is string url && !string.IsNullOrEmpty(url))
                            return url;
                    }

                    return null;
                }
                catch { return null; }
            });
        }

        private static string[] GetTitleVariants(string title)
        {
            var variants = new System.Collections.Generic.List<string> { title };

            // Without subtitle after colon
            if (title.Contains(":"))
                variants.Add(title.Split(':')[0].Trim());

            // Without content after dash
            if (title.Contains(" - "))
                variants.Add(title.Split(new[] { " - " }, StringSplitOptions.None)[0].Trim());

            // Without apostrophes
            string noApostrophe = title.Replace("'", "").Replace("\u2019", "").Trim();
            if (noApostrophe != title) variants.Add(noApostrophe);

            // Without year in parentheses
            string noYear = System.Text.RegularExpressions.Regex
                .Replace(title, @"\s*\(\d{4}\)\s*$", "").Trim();
            if (noYear != title) variants.Add(noYear);

            return variants.ToArray();
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 50 ? name[..50] : name;
        }
    }
}