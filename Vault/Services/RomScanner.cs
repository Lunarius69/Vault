using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class RomScanner
    {
        private readonly AppSettings _settings;

        // Common ROM file extensions mapped to platform names
        private static readonly Dictionary<string, string> ExtensionToPlatform = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".nes",  "NES" },
            { ".smc",  "SNES" },
            { ".sfc",  "SNES" },
            { ".gb",   "Game Boy" },
            { ".gbc",  "Game Boy Color" },
            { ".gba",  "GBA" },
            { ".nds",  "Nintendo DS" },
            { ".3ds",  "Nintendo 3DS" },
            { ".n64",  "Nintendo 64" },
            { ".z64",  "Nintendo 64" },
            { ".v64",  "Nintendo 64" },
            { ".iso",  "PS2" },
            { ".bin",  "PS1" },
            { ".cue",  "PS1" },
            { ".chd",  "PS2" },
            { ".gcm",  "GameCube" },
            { ".rvz",  "Wii" },
            { ".wbfs", "Wii" },
            { ".xci",  "Switch" },
            { ".nsp",  "Switch" },
            { ".exe",  "PC" },
        };

        public RomScanner(AppSettings settings)
        {
            _settings = settings;
        }

        public async Task ScanAsync()
        {
            var romPaths = GetRomPaths();
            if (romPaths.Count == 0)
                return;

            var discovered = new List<(string Title, string Platform, string ExePath, double SizeGB)>();

            await Task.Run(() =>
            {
                foreach (var rootPath in romPaths)
                {
                    if (!Directory.Exists(rootPath))
                        continue;

                    var files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);

                    foreach (var file in files)
                    {
                        var ext = Path.GetExtension(file);
                        if (!ExtensionToPlatform.TryGetValue(ext, out var platform))
                            continue;

                        var title = CleanTitle(Path.GetFileNameWithoutExtension(file));
                        var sizeGB = new FileInfo(file).Length / 1_073_741_824.0;

                        discovered.Add((title, platform, file, sizeGB));
                    }
                }
            });

            if (discovered.Count == 0)
                return;

            using var db = new VaultContext();

            // Avoid duplicates by checking existing ExePaths
            var existingPaths = await db.Games
                .Where(g => g.ExePath != null)
                .Select(g => g.ExePath!)
                .ToHashSetAsync();

            var toAdd = discovered
                .Where(d => !existingPaths.Contains(d.ExePath))
                .ToList();

            foreach (var (title, platform, exePath, sizeGB) in toAdd)
            {
                db.Games.Add(new Game
                {
                    Title = title,
                    Platform = platform,
                    ExePath = exePath,
                    FileSizeGB = sizeGB,
                    Status = "Not Started",
                    LibraryType = "Owned",
                    IsDownloaded = true,
                });
            }

            await db.SaveChangesAsync();
        }

        private List<string> GetRomPaths()
        {
            var paths = new List<string>();

            if (!string.IsNullOrWhiteSpace(_settings.GamesFolderPath))
                paths.Add(_settings.GamesFolderPath);

            if (!string.IsNullOrWhiteSpace(_settings.GamesFolderPath2))
                paths.Add(_settings.GamesFolderPath2);

            if (!string.IsNullOrWhiteSpace(_settings.GamesFolderPath3))
                paths.Add(_settings.GamesFolderPath3);

            return paths;
        }

        private static string CleanTitle(string title)
        {
            // Remove common ROM tags like (USA), [!], (Rev 1), etc.
            var cleaned = System.Text.RegularExpressions.Regex.Replace(
                title, @"[\(\[][^\)\]]*[\)\]]", "").Trim();

            // Collapse multiple spaces
            cleaned = System.Text.RegularExpressions.Regex.Replace(
                cleaned, @"\s{2,}", " ").Trim();

            return string.IsNullOrWhiteSpace(cleaned) ? title : cleaned;
        }
    }
}