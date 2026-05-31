using Microsoft.EntityFrameworkCore;
using Vault.Models;

namespace Vault.Database
{
    public class VaultContext : DbContext
    {
        public DbSet<Game> Games { get; set; }
        public DbSet<MediaItem> MediaItems { get; set; }
        public DbSet<Episode> Episodes { get; set; }
        public DbSet<Achievement> Achievements { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            string dbPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.ApplicationData),
                "Vault", "vault.db");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
            options.UseSqlite($"Data Source={dbPath}");
        }

        public static void EnsureSchema()
        {
            using var ctx = new VaultContext();
            ctx.Database.EnsureCreated();

            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();

            // Add columns if they don't exist — safe to run every startup
            var columns = new[]
            {
                // Episode columns
                "ALTER TABLE Episodes ADD COLUMN IntroStart REAL NOT NULL DEFAULT -1",
                "ALTER TABLE Episodes ADD COLUMN IntroEnd REAL NOT NULL DEFAULT -1",
                "ALTER TABLE Episodes ADD COLUMN OutroStart REAL NOT NULL DEFAULT -1",
                "ALTER TABLE Episodes ADD COLUMN FingerprintProcessed INTEGER NOT NULL DEFAULT 0",

                // Game columns added over time
                "ALTER TABLE Games ADD COLUMN HltbMainStory INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN HltbMainPlusExtra INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN HltbCompletionist INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN HltbMain REAL",
                "ALTER TABLE Games ADD COLUMN HltbMainSides REAL",
                "ALTER TABLE Games ADD COLUMN HltbComplete REAL",
                "ALTER TABLE Games ADD COLUMN Description TEXT",
                "ALTER TABLE Games ADD COLUMN Genre TEXT",
                "ALTER TABLE Games ADD COLUMN FileSizeGB REAL",
                "ALTER TABLE Games ADD COLUMN PlaytimeMinutes INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN LastPlayed TEXT",
                "ALTER TABLE Games ADD COLUMN IsWishlist INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN IsDownloaded INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN ManuallyMarkedNotDownloaded INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE Games ADD COLUMN AchievementsEarned INTEGER",
                "ALTER TABLE Games ADD COLUMN AchievementsTotal INTEGER",
                "ALTER TABLE Games ADD COLUMN RetroAchievementsGameId INTEGER",
                "ALTER TABLE Games ADD COLUMN SteamAppId INTEGER",
                "ALTER TABLE Games ADD COLUMN LibraryType TEXT NOT NULL DEFAULT 'Owned'",
                "ALTER TABLE Games ADD COLUMN ExePath TEXT",
                "ALTER TABLE Games ADD COLUMN EmulatorPath TEXT",
                "ALTER TABLE Games ADD COLUMN BoxArtPath TEXT",
                "ALTER TABLE Games ADD COLUMN SourceFile TEXT",

                // MediaItem columns added over time
                "ALTER TABLE MediaItems ADD COLUMN WatchTimeMinutes INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE MediaItems ADD COLUMN RuntimeMinutes INTEGER NOT NULL DEFAULT 0",
            };

            foreach (var sql in columns)
            {
                try
                {
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                catch { /* column already exists — ignore */ }
            }

            // Achievements table — CREATE IF NOT EXISTS is idempotent
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS Achievements (" +
                "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "GameId INTEGER NOT NULL, " +
                "ApiName TEXT NOT NULL DEFAULT '', " +
                "DisplayName TEXT NOT NULL DEFAULT '', " +
                "Description TEXT NOT NULL DEFAULT '', " +
                "IconPath TEXT, " +
                "IsUnlocked INTEGER NOT NULL DEFAULT 0, " +
                "UnlockedAt TEXT, " +
                "FOREIGN KEY(GameId) REFERENCES Games(Id) ON DELETE CASCADE" +
                ")";
            cmd.ExecuteNonQuery();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Episode>()
                .HasOne(e => e.MediaItem)
                .WithMany()
                .HasForeignKey(e => e.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Achievement>()
                .HasOne(a => a.Game)
                .WithMany()
                .HasForeignKey(a => a.GameId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}