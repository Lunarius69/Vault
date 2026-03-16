using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
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
        // Fetch all episodes for a specific season
public async Task<List<TmdbEpisode>> FetchSeasonEpisodesAsync(int tmdbId, int seasonNumber)
{
    try
    {
        string url = $"https://api.themoviedb.org/3/tv/{tmdbId}/season/{seasonNumber}" +
                     $"?api_key={_apiKey}";
        var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new();

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var episodes = new List<TmdbEpisode>();
        if (!doc.RootElement.TryGetProperty("episodes", out var eps)) return new();

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

// Download episode thumbnail
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

        public bool IsConfigured => !string.IsNullOrEmpty(_apiKey);

        // Search TMDB and return the best matching result ID
        public async Task<int?> SearchAsync(string title, bool isSeries)
        {
            try
            {
                string type = isSeries ? "tv" : "movie";
                string encoded = Uri.EscapeDataString(title);
                string url = $"https://api.themoviedb.org/3/search/{type}" +
                             $"?api_key={_apiKey}&query={encoded}&page=1";

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

        // Fetch full details for a movie or TV show
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

                // Title
                details.Title = isSeries
                    ? root.TryGetProperty("name", out var n) ? n.GetString() : null
                    : root.TryGetProperty("title", out var t) ? t.GetString() : null;

                // Overview
                details.Description = root.TryGetProperty("overview", out var ov)
                    ? ov.GetString() : null;

                // Rating
                details.Rating = root.TryGetProperty("vote_average", out var va)
                    ? va.GetDouble() : null;

                // Year
                string? dateStr = isSeries
                    ? (root.TryGetProperty("first_air_date", out var fd) ? fd.GetString() : null)
                    : (root.TryGetProperty("release_date", out var rd) ? rd.GetString() : null);
                if (dateStr?.Length >= 4 && int.TryParse(dateStr[..4], out int yr))
                    details.Year = yr;

                // Poster path
                details.PosterPath = root.TryGetProperty("poster_path", out var pp)
                    ? pp.GetString() : null;

                // Backdrop/banner path
                details.BackdropPath = root.TryGetProperty("backdrop_path", out var bp)
                    ? bp.GetString() : null;

                // Seasons and episodes (TV only)
                if (isSeries)
                {
                    details.TotalSeasons = root.TryGetProperty("number_of_seasons", out var ns)
                        ? ns.GetInt32() : null;
                    details.TotalEpisodes = root.TryGetProperty("number_of_episodes", out var ne)
                        ? ne.GetInt32() : null;
                }

                // Genre
                if (root.TryGetProperty("genres", out var genres) &&
                    genres.GetArrayLength() > 0)
                    details.Genre = genres[0].GetProperty("name").GetString();

                return details;
            }
            catch { return null; }
        }

        public class TmdbEpisode
{
    public int EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int RuntimeMinutes { get; set; }
    public string? ThumbnailPath { get; set; }
}

        // Download and cache poster image, returns local path
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

        // Download and cache banner/backdrop image, returns local path
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
}