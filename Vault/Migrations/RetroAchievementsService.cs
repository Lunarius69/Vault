using System;
using System.Collections.Generic;
using System.Linq;
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

        // ── Game ID lookup ────────────────────────────────────────────────────────

        /// <summary>
        /// Searches RA's game list for the given console and returns the best
        /// matching game ID, or null if nothing found.
        /// </summary>
        public async Task<int?> SearchGameIdAsync(string title, string platform)
        {
            try
            {
                string consoleId = GetConsoleId(platform);
                if (consoleId == "0") return null;

                string url = $"https://retroachievements.org/API/API_GetGameList.php" +
                             $"?z={_user}&y={_apiKey}&i={consoleId}&h=1";

                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                string titleLower = title.ToLower().Trim();

                // First pass: exact match
                foreach (var game in doc.RootElement.EnumerateArray())
                {
                    if (!game.TryGetProperty("Title", out var t)) continue;
                    if (t.GetString()?.ToLower().Trim() == titleLower)
                    {
                        if (game.TryGetProperty("ID", out var id))
                            return id.GetInt32();
                    }
                }

                // Second pass: contains match
                foreach (var game in doc.RootElement.EnumerateArray())
                {
                    if (!game.TryGetProperty("Title", out var t)) continue;
                    string? name = t.GetString()?.ToLower().Trim();
                    if (name == null) continue;
                    if (name.Contains(titleLower) || titleLower.Contains(name))
                    {
                        if (game.TryGetProperty("ID", out var id))
                            return id.GetInt32();
                    }
                }

                return null;
            }
            catch { return null; }
        }

        // ── Achievement list ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns the full achievement list with unlock state for the given RA game,
        /// or null on failure.
        /// </summary>
        public async Task<List<Achievement>?> GetAchievementsAsync(int raGameId)
        {
            try
            {
                string url = $"https://retroachievements.org/API/API_GetGameInfoAndUserProgress.php" +
                             $"?z={_user}&y={_apiKey}&u={_user}&g={raGameId}";

                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("Achievements", out var achievements))
                    return null;

                var list = new List<Achievement>();

                foreach (var ach in achievements.EnumerateObject())
                {
                    var val = ach.Value;

                    string title = val.TryGetProperty("Title", out var tt) ? tt.GetString() ?? "" : "";
                    string desc = val.TryGetProperty("Description", out var dd) ? dd.GetString() ?? "" : "";
                    string? earned = val.TryGetProperty("DateEarned", out var de)
                                     && de.ValueKind != JsonValueKind.Null
                                     ? de.GetString() : null;

                    bool isUnlocked = !string.IsNullOrEmpty(earned);

                    DateTime? unlockedAt = null;
                    if (isUnlocked && DateTime.TryParse(earned, out var dt))
                        unlockedAt = dt;

                    list.Add(new Achievement
                    {
                        ApiName = ach.Name,
                        DisplayName = title,
                        Description = desc,
                        IsUnlocked = isUnlocked,
                        UnlockedAt = unlockedAt
                    });
                }

                if (list.Count == 0) return null;

                // Unlocked first, then alphabetically — same ordering as Steam path
                return list
                    .OrderByDescending(a => a.IsUnlocked)
                    .ThenBy(a => a.DisplayName)
                    .ToList();
            }
            catch { return null; }
        }

        // ── Console ID map ────────────────────────────────────────────────────────

        private static string GetConsoleId(string platform)
        {
            return platform?.ToLower() switch
            {
                var p when p != null && (p.Contains("ps1") || p.Contains("playstation 1")) => "12",
                var p when p != null && (p.Contains("ps2") || p.Contains("playstation 2")) => "21",
                var p when p != null && p.Contains("psp") => "41",
                var p when p != null && p.Contains("gamecube") => "16",
                var p when p != null && p.Contains("wii u") => "53",
                var p when p != null && p.Contains("wii") => "45",
                var p when p != null && (p.Contains("nintendo 64") || p.Contains("n64")) => "2",
                var p when p != null && (p.Contains("snes") || p.Contains("super nintendo")) => "3",
                var p when p != null && p.Contains("nes") => "7",
                var p when p != null && (p.Contains("game boy advance") || p.Contains("gba")) => "5",
                var p when p != null && (p.Contains("game boy color") || p.Contains("gbc")) => "6",
                var p when p != null && p.Contains("game boy") => "4",
                var p when p != null && p.Contains("3ds") => "4",
                var p when p != null && p.Contains("ds") => "18",
                var p when p != null && p.Contains("xbox 360") => "69",
                var p when p != null && p.Contains("dreamcast") => "49",
                var p when p != null && p.Contains("saturn") => "39",
                var p when p != null && (p.Contains("mega drive") || p.Contains("genesis")) => "1",
                _ => "0"
            };
        }
    }
}