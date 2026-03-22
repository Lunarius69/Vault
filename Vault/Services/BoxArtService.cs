using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Vault.Models;

namespace Vault.Services
{
    public class BoxArtService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _cacheFolder;

        public BoxArtService(AppSettings settings)
        {
            _apiKey = settings.SteamGridDbApiKey;
            _cacheFolder = Path.Combine(
                string.IsNullOrEmpty(settings.DataFolderPath)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Vault")
                    : settings.DataFolderPath,
                "cache", "boxart");

            Directory.CreateDirectory(_cacheFolder);

            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<string?> GetBoxArtAsync(Game game)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_cacheFolder, $"{game.Id}_{safeName}.jpg");
            if (File.Exists(cachedPath)) return cachedPath;

            try
            {
                int? gameId = await SearchGameAsync(game.Title);
                if (gameId == null) return null;

                string? imageUrl = await GetGridImageUrlAsync(gameId.Value);
                if (imageUrl == null) return null;

                byte[] imageData = await _http.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(cachedPath, imageData);
                return cachedPath;
            }
            catch { return null; }
        }

        public async Task<string?> GetHeroAsync(Game game)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_cacheFolder,
                $"{game.Id}_{safeName}_hero.jpg");
            if (File.Exists(cachedPath)) return cachedPath;

            try
            {
                int? gameId = await SearchGameAsync(game.Title);
                if (gameId == null) return null;

                string? imageUrl = await GetHeroImageUrlAsync(gameId.Value);
                if (imageUrl == null) return null;

                byte[] imageData = await _http.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(cachedPath, imageData);
                return cachedPath;
            }
            catch { return null; }
        }

        private async Task<int?> SearchGameAsync(string title)
        {
            // Build a list of search variants to try in order
            var variants = new System.Collections.Generic.List<string> { title };

            // Remove content after dash/em-dash
            if (title.Contains(" - ") || title.Contains(" – "))
            {
                string short1 = System.Text.RegularExpressions.Regex
                    .Replace(title, @"\s*[-–].*$", "").Trim();
                if (!variants.Contains(short1)) variants.Add(short1);
            }

            // Remove subtitle after colon
            if (title.Contains(":"))
            {
                string short2 = title.Split(':')[0].Trim();
                if (!variants.Contains(short2)) variants.Add(short2);
            }

            // Remove special characters version
            string cleaned = title
                .Replace("'", "")
                .Replace("'", "")
                .Replace(":", "")
                .Replace("–", "")
                .Replace("-", " ")
                .Replace("  ", " ")
                .Trim();
            if (!variants.Contains(cleaned)) variants.Add(cleaned);

            // Remove Roman numerals and numbers at end
            string noNums = System.Text.RegularExpressions.Regex
                .Replace(title, @"\s+(II|III|IV|VI|VII|VIII|IX|XI|XII|2|3|4|5|6|7|8|9)$",
                    "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Trim();
            if (!variants.Contains(noNums) && noNums != title)
                variants.Add(noNums);

            foreach (string variant in variants)
            {
                int? result = await TrySearchAsync(variant);
                if (result.HasValue) return result;
            }

            return null;
        }

        private async Task<int?> TrySearchAsync(string title)
        {
            try
            {
                string encoded = Uri.EscapeDataString(title);
                string url = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{encoded}";

                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;

                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;

                return data[0].GetProperty("id").GetInt32();
            }
            catch { return null; }
        }

        private async Task<string?> GetGridImageUrlAsync(int gameId)
        {
            string url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}" +
                         $"?dimensions=600x900";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}";
                response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            return data[0].GetProperty("url").GetString();
        }

        private async Task<string?> GetHeroImageUrlAsync(int gameId)
        {
            string url = $"https://www.steamgriddb.com/api/v2/heroes/game/{gameId}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0) return null;
            return data[0].GetProperty("url").GetString();
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 50 ? name[..50] : name;
        }
    }
}