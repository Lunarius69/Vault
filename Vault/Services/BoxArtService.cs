// Services/BoxArtService.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vault.Models;

namespace Vault.Services
{
    public class BoxArtService : IDisposable
    {
        // Use static HttpClient instances to avoid socket exhaustion
        private static readonly HttpClient _http = new HttpClient();
        private static readonly HttpClient _httpCdn = new HttpClient();
        private static readonly SemaphoreSlim _rateLimitGate = new SemaphoreSlim(1, 1);
        private static DateTime _rateLimitUntil = DateTime.MinValue;

        // Cache for in-memory results to avoid repeated lookups
        private static readonly ConcurrentDictionary<int, string?> _gameImageCache = new();

        private readonly string _apiKey;
        private readonly string _cacheFolder;
        private bool _disposed;

        static BoxArtService()
        {
            // Configure static clients once
            _http.Timeout = TimeSpan.FromSeconds(15);
            _httpCdn.Timeout = TimeSpan.FromSeconds(30);
        }

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
        }

        public async Task<string?> GetBoxArtAsync(Game game, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            // Check cache first
            if (_gameImageCache.TryGetValue(game.Id, out var cachedPathResult))
                return cachedPathResult;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_cacheFolder, $"{game.Id}_{safeName}.jpg");

            if (File.Exists(cachedPath))
            {
                _gameImageCache[game.Id] = cachedPath;
                return cachedPath;
            }

            int? gameId = await SearchGameAsync(game.Title, ct);
            if (gameId == null) return null;

            string? imageUrl = await GetGridImageUrlAsync(gameId.Value, ct);
            if (imageUrl == null) return null;

            string result = await DownloadImageAsync(imageUrl, cachedPath, ct);
            _gameImageCache[game.Id] = result;
            return result;
        }

        private async Task<int?> SearchGameAsync(string title, CancellationToken ct)
        {
            var variants = new System.Collections.Generic.List<string> { title };

            if (title.Contains(" - ") || title.Contains(" – "))
            {
                string s = System.Text.RegularExpressions.Regex
                    .Replace(title, @"\s*[-–].*$", "").Trim();
                if (!variants.Contains(s)) variants.Add(s);
            }

            if (title.Contains(":"))
            {
                string s = title.Split(':')[0].Trim();
                if (!variants.Contains(s)) variants.Add(s);
            }

            string cleaned = title
                .Replace("'", "").Replace("\u2019", "")
                .Replace(":", "").Replace("–", "")
                .Replace("-", " ").Replace("  ", " ").Trim();
            if (!variants.Contains(cleaned)) variants.Add(cleaned);

            string noNums = System.Text.RegularExpressions.Regex
                .Replace(title,
                    @"\s+(II|III|IV|VI|VII|VIII|IX|XI|XII|2|3|4|5|6|7|8|9)$",
                    "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Trim();
            if (!variants.Contains(noNums) && noNums != title)
                variants.Add(noNums);

            foreach (string variant in variants)
            {
                ct.ThrowIfCancellationRequested();
                int? result = await TrySearchAsync(variant, ct);
                if (result.HasValue) return result;
            }

            return null;
        }

        private async Task<int?> TrySearchAsync(string title, CancellationToken ct)
        {
            await WaitIfRateLimitedAsync(ct);

            try
            {
                string encoded = Uri.EscapeDataString(title);
                string url = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{encoded}";

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                    var response = await _http.SendAsync(request, ct);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await HandleRateLimitAsync(response, ct);
                        return null;
                    }

                    if (!response.IsSuccessStatusCode) return null;

                    string json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;

                    var data = doc.RootElement.GetProperty("data");
                    if (data.GetArrayLength() == 0) return null;

                    return data[0].GetProperty("id").GetInt32();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<string?> GetGridImageUrlAsync(int gameId, CancellationToken ct)
        {
            await WaitIfRateLimitedAsync(ct);
            try
            {
                string url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}" +
                             $"?dimensions=600x900";

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                    var response = await _http.SendAsync(request, ct);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await HandleRateLimitAsync(response, ct);
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // Retry without dimension filter
                        url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}";
                        using (var retryRequest = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            retryRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
                            response = await _http.SendAsync(retryRequest, ct);
                        }
                        if (!response.IsSuccessStatusCode) return null;
                    }

                    string json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;
                    var data = doc.RootElement.GetProperty("data");
                    if (data.GetArrayLength() == 0) return null;
                    return data[0].GetProperty("url").GetString();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<string> DownloadImageAsync(string imageUrl, string destPath, CancellationToken ct)
        {
            try
            {
                // Use the CDN client without auth headers
                var imageData = await _httpCdn.GetByteArrayAsync(imageUrl, ct);
                await File.WriteAllBytesAsync(destPath, imageData, ct);
                return destPath;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Return path even if download failed partially
                return destPath ?? string.Empty;
            }
        }

        private static async Task HandleRateLimitAsync(HttpResponseMessage response, CancellationToken ct)
        {
            int waitSeconds = 60;
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                waitSeconds = (int)response.Headers.RetryAfter.Delta!.Value.TotalSeconds + 1;

            _rateLimitUntil = DateTime.UtcNow.AddSeconds(waitSeconds);
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
        }

        private static async Task WaitIfRateLimitedAsync(CancellationToken ct)
        {
            var remaining = _rateLimitUntil - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, ct);
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 50 ? name[..50] : name;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Don't dispose static HttpClients - they're shared
        }
    }
}