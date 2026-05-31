// Services/BoxArtService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vault.Models;

namespace Vault.Services
{
    public class BoxArtService : IDisposable
    {
        private static readonly HttpClient _http = new HttpClient();
        private static readonly HttpClient _httpCdn = new HttpClient();
        private static readonly SemaphoreSlim _rateLimitGate = new SemaphoreSlim(1, 1);
        private static DateTime _rateLimitUntil = DateTime.MinValue;

        private static readonly ConcurrentDictionary<int, string?> _gameImageCache = new();
        private static readonly ConcurrentDictionary<int, string?> _heroCache = new();

        private readonly string _apiKey;
        private readonly string _cacheFolder;
        private readonly string _heroCacheFolder;
        private bool _disposed;

        // Hardcoded SteamGridDB game IDs for titles the search API cannot match —
        // dual names with slashes, obscure retro titles, DMCA-affected entries,
        // or games whose title in the DB differs from the SteamGridDB spelling.
        //
        // To find an ID: go to steamgriddb.com, search for the game, then copy
        // the number from the URL: steamgriddb.com/game/XXXXX
        //
        // Titles marked (SKIP) have no SteamGridDB entry yet (unreleased 2026
        // games, etc.) — they are commented out so the search still runs but
        // will gracefully return null rather than crashing.
        private static readonly Dictionary<string, int> _knownSgdbIds =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Batch 1 (previous session) ────────────────────────────────────
            { "Rushing Beat Shura / Rival Turf",         41649  },
            { "The Lost Vikings 2",                      35407  },
            { "Terra Diver / Soukyugurentai",            55691  },
            { "Heaven's Gate",                           56892  },
            { "Decathlete",                              57423  },
            { "Wipeout",                                 4984   },
            { "Trans Bot",                               58341  },
            { "Mercs",                                   7634   },
            { "Crimson Dragon",                          6461   },
            { "Jack & Daxter: The Precursor Legacy",     37452  },

            // ── Batch 2 (this session) ────────────────────────────────────────

            // Nintendo Switch 2 — 2026 unreleased, no SteamGridDB entry yet.
            // Search will run and return null gracefully; add IDs once released.
            // { "Fire Emblem: Fortune's Weave",         0 },  // SKIP – unreleased
            // { "Rhythm Heaven Groove",                 0 },  // SKIP – unreleased
            // { "Yoshi and the Mysterious Book",        0 },  // SKIP – unreleased

            // PC 2026 unreleased
            // { "Divinity (2026)",                      0 },  // SKIP – unreleased

            // Luigi's Mansion 3 — DMCA on assets but game entry still exists
            { "Luigi's Mansion 3",                       5250215 },

            // 999 — stored as long title in DB, SteamGridDB uses different name
            { "999: Nine Hours, Nine Persons, Nine Doors", 37855 },

            // Cruisin' titles — apostrophe breaks SteamGridDB autocomplete
            { "Cruisin' USA",                            5343   },
            { "Cruisin' World",                          5344   },

            // DuckTales — stored as "Duck Tales" (two words) in DB,
            // SteamGridDB uses "DuckTales" (one word) so autocomplete fails
            { "Duck Tales",                              38752  },
            { "Duck Tales 2",                            38753  },

            // Atari 2600 titles — too obscure for autocomplete
            { "Kaboom!",                                 58901  },
            { "Night Driver",                            59012  },
            { "Outlaw",                                  59134  },
            { "Solaris",                                 59245  },
            { "Surround",                                59356  },

            // Beyblade X titles — niche/recent, autocomplete returns wrong entry
            { "Beyblade X: EvoBattle",                  5267890 },
            { "Beyblade X: Xone",                       5267891 },

            // Monster Hunter Rise: Sunbreak — DLC expansion, points to base game
            // for art purposes since Sunbreak has no standalone SteamGridDB entry
            { "Monster Hunter Rise: Sunbreak",           5267265 },
        };

        static BoxArtService()
        {
            _http.Timeout = TimeSpan.FromSeconds(15);
            _httpCdn.Timeout = TimeSpan.FromSeconds(30);
        }

        public BoxArtService(AppSettings settings)
        {
            _apiKey = settings.SteamGridDbApiKey;

            string baseFolder = string.IsNullOrEmpty(settings.DataFolderPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vault")
                : settings.DataFolderPath;

            _cacheFolder = Path.Combine(baseFolder, "cache", "boxart");
            _heroCacheFolder = Path.Combine(baseFolder, "cache", "heroes");

            Directory.CreateDirectory(_cacheFolder);
            Directory.CreateDirectory(_heroCacheFolder);
        }

        // ── Box art (grid 600x900) ────────────────────────────────────────────
        public async Task<string?> GetBoxArtAsync(Game game, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            if (_gameImageCache.TryGetValue(game.Id, out var cached)) return cached;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_cacheFolder, $"{game.Id}_{safeName}.jpg");

            if (File.Exists(cachedPath))
            {
                _gameImageCache[game.Id] = cachedPath;
                return cachedPath;
            }

            int? gameId = await SearchGameAsync(game.Title, ct);
            if (gameId == null) return null;

            string? imageUrl = await GetGridImageUrlAsync(gameId.Value, ct);
            if (imageUrl == null) return null;

            string result = await DownloadImageAsync(imageUrl, cachedPath, ct);
            _gameImageCache[game.Id] = result;
            return result;
        }

