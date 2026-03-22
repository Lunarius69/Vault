using System.IO;
using System.Text.Json;

namespace Vault.Models
{
    public class AppSettings
    {
        public string GamesFolderPath { get; set; } = "";
        public string EmulatorsFolderPath { get; set; } = "";
        public string MoviesFolderPath { get; set; } = "";
        public string ShowsFolderPath { get; set; } = "";
        public string AnimeFolderPath { get; set; } = "";
        public string DataFolderPath { get; set; } = "";
        public string SteamGridDbApiKey { get; set; } = "";
        public string TmdbApiKey { get; set; } = "";
        public string SteamApiKey { get; set; } = "";
        public string SteamUserId { get; set; } = "";
        public string? OpenVGDBPath { get; set; }
        public string RetroAchievementsUser { get; set; } = "";
        public string RetroAchievementsApiKey { get; set; } = "";
        public string GamesExcelPath { get; set; } = "";
        public string MediaExcelPath { get; set; } = "";

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
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}