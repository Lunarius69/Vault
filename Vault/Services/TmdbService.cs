using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vault.Database;
using Vault.Models;

namespace Vault.Services
{
    public class TmdbService
    {
        private readonly HttpClient _http = new();
        private readonly string _apiKey;
        private readonly string _cacheFolder;

        // TMDB genre ID for Animation — used to distinguish anime/animated
        // content from live-action results with the same title.
        private const int AnimationGenreId = 16;

        // Hardcoded TMDB IDs for titles that the search API gets wrong —
        // either because two things share a name, the title is too new,
        // or TMDB's search ranking returns the wrong entry first.
        // Key: exact title string as stored in the DB.
        // Value: (tmdbId, isSeries)
        private static readonly Dictionary<string, (int TmdbId, bool IsSeries)> _knownIds =
            new(StringComparer.OrdinalIgnoreCase)
        {
            { "Demon Slayer: Infinity Castle Arc",          (1311031, false) },
            { "Evangelion: 3.0+1.0 Thrice Upon a Time",    (283566,  false) },
            { "Project Sekai Movie: Kowareta Sekai to Utaenai Miku", (1322752, false) },
            { "Witch Watch",                                (261868,  true)  },
            { "Atelier of Witch Hat",                       (196950,  true)  },
            { "Fullmetal Alchemist (2003)",          (37863,  true)  },
           
        };

