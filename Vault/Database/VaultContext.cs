using Microsoft.EntityFrameworkCore;
using Vault.Models;

namespace Vault.Database
{
    public class VaultContext : DbContext
    {
        public DbSet<Game> Games { get; set; }
        public DbSet<MediaItem> MediaItems { get; set; }
        public DbSet<Episode> Episodes { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            string dbPath = System.IO.Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.ApplicationData),
                "Vault", "vault.db");
            options.UseSqlite($"Data Source={dbPath}");
        }

        public static void EnsureSchema()
        {
            using var ctx = new VaultContext();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();

            // Add columns if they don't exist — safe to run every startup
            var columns = new[]
            {
        "ALTER TABLE Episodes ADD COLUMN IntroStart REAL NOT NULL DEFAULT -1",
        "ALTER TABLE Episodes ADD COLUMN IntroEnd REAL NOT NULL DEFAULT -1",
        "ALTER TABLE Episodes ADD COLUMN OutroStart REAL NOT NULL DEFAULT -1",
        "ALTER TABLE Episodes ADD COLUMN FingerprintProcessed INTEGER NOT NULL DEFAULT 0",
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
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Episode>()
                .HasOne(e => e.MediaItem)
                .WithMany()
                .HasForeignKey(e => e.MediaItemId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}