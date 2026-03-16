using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vault.Models;

namespace Vault.Services
{
    public class HltbService
    {
        private static readonly HttpClient _http = new HttpClient();

        static HltbService()
        {
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            _http.DefaultRequestHeaders.Referrer = new Uri("https://howlongtobeat.com/");
        }

        public async Task<(double? Main, double? Sides, double? Complete)> FetchAsync(string title)
        {
            try
            {
                // Step 1: get the search API key from the HLTB homepage script
                string apiKey = await GetApiKeyAsync();
                if (string.IsNullOrEmpty(apiKey)) return (null, null, null);

                // Step 2: search for the game
                var payload = new
                {
                    searchType = "games",
                    searchTerms = title.Split(' '),
                    searchPage = 1,
                    size = 5,
                    searchOptions = new
                    {
                        games = new
                        {
                            userId = 0,
                            platform = "",
                            sortCategory = "popular",
                            rangeCategory = "main",
                            rangeTime = new { min = 0, max = 0 },
                            gameplay = new { perspective = "", flow = "", genre = "" },
                            modifier = ""
                        },
                        filter = "",
                        sort = 0,
                        randomizer = 0
                    }
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(
                    $"https://howlongtobeat.com/api/search/{apiKey}", content);

                if (!response.IsSuccessStatusCode) return (null, null, null);

                string body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);

                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return (null, null, null);

                var first = data[0];

                double? main = GetHours(first, "comp_main");
                double? sides = GetHours(first, "comp_plus");
                double? complete = GetHours(first, "comp_100");

                return (main, sides, complete);
            }
            catch
            {
                return (null, null, null);
            }
        }

        private async Task<string> GetApiKeyAsync()
        {
            try
            {
                string html = await _http.GetStringAsync("https://howlongtobeat.com");
                // Find the search API key embedded in the page scripts
                var match = Regex.Match(html,
                    @"/api/search/([a-zA-Z0-9]+)");
                return match.Success ? match.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        private static double? GetHours(JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var prop)) return null;
            if (prop.ValueKind == JsonValueKind.Number)
            {
                double seconds = prop.GetDouble();
                return seconds > 0 ? Math.Round(seconds / 3600.0, 1) : null;
            }
            return null;
        }
    }
}