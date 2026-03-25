// Services/GameDataEnricher.cs
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
            // Based on your Excel data, you can create a lookup
            // This is a sample - you'll want to build this from your Excel file
        };

        public async Task EnrichMissingGenresAsync()
        {
            using var db = new VaultContext();
            var gamesMissingGenre = await db.Games
                .Where(g => string.IsNullOrEmpty(g.Genre))
                .ToListAsync();

            foreach (var game in gamesMissingGenre)
            {
                // Try to find genre from your dictionary
                if (_genreMap.TryGetValue(game.Title, out var genre))
                {
                    game.Genre = genre;
                }
                else
                {
                    // Try to infer from platform or title
                    game.Genre = InferGenre(game);
                }
            }

            await db.SaveChangesAsync();
            Console.WriteLine($"Updated {gamesMissingGenre.Count} games with genres");
        }

        private string InferGenre(Game game)
        {
            // Infer genre from platform and title patterns
            string title = game.Title.ToLower();

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

            return "Action";
        }
    }
}