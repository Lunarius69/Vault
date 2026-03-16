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

        // Navigation property
        public MediaItem? MediaItem { get; set; }
    }
}