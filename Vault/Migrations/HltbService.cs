using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;
using HowLongToBeat;

namespace Vault.Services
{
    public class HltbService
    {
        private static readonly HttpClient _http = new HttpClient();
        private readonly HLTBWebScraper _scraper;

        public HltbService()
        {
            _scraper = new HLTBWebScraper(_http);
        }

        public async Task<(double? Main, double? Sides, double? Complete)> FetchAsync(string title)
        {
            try
            {
                var results = await _scraper.Search(title);
                if (results == null || results.Count == 0)
                    return (null, null, null);

                string titleLower = title.ToLower();
                var best = results
                    .OrderByDescending(r => {
                        string name = r.Title?.ToLower() ?? "";
                        if (name == titleLower) return 3;
                        if (name.Contains(titleLower)) return 2;
                        if (titleLower.Contains(name)) return 1;
                        return 0;
                    })
                    .First();

                return (
                    ParseHours(best.Main),
                    ParseHours(best.MainAndExtras),
                    ParseHours(best.Completionist)
                );
            }
            catch
            {
                return (null, null, null);
            }
        }

        private static double? ParseHours(string? timeStr)
        {
            if (string.IsNullOrWhiteSpace(timeStr)) return null;

            // Remove " Hours" and handle fractions like "10½"
            timeStr = timeStr.Replace(" Hours", "").Replace(" Mins", "").Trim();
            timeStr = timeStr.Replace("½", ".5").Replace("¼", ".25").Replace("¾", ".75");

            if (double.TryParse(timeStr,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double val))
                return Math.Round(val, 1);

            return null;
        }
    }
}