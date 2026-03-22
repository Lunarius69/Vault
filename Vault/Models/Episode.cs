using System;

namespace Vault.Models
{
    public class Episode
    {
        public int Id { get; set; }
        public int MediaItemId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? ThumbnailPath { get; set; }
        public string? FilePath { get; set; }
        public bool IsWatched { get; set; } = false;
        public long ResumePositionSeconds { get; set; } = 0;
        public DateTime? WatchedDate { get; set; }
        public int RuntimeMinutes { get; set; } = 0;

        // -1 means "not detected yet"
        // IntroStart/IntroEnd: fingerprint-detected opening/recap segment
        public double IntroStart { get; set; } = -1;
        public double IntroEnd { get; set; } = -1;

        // OutroStart: where credits/outro begin
        public double OutroStart { get; set; } = -1;

        // True once fingerprinting has been attempted for this episode
        public bool FingerprintProcessed { get; set; } = false;

        // Navigation property
        public MediaItem? MediaItem { get; set; }
    }
}