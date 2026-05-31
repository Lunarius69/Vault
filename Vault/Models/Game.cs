using System;

namespace Vault.Models
{
    public class Game
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Platform { get; set; } = "";
        public int? Year { get; set; }
        public double? FileSizeGB { get; set; }
        public string Status { get; set; } = "Not Started";
        public string? SourceFile { get; set; }
        public string LibraryType { get; set; } = "Owned";
        public string? ExePath { get; set; }
        public string? EmulatorPath { get; set; }
        public string? BoxArtPath { get; set; }
        public string? Description { get; set; }
        public int HltbMainStory { get; set; }
        public int HltbMainPlusExtra { get; set; }
        public int HltbCompletionist { get; set; }
        public string? Genre { get; set; }
        public int PlaytimeMinutes { get; set; } = 0;
        public double? HltbMain { get; set; }
        public double? HltbMainSides { get; set; }
        public double? HltbComplete { get; set; }
        public DateTime? LastPlayed { get; set; }
        public bool IsWishlist { get; set; } = false;
        public bool IsDownloaded { get; set; } = false;
        public bool ManuallyMarkedNotDownloaded { get; set; } = false;

        // Achievements
        public int? AchievementsEarned { get; set; }
        public int? AchievementsTotal { get; set; }
        public int? RetroAchievementsGameId { get; set; }

        // Cached Steam AppID — resolved automatically from store search or steam_appid.txt
        public int? SteamAppId { get; set; }

        // Source / emulator info from spreadsheet
        public string? Notes { get; set; }
        public string? Emulator { get; set; }
    }
}