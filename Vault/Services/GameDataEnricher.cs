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
    public class GameDataEnricher
    {
        private readonly Dictionary<string, string> _genreMap = new Dictionary<string, string>
        {
            // Populate from your Excel data if needed — used as a fast lookup
            // before falling back to InferGenre()
        };

        public async Task EnrichMissingGenresAsync()
        {
            try
            {
                using var db = new VaultContext();
                var gamesMissingGenre = await db.Games
                    .Where(g => string.IsNullOrEmpty(g.Genre))
                    .ToListAsync();

                int updated = 0;

                foreach (var game in gamesMissingGenre)
                {
                    try
                    {
                        string? genre = null;

                        if (!string.IsNullOrEmpty(game.Title) &&
                            _genreMap.TryGetValue(game.Title, out var mapped))
                        {
                            genre = mapped;
                        }
                        else
                        {
                            genre = InferGenre(game);
                        }

                        if (!string.IsNullOrEmpty(genre))
                        {
                            game.Genre = genre;
                            updated++;
                        }
                    }
                    catch { /* skip any single game that fails */ }
                }

                await db.SaveChangesAsync();
                System.Diagnostics.Debug.WriteLine($"[GameDataEnricher] Updated {updated} games with genres");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GameDataEnricher] EnrichMissingGenresAsync error: {ex}");
            }
        }

        private static string? InferGenre(Game game)
        {
            try
            {
                string title = game.Title?.ToLower() ?? "";

                if (title.Contains("mario") || title.Contains("sonic") || title.Contains("kirby"))
                    return "Platformer";
                if (title.Contains("final fantasy") || title.Contains("dragon quest") || title.Contains("pokemon"))
                    return "RPG";
                if (title.Contains("zelda") || title.Contains("metroid"))
                    return "Action-Adventure";
                if (title.Contains("mortal kombat") || title.Contains("street fighter"))
                    return "Fighting";
                if (title.Contains("gran turismo") || title.Contains("forza") || title.Contains("need for speed"))
                    return "Racing";
                if (title.Contains("call of duty") || title.Contains("halo"))
                    return "First-Person Shooter";
                if (title.Contains("tetris") || title.Contains("puzzle"))
                    return "Puzzle";
            }
            catch { }

            return null; // Return null rather than a wrong default
        }
    }
}