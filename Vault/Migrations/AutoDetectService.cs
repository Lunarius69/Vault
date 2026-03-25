using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Concurrent;
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

        private static readonly HashSet<string> BlockedFileNames = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "snapshot_blob", "v8_context_snapshot", "natives_blob", "d3dcompiler_47",
            "ffmpeg", "libEGL", "libGLESv2", "index", "update", "updater", "uninstall",
            "uninstaller", "setup", "install", "installer", "crashpad_handler",
            "notification_helper", "crash_reporter", "elevate", "squirrel",
            "UnityCrashHandler64", "UnityCrashHandler32", "dxwebsetup",
            "vc_redist.x64", "vc_redist.x86", "dotnet", "dotnetfx"
        };

        private static readonly HashSet<string> BlockedPathFragments = new(
            StringComparer.OrdinalIgnoreCase)
        {
            "redist", "redistributable", "directx", "vcredist", "dotnetfx",
            "_commonredist", "support", "tools", "crashpad", "resources",
            "locales", "swiftshader"
        };

        public AutoDetectService(AppSettings settings) => _settings = settings;

        public async Task<int> ScanAndUpdateAsync()
        {
            var folders = new[]
            {
                _settings.GamesFolderPath,
                _settings.GamesFolderPath2,
                _settings.GamesFolderPath3
            }
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .ToList();

            if (folders.Count == 0) return 0;

            string[] extensions = { "*.exe", "*.iso", "*.bin", "*.chd", "*.nsp", "*.xci", "*.nds", "*.gba", "*.n64", "*.z64", "*.pkg" };

            // FIX — scan folders in parallel when there are multiple configured.
            // Previously scanned sequentially; with 3 large folders this adds up.
            // ConcurrentDictionary is safe for parallel writes.
            var fileLookup = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await Task.WhenAll(folders.Select(folder => Task.Run(() =>
            {
                foreach (string ext in extensions)
                {
                    // FIX — wrap in try/catch per extension so one inaccessible
                    // subdirectory doesn't abort the entire scan
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(folder, ext, SearchOption.AllDirectories))
                        {
                            string rawName = Path.GetFileNameWithoutExtension(file);
                            if (BlockedFileNames.Contains(rawName)) continue;

                            string dir = Path.GetDirectoryName(file) ?? "";

                            // FIX — use a simple loop instead of LINQ Any() here.
                            // This is called for every file found; eliminating the
                            // delegate overhead and early-exit is measurably faster
                            // across tens of thousands of files.
                            bool blocked = false;
                            foreach (string frag in BlockedPathFragments)
                            {
                                if (dir.Contains(frag, StringComparison.OrdinalIgnoreCase))
                                {
                                    blocked = true;
                                    break;
                                }
                            }
                            if (blocked) continue;

                            string cleaned = CleanTitle(rawName);
                            fileLookup[cleaned] = file; // last match wins
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            })));

            if (fileLookup.Count == 0) return 0;

            using var db = new VaultContext();
            var games = await db.Games
                .Where(g => !g.IsWishlist && !g.IsDownloaded)
                .ToListAsync();

            int updated = 0;

            foreach (var game in games)
            {
                string titleClean = CleanTitle(game.Title);

                if (fileLookup.TryGetValue(titleClean, out string? match))
                {
                    string ext = Path.GetExtension(match).ToLower();
                    if (ext == ".exe")
                        game.ExePath = match;
                    else
                        game.EmulatorPath = match;

                    game.IsDownloaded = true;
                    updated++;
                }
            }

            if (updated > 0)
                await db.SaveChangesAsync();

            return updated;
        }

        private static string CleanTitle(string title)
        {
            return title
                .Replace(":", "")
                .Replace("-", " ")
                .Replace("_", " ")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("the ", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
        }
    }
}