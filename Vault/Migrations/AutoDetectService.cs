using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

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

            string[] extensions = {
                "*.exe", "*.iso", "*.bin", "*.chd", "*.nsp",
                "*.xci", "*.nds", "*.gba", "*.n64", "*.z64", "*.pkg"
            };

            var allFiles = new List<string>();
            foreach (string ext in extensions)
                allFiles.AddRange(Directory.GetFiles(
                    _settings.GamesFolderPath, ext,
                    SearchOption.AllDirectories));

            using var db = new VaultContext();
            var games = await db.Games
                .Where(g => !g.IsWishlist)
                .ToListAsync();

            int updated = 0;

            foreach (var game in games)
            {
                if (game.IsDownloaded) continue;

                string titleClean = CleanTitle(game.Title).ToLower();

                var match = allFiles.FirstOrDefault(f =>
                {
                    string fileClean = CleanTitle(
                        Path.GetFileNameWithoutExtension(f)).ToLower();

                    if (fileClean.Equals(titleClean))
                        return true;

                    if (titleClean.Length >= 6 &&
                        fileClean.StartsWith(titleClean))
                    {
                        if (fileClean.Length == titleClean.Length ||
                            !char.IsLetter(fileClean[titleClean.Length]))
                            return true;
                    }

                    return false;
                });

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
            return title
                .Replace(":", "").Replace("-", " ").Replace("_", " ")
                .Replace("(", "").Replace(")", "")
                .Replace("the ", "", System.StringComparison.OrdinalIgnoreCase)
                .Trim();
        }
    }
}