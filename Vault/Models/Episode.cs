namespace Vault.Models
{
    public class Episode
    {
        public int Id { get; set; }
        public int MediaItemId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; } = "";
        public string? FilePath { get; set; }
        public bool IsWatched { get; set; } = false;
        public long ResumePositionSeconds { get; set; } = 0;
        public long IntroEndSeconds { get; set; } = 0;
        public long OutroStartSeconds { get; set; } = 0;
        public MediaItem? MediaItem { get; set; }
    }
}