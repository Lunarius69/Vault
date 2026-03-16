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