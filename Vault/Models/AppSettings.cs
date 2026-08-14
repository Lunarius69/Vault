using System.IO;
using System.Text.Json;

namespace Vault.Models
{
    public class AppSettings
    {
        public string GamesFolderPath { get; set; } = "";
        public string GamesFolderPath2 { get; set; } = "";
        public string GamesFolderPath3 { get; set; } = "";
        public string EmulatorsFolderPath { get; set; } = "";
        public string MoviesFolderPath { get; set; } = "";
        public string ShowsFolderPath { get; set; } = "";
        public string AnimeFolderPath { get; set; } = "";
        // FIX: only Movies/Shows/Anime had a root folder setting — the other
        // three media categories (Anime Movies, Animated Series, Animated
        // Movies) had nowhere to configure one at all.
        public string AnimeMoviesFolderPath { get; set; } = "";
        public string AnimatedSeriesFolderPath { get; set; } = "";
        public string AnimatedMoviesFolderPath { get; set; } = "";
        public string DataFolderPath { get; set; } = "";
        public string SteamGridDbApiKey { get; set; } = "4687f5ff5527c4fa12a89b0f8c2ee359";
        public string TmdbApiKey { get; set; } = "43d720cdc573b6baba8276b8b19ab901";
        public string SteamApiKey { get; set; } = "B4999E91D9E2C56C93B5B371170D78CA";
        public string SteamUserId { get; set; } = "";
        public string? OpenVGDBPath { get; set; }
        public string RetroAchievementsUser { get; set; } = "";
        public string RetroAchievementsApiKey { get; set; } = "KBqRJjEVs6u2rhZmgXjG6LHow9Gr6vrN";
        public string GamesExcelPath { get; set; } = "";
        public string MediaExcelPath { get; set; } = "";

        // Player language preferences — matched case-insensitively against LibVLC track names.
        // Empty string = no preference (leave LibVLC's default). "off" = always disable subtitles.
        public string PreferredSubtitleLanguage { get; set; } = "";
        public string PreferredAudioLanguage { get; set; } = "";

        private static string SettingsPath => Path.Combine(
            System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.ApplicationData),
            "Vault", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string json = JsonSerializer.Serialize(this,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}