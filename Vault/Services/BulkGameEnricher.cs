// Services/BulkGameEnricher.cs
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class BulkGameEnricher
    {
        private readonly AppSettings _settings;

        public BulkGameEnricher(AppSettings settings)
        {
            _settings = settings;
        }

        public async Task<int> EnrichAllGamesAsync(IProgress<string>? progress = null)
        {
            using var db = new VaultContext();
            var games = await db.Games
                .Where(g => !g.IsWishlist)
                .ToListAsync();

            int updated = 0;
            int total = games.Count;
            int current = 0;

            var hltb = new HltbService();

            foreach (var game in games)
            {
                current++;
                progress?.Report($"Processing {current}/{total}: {game.Title}");

                // Skip if we already have HLTB data and description
                bool hasHltbData = game.HltbMainStory > 0 || game.HltbMainPlusExtra > 0 || game.HltbCompletionist > 0;
                bool hasDescription = !string.IsNullOrEmpty(game.Description);

                if (hasHltbData && hasDescription)
                {
                    continue;
                }

                try
                {
                    var data = await hltb.FetchGameDataAsync(game.Title);
                    if (data != null)
                    {
                        bool changed = false;

                        // Update HLTB times (convert from hours to minutes)
                        if (game.HltbMainStory <= 0 && data.MainStory.HasValue)
                        {
                            game.HltbMainStory = (int)(data.MainStory.Value * 60);
                            changed = true;
                        }

                        if (game.HltbMainPlusExtra <= 0 && data.MainPlusExtra.HasValue)
                        {
                            game.HltbMainPlusExtra = (int)(data.MainPlusExtra.Value * 60);
                            changed = true;
                        }

                        if (game.HltbCompletionist <= 0 && data.Completionist.HasValue)
                        {
                            game.HltbCompletionist = (int)(data.Completionist.Value * 60);
                            changed = true;
                        }

                        if (string.IsNullOrEmpty(game.Description) && !string.IsNullOrEmpty(data.Description))
                        {
                            game.Description = data.Description;
                            changed = true;
                        }

                        if (string.IsNullOrEmpty(game.Genre) && !string.IsNullOrEmpty(data.Genre))
                        {
                            game.Genre = data.Genre;
                            changed = true;
                        }

                        if (changed)
                        {
                            updated++;
                        }

                        // Rate limit to avoid being blocked
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Error: {game.Title} - {ex.Message}");
                }
            }

            await db.SaveChangesAsync();
            return updated;
        }
    }
}