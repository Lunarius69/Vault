using System;

namespace Vault.Models
{
    public class Achievement
    {
        public int Id { get; set; }
        public int GameId { get; set; }

        public string ApiName { get; set; } = "";        // internal key from emulator file
        public string DisplayName { get; set; } = "";    // human-readable name
        public string Description { get; set; } = "";
        public string? IconPath { get; set; }            // local cached icon (optional)

        public bool IsUnlocked { get; set; } = false;
        public DateTime? UnlockedAt { get; set; }

        // navigation
        public Game? Game { get; set; }
    }
}