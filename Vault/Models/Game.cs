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
        public string LibraryType { get; set; } = "Owned";
        public string? ExePath { get; set; }
        public string? EmulatorPath { get; set; }
        public string? BoxArtPath { get; set; }
        public string? Description { get; set; }
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
    }
}