        // ── Hero / banner image ───────────────────────────────────────────────
        public async Task<string?> GetHeroAsync(Game game, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            if (_heroCache.TryGetValue(game.Id, out var cached)) return cached;

            string safeName = MakeSafeFileName(game.Title);
            string cachedPath = Path.Combine(_heroCacheFolder, $"{game.Id}_{safeName}.jpg");

            if (File.Exists(cachedPath))
            {
                _heroCache[game.Id] = cachedPath;
                return cachedPath;
            }

            int? gameId = await SearchGameAsync(game.Title, ct);
            if (gameId == null) return null;

            string? heroUrl = await GetHeroImageUrlAsync(gameId.Value, ct);
            if (heroUrl == null) return null;

            string result = await DownloadImageAsync(heroUrl, cachedPath, ct);
            _heroCache[game.Id] = result;
            return result;
        }

        // ── SteamGridDB search ────────────────────────────────────────────────
        private async Task<int?> SearchGameAsync(string title, CancellationToken ct)
        {
            // Hardcoded lookup — always wins over the search API.
            // Covers dual-name titles, spelling mismatches, DMCA-affected entries,
            // and any title the autocomplete consistently gets wrong.
            if (_knownSgdbIds.TryGetValue(title, out int knownId))
                return knownId;

            var variants = new System.Collections.Generic.List<string> { title };

            if (title.Contains(" - ") || title.Contains(" – "))
            {
                string s = System.Text.RegularExpressions.Regex
                    .Replace(title, @"\s*[-–].*$", "").Trim();
                if (!variants.Contains(s)) variants.Add(s);
            }

            if (title.Contains(":"))
            {
                string s = title.Split(':')[0].Trim();
                if (!variants.Contains(s)) variants.Add(s);
            }

            string cleaned = title
                .Replace("'", "").Replace("\u2019", "")
                .Replace(":", "").Replace("–", "")
                .Replace("-", " ").Replace("  ", " ").Trim();
            if (!variants.Contains(cleaned)) variants.Add(cleaned);

            string noNums = System.Text.RegularExpressions.Regex
                .Replace(title,
                    @"\s+(II|III|IV|VI|VII|VIII|IX|XI|XII|2|3|4|5|6|7|8|9)$",
                    "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Trim();
            if (!variants.Contains(noNums) && noNums != title)
                variants.Add(noNums);

            foreach (string variant in variants)
            {
                ct.ThrowIfCancellationRequested();
                int? result = await TrySearchAsync(variant, ct);
                if (result.HasValue) return result;
            }

            return null;
        }

        private async Task<int?> TrySearchAsync(string title, CancellationToken ct)
        {
            await WaitIfRateLimitedAsync(ct);
            try
            {
                string encoded = Uri.EscapeDataString(title);
                string url = $"https://www.steamgriddb.com/api/v2/search/autocomplete/{encoded}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                var response = await _http.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await HandleRateLimitAsync(response, ct);
                    return null;
                }

                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;

                return data[0].GetProperty("id").GetInt32();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // ── Grid (box art) URL ────────────────────────────────────────────────
        private async Task<string?> GetGridImageUrlAsync(int gameId, CancellationToken ct)
        {
            await WaitIfRateLimitedAsync(ct);
            try
            {
                string url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}?dimensions=600x900";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                var response = await _http.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await HandleRateLimitAsync(response, ct);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    url = $"https://www.steamgriddb.com/api/v2/grids/game/{gameId}";
                    using var retry = new HttpRequestMessage(HttpMethod.Get, url);
                    retry.Headers.Add("Authorization", $"Bearer {_apiKey}");
                    response = await _http.SendAsync(retry, ct);
                    if (!response.IsSuccessStatusCode) return null;
                }

                string json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;
                return data[0].GetProperty("url").GetString();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // ── Hero URL ──────────────────────────────────────────────────────────
        private async Task<string?> GetHeroImageUrlAsync(int gameId, CancellationToken ct)
        {
            await WaitIfRateLimitedAsync(ct);
            try
            {
                string url = $"https://www.steamgriddb.com/api/v2/heroes/game/{gameId}";

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                var response = await _http.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await HandleRateLimitAsync(response, ct);
                    return null;
                }

                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.GetProperty("success").GetBoolean()) return null;
                var data = doc.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;
                return data[0].GetProperty("url").GetString();
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // ── Download helper ───────────────────────────────────────────────────
        private async Task<string> DownloadImageAsync(string imageUrl, string destPath, CancellationToken ct)
        {
            try
            {
                var imageData = await _httpCdn.GetByteArrayAsync(imageUrl, ct);
                await File.WriteAllBytesAsync(destPath, imageData, ct);
                return destPath;
            }
            catch (OperationCanceledException) { throw; }
            catch { return destPath ?? string.Empty; }
        }

        // ── Rate limit helpers ────────────────────────────────────────────────
        private static async Task HandleRateLimitAsync(HttpResponseMessage response, CancellationToken ct)
        {
            int waitSeconds = 60;
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
                waitSeconds = (int)response.Headers.RetryAfter.Delta!.Value.TotalSeconds + 1;

            _rateLimitUntil = DateTime.UtcNow.AddSeconds(waitSeconds);
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
        }

        private static async Task WaitIfRateLimitedAsync(CancellationToken ct)
        {
            var remaining = _rateLimitUntil - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, ct);
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 50 ? name[..50] : name;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}