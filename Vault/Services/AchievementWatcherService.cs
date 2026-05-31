using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    /// <summary>
    /// Loads achievement data for a game by:
    ///   1. Determining whether the game is Steam-based (PC platform + Notes indicate Steam)
    ///   2. Resolving the Steam AppID from local files, DB cache, Notes field, or the
    ///      Steam store search API
    ///   3. Fetching the achievement schema from Steam's API
    ///      - With a Steam Web API key: uses GetSchemaForGame (works for all games)
    ///      - Without a key: same endpoint but returns empty for many games
    ///   4. Scanning local repack emulator files for unlock data (Goldberg, Codex, etc.)
    ///
    /// Non-Steam platforms and GOG/Battle.net/EA App-only games return null immediately.
    ///
    /// NOTE: Many Steam games require a Steam Web API key to return achievement schemas.
    ///       Add your key in Settings → Steam Web API Key.
    ///       Get one free at: https://steamcommunity.com/dev/apikey
    /// </summary>
    public class AchievementWatcherService
    {
        private static readonly HttpClient _http = new();

        // Optional Steam Web API key — set via SetApiKey() called from Settings load
        private static string? _steamApiKey;

        public static void SetApiKey(string? key)
        {
            _steamApiKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        // ── Non-Steam source prefixes — skip Steam API entirely for these ─────────
        // Matches the actual Notes field values found in this library.
        // A Notes field starting with any of these means the game is NOT on Steam.
        private static readonly string[] NonSteamPrefixes = new[]
        {
            "gog",
            "battle.net",
            "ea app",
            "ubisoft connect",
            "epic games store",
            "microsoft store",
            "xbox app",
            "minecraft.net",
            "mobile",
            "pc abandonware",
            "not on steam",
            "psp only",
            "msx",
        };

        // ── Public entry point ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the full achievement list with unlock state, or null if:
        /// - The game is not PC
        /// - The game is not on Steam (GOG-only, Battle.net, abandonware, etc.)
        /// - No AppID could be resolved
        /// - Steam returned no achievement schema
        /// </summary>
        public async Task<List<Achievement>?> LoadAchievementsAsync(Game game)
        {
            // Only attempt for PC games
            if (!IsPcGame(game)) return null;

            int? appId = null;

            try
            {
                if (game.SteamAppId.HasValue)
                {
                    // Fast path — AppID already cached in DB, skip all detection
                    appId = game.SteamAppId;
                }
                else
                {
                    // ── Step 1: local steam_appid.txt next to the exe ─────────────
                    appId = ExtractAppIdFromLocal(game);

                    // ── Step 2: "steam: 12345" pattern in Notes field ─────────────
                    if (appId == null)
                        appId = ExtractAppIdFromNotes(game);

                    // ── Step 3: Steam store search — ONLY for Steam-sourced games ──
                    // If Notes clearly show this is GOG / Battle.net / abandonware /
                    // etc., return null cleanly — no network call, no error message.
                    if (appId == null)
                    {
                        if (!IsSteamGame(game))
                            return null;

                        try { appId = await SearchSteamAppIdAsync(game.Title); }
                        catch { /* network failure is non-fatal */ }
                    }

                    if (appId == null) return null;

                    // Cache in memory for this session
                    game.SteamAppId = appId;

                    // Persist to DB so future opens are instant
                    try
                    {
                        using var db = new VaultContext();
                        var dbGame = await db.Games.FindAsync(game.Id);
                        if (dbGame != null && !dbGame.SteamAppId.HasValue)
                        {
                            dbGame.SteamAppId = appId;
                            await db.SaveChangesAsync();
                        }
                    }
                    catch { /* non-fatal */ }
                }

                // Fetch achievement definitions from Steam
                List<AchDef>? definitions = null;
                try { definitions = await FetchSteamSchemaAsync(appId.Value); }
                catch { }

                if (definitions == null || definitions.Count == 0) return null;

                // Scan local repack/emulator files for unlock state
                var unlocks = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
                try { unlocks = ScanLocalUnlocks(appId.Value); }
                catch { }

                // Merge definitions with unlock state
                var results = definitions.Select(def =>
                {
                    bool unlocked = unlocks.TryGetValue(def.ApiName, out DateTime? unlockedAt);
                    return new Achievement
                    {
                        GameId = game.Id,
                        ApiName = def.ApiName,
                        DisplayName = def.DisplayName,
                        Description = def.Description,
                        IsUnlocked = unlocked,
                        UnlockedAt = unlockedAt
                    };
                }).ToList();

                return results
                    .OrderByDescending(a => a.IsUnlocked)
                    .ThenBy(a => a.DisplayName)
                    .ToList();
            }
            catch
            {
                return null;
            }
        }

        // ── Platform / store detection ────────────────────────────────────────────

        private static bool IsPcGame(Game game)
        {
            try
            {
                string p = game?.Platform?.ToLower() ?? "";
                return p.Contains("pc") || p.Contains("windows");
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns true if the game's Notes field indicates Steam is one of its platforms.
        ///
        /// True examples:  "Steam", "Steam — description", "Steam / GOG", "GOG / Steam",
        ///                 "EA App / Steam", "Epic / Steam", "Steam / Epic"
        ///
        /// False examples: "GOG — ...", "Battle.net — ...", "EA App — ...",
        ///                 "PC abandonware", "Not on Steam", (empty)
        /// </summary>
        internal static bool IsSteamGame(Game game)
        {
            try
            {
                string notes = game.Notes?.ToLower().Trim() ?? "";

                if (string.IsNullOrEmpty(notes)) return false;

                // If Notes starts with a known non-Steam prefix → not a Steam game,
                // even if "steam" appears later in the description text
                foreach (var prefix in NonSteamPrefixes)
                {
                    if (notes.StartsWith(prefix)) return false;
                }

                // Notes contain "steam" → Steam is one of the platforms
                return notes.Contains("steam");
            }
            catch { return false; }
        }

        // ── AppID resolution: local files ─────────────────────────────────────────

        private static int? ExtractAppIdFromLocal(Game game)
        {
            try
            {
                string? dir = null;
                if (!string.IsNullOrEmpty(game.ExePath))
                    dir = Path.GetDirectoryName(game.ExePath);
                else if (!string.IsNullOrEmpty(game.EmulatorPath))
                    dir = Path.GetDirectoryName(game.EmulatorPath);

                if (dir == null) return null;

                foreach (string candidate in new[]
                {
                    Path.Combine(dir, "steam_appid.txt"),
                    Path.Combine(Path.GetDirectoryName(dir) ?? dir, "steam_appid.txt"),
                })
                {
                    if (File.Exists(candidate) &&
                        int.TryParse(File.ReadAllText(candidate).Trim(), out int id))
                        return id;
                }
            }
            catch { }

            return null;
        }

        // ── AppID resolution: Notes field ─────────────────────────────────────────

        private static int? ExtractAppIdFromNotes(Game game)
        {
            try
            {
                if (string.IsNullOrEmpty(game.Notes)) return null;
                var match = Regex.Match(game.Notes, @"steam[:\s/]+(\d{4,8})", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
                    return id;
            }
            catch { }
            return null;
        }

        // ── AppID resolution: Steam store search API ──────────────────────────────

        internal static async Task<int?> SearchSteamAppIdAsync(string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title)) return null;

                string query = Uri.EscapeDataString(title);
                string url = $"https://store.steampowered.com/api/storesearch/?term={query}&l=english&cc=US";

                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("items", out var items)) return null;

                string titleLower = title.ToLower().Trim();

                // First pass: exact match
                foreach (var item in items.EnumerateArray())
                {
                    string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name?.ToLower().Trim() == titleLower)
                        if (item.TryGetProperty("id", out var idProp))
                            return idProp.GetInt32();
                }

                // Second pass: close match
                foreach (var item in items.EnumerateArray())
                {
                    string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null) continue;
                    string nameLower = name.ToLower().Trim();
                    if (nameLower.Contains(titleLower) || titleLower.Contains(nameLower))
                        if (item.TryGetProperty("id", out var idProp))
                            return idProp.GetInt32();
                }

                return null;
            }
            catch { return null; }
        }

        // ── Steam achievement schema ──────────────────────────────────────────────

        private record AchDef(string ApiName, string DisplayName, string Description);

        private static async Task<List<AchDef>?> FetchSteamSchemaAsync(int appId)
        {
            try
            {
                // With a key: reliable for all Steam games
                // Without a key: only works for games with public stats — many will return empty
                string url = string.IsNullOrEmpty(_steamApiKey)
                    ? $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?appid={appId}"
                    : $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?appid={appId}&key={_steamApiKey}";

                string json = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("game", out var gameEl)) return null;
                if (!gameEl.TryGetProperty("availableGameStats", out var stats)) return null;
                if (!stats.TryGetProperty("achievements", out var achs)) return null;

                var list = new List<AchDef>();
                foreach (var a in achs.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string display = a.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;
                    string desc = a.TryGetProperty("description", out var de) ? de.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name))
                        list.Add(new AchDef(name, display, desc));
                }
                return list.Count > 0 ? list : null;
            }
            catch { return null; }
        }

        // ── Local emulator file scanning ──────────────────────────────────────────

        private static Dictionary<string, DateTime?> ScanLocalUnlocks(int appId)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            foreach (var scanner in GetScanners())
            {
                try { foreach (var kv in scanner(appId)) result.TryAdd(kv.Key, kv.Value); }
                catch { }
            }
            return result;
        }

        private static IEnumerable<Func<int, Dictionary<string, DateTime?>>> GetScanners()
        {
            yield return ScanGoldberg;
            yield return ScanCodex;
            yield return ScanSmartSteamEmu;
            yield return ScanAli213;
            yield return ScanCreamApi;
        }

        private static Dictionary<string, DateTime?> ScanGoldberg(int appId)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var paths = new[]
                {
                    Path.Combine(AppData,      "Goldberg SteamEmu Saves", appId.ToString(), "achievements.json"),
                    Path.Combine(LocalAppData, "Goldberg SteamEmu Saves", appId.ToString(), "achievements.json"),
                };
                foreach (string path in paths.Where(File.Exists))
                {
                    try
                    {
                        var node = JsonNode.Parse(File.ReadAllText(path));
                        if (node is not JsonObject obj) continue;
                        foreach (var kv in obj)
                        {
                            try
                            {
                                if (kv.Value is not JsonObject ach) continue;
                                bool earned = false;
                                try { earned = ach["earned"]?.GetValue<int>() == 1; } catch { }
                                if (!earned)
                                    try { earned = ach["earned"]?.GetValue<bool>() == true; } catch { }
                                if (!earned) continue;
                                DateTime? ts = null;
                                try
                                {
                                    if (ach["earned_time"] is JsonValue tv &&
                                        long.TryParse(tv.ToJsonString(), out long unix) && unix > 0)
                                        ts = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                                }
                                catch { }
                                result.TryAdd(kv.Key, ts);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<string, DateTime?> ScanCodex(int appId)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var paths = new[]
                {
                    Path.Combine(AppData, "CODEX",    appId.ToString(), "achievements.ini"),
                    Path.Combine(AppData, "Skidrow",  appId.ToString(), "achievements.ini"),
                    Path.Combine(AppData, "SteamEmu", appId.ToString(), "achievements.ini"),
                };
                foreach (string path in paths.Where(File.Exists))
                {
                    try
                    {
                        foreach (string line in File.ReadAllLines(path))
                        {
                            try
                            {
                                if (line.StartsWith("[") || !line.Contains("=")) continue;
                                int eq = line.IndexOf('=');
                                string key = line[..eq].Trim();
                                string val = line[(eq + 1)..].Trim();
                                if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase))
                                    result.TryAdd(key, null);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<string, DateTime?> ScanSmartSteamEmu(int appId)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var dirs = new[]
                {
                    Path.Combine(AppData, "SmartSteamEmu",  appId.ToString(), "stats"),
                    Path.Combine(AppData, "SmartSteamEmu2", appId.ToString(), "stats"),
                };
                foreach (string dir in dirs.Where(Directory.Exists))
                {
                    try
                    {
                        foreach (string file in Directory.GetFiles(dir))
                        {
                            try
                            {
                                string achName = Path.GetFileName(file);
                                var t = File.GetLastWriteTime(file);
                                result.TryAdd(achName, t > DateTime.MinValue ? t : null);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<string, DateTime?> ScanAli213(int appId)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(AppData, "ALI213", appId.ToString(), "SteamEmu.ini");
                if (!File.Exists(path)) return result;
                bool inSection = false;
                foreach (string line in File.ReadAllLines(path))
                {
                    try
                    {
                        string t = line.Trim();
                        if (t.Equals("[Achievements]", StringComparison.OrdinalIgnoreCase))
                        { inSection = true; continue; }
                        if (t.StartsWith("[")) { inSection = false; continue; }
                        if (!inSection || !t.Contains("=")) continue;
                        int eq = t.IndexOf('=');
                        result.TryAdd(t[..eq].Trim(), null);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static Dictionary<string, DateTime?> ScanCreamApi(int appId)
        {
            var result = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string[] roots =
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),    "Steam", "steamapps", "common"),
                    @"C:\Games", @"D:\Games", @"E:\Games", @"F:\Games",
                };
                foreach (string root in roots.Where(Directory.Exists))
                {
                    try
                    {
                        foreach (string ini in Directory.GetFiles(root, "cream_api.ini", SearchOption.AllDirectories).Take(200))
                        {
                            try
                            {
                                string content = File.ReadAllText(ini);
                                if (!content.Contains(appId.ToString())) continue;
                                bool inAchieved = false;
                                foreach (string line in content.Split('\n'))
                                {
                                    try
                                    {
                                        string t = line.Trim();
                                        if (t.Equals("[achieved]", StringComparison.OrdinalIgnoreCase))
                                        { inAchieved = true; continue; }
                                        if (t.StartsWith("[")) { inAchieved = false; continue; }
                                        if (!inAchieved || !t.Contains("=")) continue;
                                        int eq = t.IndexOf('=');
                                        result.TryAdd(t[..eq].Trim(), null);
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        private static string AppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static string LocalAppData =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }
}