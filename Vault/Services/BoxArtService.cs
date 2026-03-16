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
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vault")
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

        // New: fetches wide hero/banner image (1920x620 style) from SteamGridDB
        public async Task<string?> GetHeroAsync(Game game)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_cacheFolder, $"{game.Id}_{safeName}_hero.jpg");
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

        private async Task<string?> GetGridImageUrlAsync(int gameId)
        {
            string url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}?dimensions=600x900";
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