        public TmdbService(AppSettings settings)
        {
            _apiKey = settings.TmdbApiKey;
            _cacheFolder = Path.Combine(
                string.IsNullOrEmpty(settings.DataFolderPath)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Vault")
                    : settings.DataFolderPath,
                "cache", "media");
            Directory.CreateDirectory(_cacheFolder);
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

        public static int ExtractSeasonNumber(string title)
        {
            var match = Regex.Match(title, @"Season\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int s)) return s;

            var match2 = Regex.Match(title, @"(\d+)(?:st|nd|rd|th)\s+Season", RegexOptions.IgnoreCase);
            if (match2.Success && int.TryParse(match2.Groups[1].Value, out int s2)) return s2;

            var match3 = Regex.Match(title, @"Part\s+(\d+)", RegexOptions.IgnoreCase);
            if (match3.Success && int.TryParse(match3.Groups[1].Value, out int s3)) return s3;

            return 1;
        }

        // ── SearchAsync ──────────────────────────────────────────────────────────
        // Checks the hardcoded _knownIds table first — if the title is there,
        // returns immediately without hitting the TMDB search API at all.
        // Otherwise falls through to the normal genre-filtered search.
        // ────────────────────────────────────────────────────────────────────────
        public async Task<int?> SearchAsync(
            string title,
            bool isSeries,
            int? year = null,
            string? mediaType = null)
        {
            // Hardcoded lookup — always wins over search API
            if (_knownIds.TryGetValue(title, out var known))
                return known.TmdbId;

            try
            {
                string type = isSeries ? "tv" : "movie";
                string encoded = Uri.EscapeDataString(title);
                string url = $"https://api.themoviedb.org/3/search/{type}" +
                             $"?api_key={_apiKey}&query={encoded}&page=1";
                if (year.HasValue)
                    url += $"&year={year}";

                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var results = doc.RootElement.GetProperty("results");
                if (results.GetArrayLength() == 0) return null;

                bool wantAnimation = IsAnimatedType(mediaType);
                bool isLiveAction = IsLiveActionType(mediaType);

                // No genre filtering needed — return first result as before
                if (!wantAnimation && !isLiveAction)
                    return results[0].GetProperty("id").GetInt32();

                // Build list of (id, isAnimated, popularity) for all results
                var candidates = new List<(int id, bool isAnimated, double popularity)>();
                foreach (var result in results.EnumerateArray())
                {
                    int id = result.GetProperty("id").GetInt32();
                    bool isAnimated = false;
                    double popularity = result.TryGetProperty("popularity", out var pop)
                        ? pop.GetDouble() : 0;

                    if (result.TryGetProperty("genre_ids", out var genreIds))
                        foreach (var g in genreIds.EnumerateArray())
                            if (g.GetInt32() == AnimationGenreId) { isAnimated = true; break; }

                    candidates.Add((id, isAnimated, popularity));
                }

                if (wantAnimation)
                {
                    // First: prefer animated results
                    var animatedMatch = candidates.FirstOrDefault(c => c.isAnimated);
                    if (animatedMatch.id != 0) return animatedMatch.id;

                    // Second: if year is known, find by exact year match regardless of genre
                    // (some anime on TMDB are miscategorised and lack genre 16)
                    if (year.HasValue)
                    {
                        foreach (var result in results.EnumerateArray())
                        {
                            string? dateStr = result.TryGetProperty("release_date", out var rd)
                                ? rd.GetString()
                                : result.TryGetProperty("first_air_date", out var fd)
                                    ? fd.GetString() : null;

                            if (dateStr?.Length >= 4 &&
                                int.TryParse(dateStr[..4], out int resultYear) &&
                                resultYear == year.Value)
                                return result.GetProperty("id").GetInt32();
                        }
                    }

                    // Last resort: first result (better than blank)
                    return candidates[0].id;
                }
                else // live-action
                {
                    var nonAnimated = candidates.FirstOrDefault(c => !c.isAnimated);
                    return nonAnimated.id != 0 ? nonAnimated.id : candidates[0].id;
                }
            }
            catch { return null; }
        }

        // ── ResetWrongTmdbMatchesAsync ────────────────────────────────────────────
        public static async Task<int> ResetWrongTmdbMatchesAsync(params string[] titles)
        {
            using var db = new VaultContext();
            int fixed_ = 0;

            foreach (string title in titles)
            {
                var items = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .ToListAsync(
                        db.MediaItems.Where(m => m.Title == title));

                foreach (var item in items)
                {
                    TryDeleteFile(item.PosterPath);
                    TryDeleteFile(item.BannerPath);

                    item.TmdbId = 0;
                    item.TmdbIds = null;
                    item.PosterPath = null;
                    item.BannerPath = null;
                    item.Description = null;
                    item.TmdbRating = null;
                    fixed_++;
                }
            }

            if (fixed_ > 0) await db.SaveChangesAsync();
            return fixed_;
        }

        private static void TryDeleteFile(string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static bool IsAnimatedType(string? mediaType) =>
            mediaType is "Anime" or "AnimeMovie" or "AnimatedSeries" or "AnimatedMovie";

        private static bool IsLiveActionType(string? mediaType) =>
            mediaType is "Movie" or "Show";

        // ── FetchDetailsAsync ────────────────────────────────────────────────────
        public async Task<TmdbDetails?> FetchDetailsAsync(int tmdbId, bool isSeries)
        {
            try
            {
                string type = isSeries ? "tv" : "movie";
                string url = $"https://api.themoviedb.org/3/{type}/{tmdbId}" +
                             $"?api_key={_apiKey}";

                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var details = new TmdbDetails();

                details.Title = isSeries
                    ? root.TryGetProperty("name", out var n) ? n.GetString() : null
                    : root.TryGetProperty("title", out var t) ? t.GetString() : null;

                details.Description = root.TryGetProperty("overview", out var ov)
                    ? ov.GetString() : null;

                details.Rating = root.TryGetProperty("vote_average", out var va)
                    ? va.GetDouble() : null;

                string? dateStr = isSeries
                    ? (root.TryGetProperty("first_air_date", out var fd) ? fd.GetString() : null)
                    : (root.TryGetProperty("release_date", out var rd) ? rd.GetString() : null);
                if (dateStr?.Length >= 4 && int.TryParse(dateStr[..4], out int yr))
                    details.Year = yr;

                details.PosterPath = root.TryGetProperty("poster_path", out var pp)
                    ? pp.GetString() : null;

                details.BackdropPath = root.TryGetProperty("backdrop_path", out var bp)
                    ? bp.GetString() : null;

                if (isSeries)
                {
                    details.TotalSeasons = root.TryGetProperty("number_of_seasons", out var ns)
                        ? ns.GetInt32() : null;
                    details.TotalEpisodes = root.TryGetProperty("number_of_episodes", out var ne)
                        ? ne.GetInt32() : null;
                }

                if (root.TryGetProperty("genres", out var genres) &&
                    genres.GetArrayLength() > 0)
                    details.Genre = genres[0].GetProperty("name").GetString();

                return details;
            }
            catch { return null; }
        }

        public async Task<List<TmdbEpisode>> FetchSeasonEpisodesAsync(
            int tmdbId, int seasonNumber)
        {
            try
            {
                string url = $"https://api.themoviedb.org/3/tv/{tmdbId}" +
                             $"/season/{seasonNumber}?api_key={_apiKey}";
                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new();

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var episodes = new List<TmdbEpisode>();
                if (!doc.RootElement.TryGetProperty("episodes", out var eps))
                    return new();

                foreach (var ep in eps.EnumerateArray())
                {
                    episodes.Add(new TmdbEpisode
                    {
                        EpisodeNumber = ep.TryGetProperty("episode_number", out var en)
                            ? en.GetInt32() : 0,
                        Title = ep.TryGetProperty("name", out var n)
                            ? n.GetString() : null,
                        Description = ep.TryGetProperty("overview", out var ov)
                            ? ov.GetString() : null,
                        RuntimeMinutes = ep.TryGetProperty("runtime", out var rt)
                            ? (rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : 0) : 0,
                        ThumbnailPath = ep.TryGetProperty("still_path", out var sp)
                            ? sp.GetString() : null
                    });
                }
                return episodes;
            }
            catch { return new(); }
        }

        public async Task<string?> DownloadSeasonPosterAsync(
            int itemId, int tmdbId, int seasonNumber)
        {
            try
            {
                string localPath = Path.Combine(_cacheFolder, $"{itemId}_poster.jpg");
                if (File.Exists(localPath)) return localPath;

                string url = $"https://api.themoviedb.org/3/tv/{tmdbId}" +
                             $"/season/{seasonNumber}?api_key={_apiKey}";
                var response = await _http.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("poster_path", out var pp))
                    return null;
                string? posterPath = pp.GetString();
                if (string.IsNullOrEmpty(posterPath)) return null;

                string imgUrl = $"https://image.tmdb.org/t/p/w500{posterPath}";
                byte[] data = await _http.GetByteArrayAsync(imgUrl);
                await File.WriteAllBytesAsync(localPath, data);
                return localPath;
            }
            catch { return null; }
        }

        public async Task<string?> DownloadPosterAsync(int itemId, string tmdbPath, int tmdbId = 0)
        {
            if (string.IsNullOrEmpty(tmdbPath)) return null;

            string cacheKey = tmdbId > 0 ? $"tmdb_{tmdbId}_poster" : $"{itemId}_poster";
            string localPath = Path.Combine(_cacheFolder, $"{cacheKey}.jpg");
            if (File.Exists(localPath)) return localPath;

            try
            {
                string url = $"https://image.tmdb.org/t/p/w500{tmdbPath}";
                byte[] data = await _http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(localPath, data);
                return localPath;
            }
            catch { return null; }
        }

        public async Task<string?> DownloadBannerAsync(int itemId, string tmdbPath, int tmdbId = 0)
        {
            if (string.IsNullOrEmpty(tmdbPath)) return null;

            string cacheKey = tmdbId > 0 ? $"tmdb_{tmdbId}_banner" : $"{itemId}_banner";
            string localPath = Path.Combine(_cacheFolder, $"{cacheKey}.jpg");
            if (File.Exists(localPath)) return localPath;

            try
            {
                string url = $"https://image.tmdb.org/t/p/w1280{tmdbPath}";
                byte[] data = await _http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(localPath, data);
                return localPath;
            }
            catch { return null; }
        }

        public async Task<string?> DownloadThumbnailAsync(int episodeId, string tmdbPath)
        {
            if (string.IsNullOrEmpty(tmdbPath)) return null;
            string localPath = Path.Combine(_cacheFolder, $"ep_{episodeId}_thumb.jpg");
            if (File.Exists(localPath)) return localPath;
            try
            {
                string url = $"https://image.tmdb.org/t/p/w300{tmdbPath}";
                byte[] data = await _http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(localPath, data);
                return localPath;
            }
            catch { return null; }
        }
    }

    public class TmdbDetails
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public double? Rating { get; set; }
        public int? Year { get; set; }
        public string? PosterPath { get; set; }
        public string? BackdropPath { get; set; }
        public int? TotalSeasons { get; set; }
        public int? TotalEpisodes { get; set; }
        public string? Genre { get; set; }
    }

    public class TmdbEpisode
    {
        public int EpisodeNumber { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int RuntimeMinutes { get; set; }
        public string? ThumbnailPath { get; set; }
    }
}