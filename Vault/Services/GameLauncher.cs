using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class GameLauncher
    {
        private readonly AppSettings _settings;

        public GameLauncher(AppSettings settings) => _settings = settings;

        public void Launch(Game game)
        {
            string? exePath = ResolveExePath(game);

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                throw new Exception($"Executable not found. Set the file path first.");

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                UseShellExecute = true
            };

            // If it's a ROM, launch via emulator
            if (IsRomFile(exePath))
            {
                string? emulator = FindEmulator(game.Platform, _settings);
                if (!string.IsNullOrEmpty(emulator))
                {
                    startInfo.FileName = emulator;
                    startInfo.Arguments = $"\"{exePath}\"";
                    startInfo.WorkingDirectory = Path.GetDirectoryName(emulator) ?? "";
                }
                else
                {
                    // FIX: RetroArch wasn't wired in anywhere — platforms like
                    // NES/SNES/Genesis/Game Boy had no candidate emulator at all
                    // above, so Launch() just threw "No emulator found". This
                    // finds retroarch.exe plus a matching core in the emulators
                    // folder and launches straight into the ROM with
                    // "-L <core> <rom>", the same as loading a core + content
                    // manually — so it opens directly into the game instead of
                    // RetroArch's menu, and playtime tracks correctly since
                    // Vault is watching that exact RetroArch process instance.
                    var retroArch = TryResolveRetroArchLaunch(game.Platform, exePath, _settings);
                    if (retroArch == null)
                        throw new Exception(
                            $"No emulator found for {game.Platform}. " +
                            $"Check your emulators folder in Settings — for RetroArch, " +
                            $"make sure retroarch.exe and a matching core are inside it.");

                    startInfo.FileName = retroArch.Value.Exe;
                    startInfo.Arguments = retroArch.Value.Args;
                    startInfo.WorkingDirectory = Path.GetDirectoryName(retroArch.Value.Exe) ?? "";
                }
            }

            var process = Process.Start(startInfo)
                ?? throw new Exception("Failed to start process.");

            // Register PID so the watcher doesn't double-count this session
            ProcessWatcherService.LauncherOwnedPids.Add(process.Id);

            // Track playtime in background
            DateTime startTime = DateTime.Now;
            _ = TrackPlaytimeAsync(game, process, startTime);
        }

        private static async Task TrackPlaytimeAsync(Game game, Process process, DateTime startTime)
        {
            try
            {
                await process.WaitForExitAsync();
                int minutes = (int)(DateTime.Now - startTime).TotalMinutes;

                ProcessWatcherService.LauncherOwnedPids.Remove(process.Id);

                if (minutes < 1) return;

                using var db = new VaultContext();
                var dbGame = await db.Games.FindAsync(game.Id);
                if (dbGame == null) return;

                dbGame.PlaytimeMinutes += minutes;
                dbGame.LastPlayed = DateTime.Now;

                game.PlaytimeMinutes += minutes;
                game.LastPlayed = dbGame.LastPlayed;

                await db.SaveChangesAsync();
                ProcessWatcherService.NotifyPlaytimeUpdated(game.Id);
            }
            catch
            {
                ProcessWatcherService.LauncherOwnedPids.Remove(process.Id);
            }
        }

        private static string? ResolveExePath(Game game)
        {
            // Direct exe takes priority
            if (!string.IsNullOrEmpty(game.ExePath) && File.Exists(game.ExePath))
                return game.ExePath;

            // ROM path
            if (!string.IsNullOrEmpty(game.EmulatorPath) && File.Exists(game.EmulatorPath))
                return game.EmulatorPath;

            return null;
        }

        private static string? FindEmulator(string platform, AppSettings settings)
        {
            if (string.IsNullOrEmpty(settings.EmulatorsFolderPath) ||
                !Directory.Exists(settings.EmulatorsFolderPath))
                return null;

            // Map platform to common emulator exe names
            string[] candidates = platform?.ToLower() switch
            {
                var p when p.Contains("ps2") => new[] { "pcsx2.exe", "pcsx2-qt.exe" },
                var p when p.Contains("ps1") || p.Contains("playstation 1") => new[] { "duckstation.exe", "epsxe.exe" },
                var p when p.Contains("psp") => new[] { "ppsspp.exe", "ppsspp-qt.exe" },
                var p when p.Contains("switch") => new[] { "yuzu.exe", "ryujinx.exe" },
                var p when p.Contains("gamecube") || p.Contains("wii") => new[] { "dolphin.exe" },
                var p when p.Contains("n64") || p.Contains("nintendo 64") => new[] { "project64.exe", "mupen64plus.exe" },
                var p when p.Contains("3ds") => new[] { "citra.exe", "citra-qt.exe" },
                var p when p.Contains("ds") => new[] { "melonds.exe", "desmume.exe" },
                var p when p.Contains("gba") => new[] { "mgba.exe" },
                var p when p.Contains("xbox 360") => new[] { "xenia.exe" },
                _ => Array.Empty<string>()
            };

            foreach (string exe in candidates)
            {
                // Search recursively in emulators folder
                var matches = Directory.GetFiles(
                    settings.EmulatorsFolderPath, exe,
                    SearchOption.AllDirectories);
                if (matches.Length > 0) return matches[0];
            }

            return null;
        }

        // FIX: platform -> keywords to look for in a RetroArch core filename.
        // Core filenames are fairly consistent when downloaded through
        // RetroArch's own "Online Updater" (e.g. "snes9x_libretro.dll",
        // "fceumm_libretro.dll") — loose keyword matching handles minor
        // naming/version differences without needing an exact filename.
        // If your cores are named differently, tell me the actual filenames
        // and I'll adjust this list.
        private static readonly Dictionary<string, string[]> RetroArchCoreKeywords =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["NES"] = new[] { "fceumm", "nestopia", "quicknes" },
            ["SNES"] = new[] { "snes9x", "bsnes" },
            ["Genesis"] = new[] { "genesis_plus_gx", "picodrive" },
            ["Mega Drive"] = new[] { "genesis_plus_gx", "picodrive" },
            ["Game Boy"] = new[] { "gambatte" },
            ["Game Boy Color"] = new[] { "gambatte" },
            ["GBA"] = new[] { "mgba", "vba_next", "vbam" },
            ["Nintendo 64"] = new[] { "mupen64plus", "parallel_n64" },
            ["Nintendo DS"] = new[] { "melonds", "desmume" },
            ["Nintendo 3DS"] = new[] { "citra" },
            ["GameCube"] = new[] { "dolphin" },
            ["Wii"] = new[] { "dolphin" },
            ["PS1"] = new[] { "pcsx_rearmed", "swanstation", "beetle_psx" },
            ["PS2"] = new[] { "play" },
        };

        // Locates retroarch.exe plus a matching core inside the configured
        // emulators folder, and builds the "-L <core> <rom>" launch args that
        // load straight into the game.
        private static (string Exe, string Args)? TryResolveRetroArchLaunch(
            string platform, string romPath, AppSettings settings)
        {
            if (string.IsNullOrEmpty(settings.EmulatorsFolderPath) ||
                !Directory.Exists(settings.EmulatorsFolderPath))
                return null;

            var raMatches = Directory.GetFiles(
                settings.EmulatorsFolderPath, "retroarch.exe", SearchOption.AllDirectories);
            if (raMatches.Length == 0) return null;
            string retroArchExe = raMatches[0];

            if (string.IsNullOrEmpty(platform) ||
                !RetroArchCoreKeywords.TryGetValue(platform, out var keywords))
                return null;

            var allCores = Directory.GetFiles(
                settings.EmulatorsFolderPath, "*_libretro.dll", SearchOption.AllDirectories);
            if (allCores.Length == 0) return null;

            string? corePath = null;
            foreach (string keyword in keywords)
            {
                corePath = allCores.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f)
                        .Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (corePath != null) break;
            }
            if (corePath == null) return null;

            string args = $"-L \"{corePath}\" \"{romPath}\"";
            return (retroArchExe, args);
        }

        public static bool IsRomFile(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext is ".iso" or ".bin" or ".cue" or ".chd" or
                          ".nsp" or ".xci" or ".elf" or ".pkg" or
                          ".rom" or ".nds" or ".3ds" or ".cia" or
                          ".gba" or ".gb" or ".gbc" or ".n64" or ".z64";
        }
    }
}
