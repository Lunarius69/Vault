using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Vault.Models;

namespace Vault.Services
{
    public class RetroAchievementsService
    {
        private readonly HttpClient _http = new HttpClient();
        private readonly string _user;
        private readonly string _apiKey;

        public RetroAchievementsService(AppSettings settings)
        {
            _user = settings.RetroAchievementsUser;
            _apiKey = settings.RetroAchievementsApiKey;
        }

        public bool IsConfigured =>
            !string.IsNullOrEmpty(_user) && !string.IsNullOrEmpty(_apiKey);

        // Search for a game ID on RetroAchievements by name
        public async Task<int?> FindGameIdAsync(string title, string platform)
        {
            try
            {
                string consoleId = GetConsoleId(platform);
                if (consoleId == "0") return null;

                string url = $"https://retroachievements.org/API/API_GetGameList.php" +
                             $"?z={_user}&y={_apiKey}&i={consoleId}&h=1";

                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                string titleLower = title.ToLower();
                foreach (var game in doc.RootElement.EnumerateArray())
                {
                    if (!game.TryGetProperty("Title", out var t)) continue;
                    if (t.GetString()?.ToLower().Contains(titleLower) == true)
                    {
                        if (game.TryGetProperty("ID", out var id))
                            return id.GetInt32();
                    }
                }
                return null;
            }
            catch { return null; }
        }

        // Get earned vs total achievements for a specific game
        public async Task<(int Earned, int Total)?> GetAchievementsAsync(int raGameId)
        {
            try
            {
                string url = $"https://retroachievements.org/API/API_GetGameInfoAndUserProgress.php" +
                             $"?z={_user}&y={_apiKey}&u={_user}&g={raGameId}";

                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                int total = 0, earned = 0;

                if (root.TryGetProperty("Achievements", out var achievements))
                {
                    foreach (var ach in achievements.EnumerateObject())
                    {
                        total++;
                        if (ach.Value.TryGetProperty("DateEarned", out var date) &&
                            date.ValueKind != JsonValueKind.Null &&
                            !string.IsNullOrEmpty(date.GetString()))
                            earned++;
                    }
                }

                return (earned, total);
            }
            catch { return null; }
        }

        private static string GetConsoleId(string platform)
        {
            return platform?.ToLower() switch
            {
                var p when p.Contains("ps1") || p.Contains("playstation 1") => "12",
                var p when p.Contains("ps2") || p.Contains("playstation 2") => "21",
                var p when p.Contains("psp") => "41",
                var p when p.Contains("gamecube") => "16",
                var p when p.Contains("wii u") => "53",
                var p when p.Contains("wii") => "45",
                var p when p.Contains("nintendo 64") || p.Contains("n64") => "2",
                var p when p.Contains("snes") || p.Contains("super nintendo") => "3",
                var p when p.Contains("nes") => "7",
                var p when p.Contains("game boy advance") || p.Contains("gba") => "5",
                var p when p.Contains("game boy color") || p.Contains("gbc") => "6",
                var p when p.Contains("game boy") => "4",
                var p when p.Contains("ds") => "18",
                var p when p.Contains("3ds") => "2",
                var p when p.Contains("xbox 360") => "69",
                var p when p.Contains("dreamcast") => "49",
                var p when p.Contains("saturn") => "39",
                var p when p.Contains("mega drive") || p.Contains("genesis") => "1",
                _ => "0"
            };
        }
    }
}