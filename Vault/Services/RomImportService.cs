using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class RomImportService
    {
        private readonly AppSettings _settings;

        public RomImportService(AppSettings settings) => _settings = settings;

        public async Task<int> ScanAndImportAsync(IProgress<string>? progress = null)
        {
            if (string.IsNullOrEmpty(_settings.GamesFolderPath) ||
                !Directory.Exists(_settings.GamesFolderPath))
                return 0;

            using var db = new VaultContext();

            // Build a set of all already-imported ROM paths for fast duplicate check
            var existingPaths = await db.Games
                .Where(g => g.EmulatorPath != null)
                .Select(g => g.EmulatorPath!)
                .ToHashSetAsync();

            var toImport = new List<Game>();

            // Each subfolder = one console
            foreach (var consoleFolder in Directory.GetDirectories(
                _settings.GamesFolderPath, "*", SearchOption.TopDirectoryOnly))
            {
                string consoleName = DetectConsoleFromFolder(
                    Path.GetFileName(consoleFolder));

                // Get all ROM files in this console folder (non-recursive)
                var romFiles = Directory.GetFiles(consoleFolder)
                    .Where(f => IsRomFile(f))
                    .ToList();

                foreach (var romPath in romFiles)
                {
                    // Skip if already imported
                    if (existingPaths.Contains(romPath)) continue;

                    string ext = Path.GetExtension(romPath).ToLower();

                    // Use extension to refine console detection if folder name was unclear
                    string finalConsole = consoleName == "Unknown"
                        ? DetectConsoleFromExtension(ext)
                        : consoleName;

                    string cleanTitle = CleanRomTitle(
                        Path.GetFileNameWithoutExtension(romPath));

                    progress?.Report($"Importing {cleanTitle}...");

                    toImport.Add(new Game
                    {
                        Title = cleanTitle,
                        Platform = finalConsole,
                        Status = "Not Started",
                        IsDownloaded = true,
                        IsWishlist = false,
                        EmulatorPath = romPath,
                        LibraryType = "Owned"
                    });
                }
            }

            if (toImport.Count == 0) return 0;

            await db.Games.AddRangeAsync(toImport);
            await db.SaveChangesAsync();
            return toImport.Count;
        }

        private static string CleanRomTitle(string filename)
        {
            // Remove region tags like (USA), (Europe), (Japan), (En), (Fr) etc.
            filename = System.Text.RegularExpressions.Regex.Replace(
                filename, @"\((?:USA|Europe|Japan|World|En|Fr|De|Es|It|Nl|Pt|Sv|No|Da|Fi)[^)]*\)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                filename = System.Text.RegularExpressions.Regex.Replace(
        filename, @"\(Disc\s*\d+\)", "",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove common ROM flags like [!], [T+Eng], [h], [b], etc.
            filename = System.Text.RegularExpressions.Regex.Replace(
                filename, @"\[[^\]]*\]", "");

            // Remove version tags like (v1.0), (Rev A), (Beta), (Proto)
            filename = System.Text.RegularExpressions.Regex.Replace(
                filename, @"\((?:v[\d.]+|Rev [A-Z]|Beta|Proto|Demo|Sample)[^)]*\)",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Remove any remaining empty parentheses
            filename = System.Text.RegularExpressions.Regex.Replace(
                filename, @"\(\s*\)", "");

            // Clean up extra spaces and trim
            filename = System.Text.RegularExpressions.Regex.Replace(
                filename, @"\s{2,}", " ").Trim();

            // Remove trailing punctuation left over from cleanup
            filename = filename.TrimEnd('-', '_', ' ', ',');

            return filename;
        }

        private static string DetectConsoleFromFolder(string folderName)
        {
            string f = folderName.ToLower();
            return f switch
            {
                var p when p.Contains("ps2") || p.Contains("playstation 2") => "PlayStation 2",
                var p when p.Contains("ps1") || p.Contains("psx") || p.Contains("playstation 1") => "PlayStation 1",
                var p when p.Contains("ps3") || p.Contains("playstation 3") => "PlayStation 3",
                var p when p.Contains("psp") => "PSP",
                var p when p.Contains("vita") => "PS Vita",
                var p when p.Contains("switch") => "Nintendo Switch",
                var p when p.Contains("n64") || p.Contains("nintendo 64") => "Nintendo 64",
                var p when p.Contains("gamecube") || p.Contains("gcn") => "GameCube",
                var p when p.Contains("wii u") => "Wii U",
                var p when p.Contains("wii") => "Wii",
                var p when p.Contains("gba") || p.Contains("game boy advance") => "Game Boy Advance",
                var p when p.Contains("gbc") || p.Contains("game boy color") => "Game Boy Color",
                var p when p.Contains("gb") || p.Contains("game boy") => "Game Boy",
                var p when p.Contains("nds") || p.Contains("nintendo ds") => "Nintendo DS",
                var p when p.Contains("3ds") => "Nintendo 3DS",
                var p when p.Contains("snes") || p.Contains("super nintendo") => "SNES",
                var p when p.Contains("nes") || p.Contains("famicom") => "NES",
                var p when p.Contains("xbox 360") => "Xbox 360",
                var p when p.Contains("xbox") => "Xbox",
                var p when p.Contains("dreamcast") || p.Contains("dc") => "Dreamcast",
                var p when p.Contains("saturn") => "Sega Saturn",
                var p when p.Contains("genesis") || p.Contains("mega drive") => "Sega Genesis",
                var p when p.Contains("pc") => "PC",
                _ => "Unknown"
            };
        }

        private static string DetectConsoleFromExtension(string ext)
        {
            return ext switch
            {
                ".z64" or ".n64" or ".v64" => "Nintendo 64",
                ".nsp" or ".xci" => "Nintendo Switch",
                ".iso" when true => "Unknown", // ISO is too ambiguous alone
                ".bin" or ".cue" => "PlayStation 1",
                ".chd" => "Unknown", // Could be PS1/PS2/Dreamcast
                ".gba" => "Game Boy Advance",
                ".gbc" => "Game Boy Color",
                ".gb" => "Game Boy",
                ".nds" => "Nintendo DS",
                ".3ds" or ".cia" => "Nintendo 3DS",
                ".sfc" or ".smc" => "SNES",
                ".nes" => "NES",
                ".elf" or ".pkg" => "PlayStation 3",
                _ => "Unknown"
            };
        }

        private static bool IsRomFile(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext is ".iso" or ".bin" or ".cue" or ".chd" or
                          ".nsp" or ".xci" or ".elf" or ".pkg" or
                          ".rom" or ".nds" or ".3ds" or ".cia" or
                          ".gba" or ".gb" or ".gbc" or ".n64" or
                          ".z64" or ".v64" or ".sfc" or ".smc" or
                          ".nes" or ".md" or ".smd";
        }
    }
}