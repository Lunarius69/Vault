using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;
using Microsoft.EntityFrameworkCore;

namespace Vault.Services
{
    public class AutoDetectService
    {
        private readonly AppSettings _settings;

        public AutoDetectService(AppSettings settings) => _settings = settings;

        public async Task<int> ScanAndUpdateAsync()
        {
            if (string.IsNullOrEmpty(_settings.GamesFolderPath) ||
                !Directory.Exists(_settings.GamesFolderPath))
                return 0;

            // Collect all game files in the folder
            string[] extensions = {
                "*.exe", "*.iso", "*.bin", "*.chd", "*.nsp",
                "*.xci", "*.nds", "*.gba", "*.n64", "*.z64", "*.pkg"
            };

            var allFiles = new List<string>();
            foreach (string ext in extensions)
            {
                allFiles.AddRange(Directory.GetFiles(
                    _settings.GamesFolderPath, ext,
                    SearchOption.AllDirectories));
            }

            using var db = new VaultContext();
            var games = await db.Games
                .Where(g => !g.IsWishlist)
                .ToListAsync();

            int updated = 0;

            foreach (var game in games)
            {
                if (game.IsDownloaded) continue;

                // Try to match by title similarity against file names
                string titleClean = CleanTitle(game.Title);
                var match = allFiles.FirstOrDefault(f =>
                    CleanTitle(Path.GetFileNameWithoutExtension(f))
                        .Contains(titleClean, StringComparison.OrdinalIgnoreCase));

                if (match == null) continue;

                string ext = Path.GetExtension(match).ToLower();
                if (ext == ".exe")
                    game.ExePath = match;
                else
                    game.EmulatorPath = match;

                game.IsDownloaded = true;
                updated++;
            }

            if (updated > 0)
                await db.SaveChangesAsync();

            return updated;
        }

        private static string CleanTitle(string title)
        {
            // Remove common noise words and punctuation for better matching
            return title
                .Replace(":", "").Replace("-", " ").Replace("_", " ")
                .Replace("(", "").Replace(")", "")
                .Replace("the ", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
        }
    }
}