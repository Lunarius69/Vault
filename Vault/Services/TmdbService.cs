using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vault.Models;

namespace Vault.Services
{
    public class TmdbService
    {
        private readonly HttpClient _http = new();
        private readonly string _apiKey;
        private readonly string _cacheFolder;

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
            // Match "Season 1", "Season 2" anywhere in the title
            var match = Regex.Match(title,
                @"Season\s+(\d+)",
                RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int s))
                return s;

            // Match "2nd Season", "3rd Season" etc.
            var match2 = Regex.Match(title,
                @"(\d+)(?:st|nd|rd|th)\s+Season",
                RegexOptions.IgnoreCase);
            if (match2.Success && int.TryParse(match2.Groups[1].Value, out int s2))
                return s2;

            // Match "Part 1", "Part 2" etc.
            var match3 = Regex.Match(title,
                @"Part\s+(\d+)",
                RegexOptions.IgnoreCase);
            if (match3.Success && int.TryParse(match3.Groups[1].Value, out int s3))
                return s3;

            return 1;
        }

        public async Task<int?> SearchAsync(string title, bool isSeries, int? year = null)
        {
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

                return results[0].GetProperty("id").GetInt32();
            }
            catch { return null; }
        }

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

        public async Task<string?> DownloadPosterAsync(int itemId, string tmdbPath)
        {
            if (string.IsNullOrEmpty(tmdbPath)) return null;
            string localPath = Path.Combine(_cacheFolder, $"{itemId}_poster.jpg");
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

        public async Task<string?> DownloadBannerAsync(int itemId, string tmdbPath)
        {
            if (string.IsNullOrEmpty(tmdbPath)) return null;
            string localPath = Path.Combine(_cacheFolder, $"{itemId}_banner.jpg");
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