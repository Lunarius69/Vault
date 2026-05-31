using System;

namespace Vault.Models
{
    public class MediaItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "Movie";
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? PosterPath { get; set; }
        public string? BannerPath { get; set; }
        public string? TmdbIds { get; set; } // Comma-separated list of TMDB IDs
        public string? Description { get; set; }
        public double? TmdbRating { get; set; }
        public string WatchStatus { get; set; } = "Not Started";
        public string? FolderPath { get; set; }
        public int TotalEpisodes { get; set; } = 0;
        public int WatchedEpisodes { get; set; } = 0;
        public int? TotalSeasons { get; set; }
        public long ResumePositionSeconds { get; set; } = 0;
        public int? CurrentSeason { get; set; }
        public int? CurrentEpisode { get; set; }
        public int TmdbId { get; set; }

        // Total minutes spent watching this item across all sessions
        public long WatchTimeMinutes { get; set; } = 0;

        // Actual runtime in minutes — populated from the player on first play for movies
        public int RuntimeMinutes { get; set; } = 0;
    }
}