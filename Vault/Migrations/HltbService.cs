// Services/HltbService.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Vault.Services
{
    public class HltbService
    {
        private static readonly HttpClient _http = new();

        public HltbService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Referer", "https://howlongtobeat.com/");
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "Origin", "https://howlongtobeat.com");
        }

        public async Task<HltbGameData?> FetchGameDataAsync(string title)
        {
            try
            {
                // Step 1: Get the search token from the HLTB homepage JS
                string? searchToken = await GetSearchTokenAsync();
                if (searchToken == null)
                {
                    System.Diagnostics.Debug.WriteLine("HLTB: could not find search token");
                    return null;
                }

                // Step 2: POST to the API endpoint with JSON body
                var payload = new
                {
                    searchType = "games",
                    searchTerms = title.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    searchPage = 1,
                    size = 20,
                    searchOptions = new
                    {
                        games = new
                        {
                            userId = 0,
                            platform = "",
                            sortCategory = "popular",
                            rangeCategory = "main",
                            rangeTime = new { min = (int?)null, max = (int?)null },
                            gameplay = new { perspective = "", flow = "", genre = "", difficulty = "" },
                            rangeYear = new { min = "", max = "" },
                            modifier = ""
                        },
                        users = new { sortCategory = "postcount" },
                        lists = new { sortCategory = "follows" },
                        filter = "",
                        sort = 0,
                        randomizer = 0
                    },
                    useCache = true
                };

                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string url = $"https://howlongtobeat.com/api/search/{searchToken}";
                var response = await _http.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"HLTB fetch failed for '{title}': {response.StatusCode}");
                    return null;
                }

                string body = await response.Content.ReadAsStringAsync();
                return ParseResponse(body);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HLTB fetch failed for '{title}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Scrapes the HLTB homepage JS bundle to find the dynamic search API token.
        /// HLTB rotates this token periodically — fetching it fresh every time keeps things working.
        /// </summary>
        private static async Task<string?> GetSearchTokenAsync()
        {
            try
            {
                string html = await _http.GetStringAsync("https://howlongtobeat.com/");

                // Find the _next/static JS chunk that contains the API path
                var scriptMatches = Regex.Matches(html,
                    @"/_next/static/chunks/([^""]+\.js)");

                foreach (Match m in scriptMatches)
                {
                    string jsUrl = "https://howlongtobeat.com" + m.Value;
                    try
                    {
                        string js = await _http.GetStringAsync(jsUrl);

                        // The token appears as: /api/search/xxxxxxxxxxxxxxxx
                        var tokenMatch = Regex.Match(js,
                            @"/api/search/([a-zA-Z0-9]+)",
                            RegexOptions.IgnoreCase);

                        if (tokenMatch.Success)
                            return tokenMatch.Groups[1].Value;
                    }
                    catch { /* skip this chunk, try next */ }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HLTB token fetch failed: {ex.Message}");
                return null;
            }
        }

        private static HltbGameData? ParseResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data) ||
                    data.GetArrayLength() == 0)
                    return null;

                // Take the first (best match) result
                var game = data[0];

                var result = new HltbGameData
                {
                    MainStory = GetHours(game, "comp_main"),
                    MainPlusExtra = GetHours(game, "comp_plus"),
                    Completionist = GetHours(game, "comp_100"),
                    Description = GetStringProperty(game, "description"),
                    Genre = GetStringProperty(game, "genre")
                };

                // Try to get a better description if available
                if (string.IsNullOrEmpty(result.Description))
                {
                    result.Description = GetStringProperty(game, "short_description");
                }

                // If still no description, try to get from profile page
                if (string.IsNullOrEmpty(result.Description))
                {
                    result.Description = GetStringProperty(game, "profile_description");
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HLTB parse failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// HLTB stores times in seconds. Convert to hours and round to 1 decimal.
        /// </summary>
        private static double? GetHours(JsonElement game, string property)
        {
            if (!game.TryGetProperty(property, out var val)) return null;
            double seconds = val.ValueKind == JsonValueKind.Number ? val.GetDouble() : 0;
            if (seconds <= 0) return null;
            return Math.Round(seconds / 3600.0, 1);
        }

        private static string? GetStringProperty(JsonElement game, string property)
        {
            if (!game.TryGetProperty(property, out var val)) return null;
            if (val.ValueKind != JsonValueKind.String) return null;
            string str = val.GetString() ?? "";
            return string.IsNullOrWhiteSpace(str) ? null : str.Trim();
        }
    }

    public class HltbGameData
    {
        public double? MainStory { get; set; }
        public double? MainPlusExtra { get; set; }
        public double? Completionist { get; set; }
        public string? Description { get; set; }
        public string? Genre { get; set; }
    }
}