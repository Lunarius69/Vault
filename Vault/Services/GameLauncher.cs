using System;
using System.Diagnostics;
using System.IO;
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
                if (string.IsNullOrEmpty(emulator))
                    throw new Exception($"No emulator found for {game.Platform}. Check your emulators folder in Settings.");

                startInfo.FileName = emulator;
                startInfo.Arguments = $"\"{exePath}\"";
                startInfo.WorkingDirectory = Path.GetDirectoryName(emulator) ?? "";
            }

            var process = Process.Start(startInfo)
                ?? throw new Exception("Failed to start process.");

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
                if (minutes < 1) return;

                using var db = new VaultContext();
                var dbGame = await db.Games.FindAsync(game.Id);
                if (dbGame == null) return;

                dbGame.PlaytimeMinutes += minutes;
                dbGame.LastPlayed = DateTime.Now;

                // Update in-memory object too so UI reflects it immediately
                game.PlaytimeMinutes += minutes;
                game.LastPlayed = dbGame.LastPlayed;

                await db.SaveChangesAsync();
            }
            catch { }
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