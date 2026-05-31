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

        // Known non-game subfolders inside steamapps/common (and general junk)
        private static readonly HashSet<string> _skipFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "VC_redist", "DirectX", "dotnet", "prerequisites", "redist", "redistributables",
            "natives_blob", "diagnostics32", "diagnostics64", "crashmsg", "launcher",
            "crs-handler", "crs-uploader", "apputil32", "apputil64", "base", "ui32", "ui64",
            "resourcecompiler32", "resourcecompiler64", "resourcecompiler",
            "UnityCrashHandler32", "UnityCrashHandler64",
            "steamredistributables", "commonredist", "installers", "setup",
            "PhysX", "DXSETUP", "vcredist", "support", "tools", "_CommonRedist",
            "EasyAntiCheat", "BattlEye", "EasyAnticheat_EOS_Setup",
            "start_protected_game", "steam_api", "steam_api64",
            "snapshot_blob", "st_data", "sheep", "playliststatetime",
            "r5apex_dx12", "r5apexdata", "MicrosoftEdgeWebView2Setup",
            "ApexLauncher", "masterduel", "Megabank", "LimbusCompany",
            "GoW", "installer", "natives_blob"
        };

        // Exe names that are never the main game executable
        private static readonly HashSet<string> _skipExeNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "unins000", "uninstall", "setup", "install", "redist",
            "dxsetup", "vcredist_x64", "vcredist_x86", "vcredist_x32",
            "VC_redist.x64", "VC_redist.x86",
            "UnityCrashHandler64", "UnityCrashHandler32",
            "crashhandler", "crashmsg", "crs-handler", "crs-uploader",
            "EasyAntiCheat_Setup", "EasyAntiCheat_EOS",
            "start_protected_game", "BattlEyeInstaller",
            "dotnetfx", "windowsdesktop-runtime",
            "apputil32", "apputil64", "launcher",
        };

        public RomScanner(AppSettings settings)
        {
            _settings = settings;
        }

        public async Task ScanAsync()
        {
            var romPaths = GetRomPaths();
            if (romPaths.Count == 0) return;

            var discovered = new List<(string Title, string Platform, string ExePath, double SizeGB)>();

            await Task.Run(() =>
            {
                foreach (var rootPath in romPaths)
                {
                    if (!Directory.Exists(rootPath)) continue;

                    // Non-PC ROMs: scan all files recursively by extension
                    var nonPcFiles = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories);
                    foreach (var file in nonPcFiles)
                    {
                        var ext = Path.GetExtension(file);
                        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!ExtensionToPlatform.TryGetValue(ext, out var platform)) continue;

                        var title = CleanTitle(Path.GetFileNameWithoutExtension(file));
                        var sizeGB = new FileInfo(file).Length / 1_073_741_824.0;
                        discovered.Add((title, platform, file, sizeGB));
                    }

                    // PC games: each direct subfolder = one game, pick largest exe
                    foreach (var gameFolder in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        var folderName = Path.GetFileName(gameFolder);
                        if (_skipFolders.Contains(folderName)) continue;

                        var bestExe = Directory
                            .EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
                            .Where(f => !_skipExeNames.Contains(
                                Path.GetFileNameWithoutExtension(f),
                                StringComparer.OrdinalIgnoreCase))
                            .OrderByDescending(f => new FileInfo(f).Length)
                            .FirstOrDefault();

                        if (bestExe == null) continue;

                        var title = CleanTitle(folderName);
                        var sizeGB = Directory
                            .EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                            / 1_073_741_824.0;

                        discovered.Add((title, "PC", bestExe, sizeGB));
                    }
                }
            });

            if (discovered.Count == 0) return;

            using var db = new VaultContext();

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
            if (!string.IsNullOrWhiteSpace(_settings.GamesFolderPath)) paths.Add(_settings.GamesFolderPath);
            if (!string.IsNullOrWhiteSpace(_settings.GamesFolderPath2)) paths.Add(_settings.GamesFolderPath2);
            if (!string.IsNullOrWhiteSpace(_settings.GamesFolderPath3)) paths.Add(_settings.GamesFolderPath3);
            return paths;
        }

        private static string CleanTitle(string title)
        {
            var cleaned = System.Text.RegularExpressions.Regex
                .Replace(title, @"[\(\[][^\)\]]*[\)\]]", "").Trim();
            cleaned = System.Text.RegularExpressions.Regex
                .Replace(cleaned, @"\s{2,}", " ").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? title : cleaned;
        }
    }
}