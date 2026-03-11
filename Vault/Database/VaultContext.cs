using Microsoft.EntityFrameworkCore;
using System.IO;
using Vault.Models;

namespace Vault.Database
{
    public class VaultContext : DbContext
    {
        public DbSet<Game> Games { get; set; }
        public DbSet<MediaItem> MediaItems { get; set; }
        public DbSet<Episode> Episodes { get; set; }

        public static string DbPath
        {
            get
            {
                string folder = Path.Combine(
                    System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.ApplicationData), "Vault");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "vault.db");
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlite($"Data Source={DbPath}");
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