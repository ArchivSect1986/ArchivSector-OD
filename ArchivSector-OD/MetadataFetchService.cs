using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ArchivSector_OD
{
    // Ported from the Python app's build_search_query(),
    // rawg_search_games(), tmdb_search_movies(),
    // save_artwork_and_metadata(), and get_art_cache_dir()/get_cached_art().
    //
    // Deliberate deviation: the Python app also had an iTunes movie
    // search as a no-key fallback. It was removed here after testing
    // showed Apple's search returning no movie results at all (even for
    // titles Apple sells), so movie lookups are TMDB-only.
    //
    // Caching note: the Python app keys its art cache by disc SERIAL
    // NUMBER; this app hasn't built serial extraction, so the cache
    // here is keyed by a sanitized version of the disc title instead.
    //
    // Beyond the Python app: movie/DVD queries also expand common
    // volume-label abbreviations (XMAS -> CHRISTMAS, VOL -> VOLUME,
    // etc.), and TMDB searches that return nothing are retried with
    // progressively shorter queries (dropping trailing words), so
    // leftover junk at the end of a label doesn't sink the whole search.
    public static class MetadataFetchService
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        static MetadataFetchService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchivSector-OD/1.0");
        }

        private static readonly string[] VolumeJunkTokens =
        {
            "4K", "UHD", "ULTRA HD", "BLU RAY", "BLURAY", "BD", "DVD", "DISC",
            "DISK", "D1", "D2", "CD1", "CD2", "REGION FREE", "RF",
            // Edition/cut markers -- beyond the Python app's original
            // list, added after a real auto-fetch miss on a volume
            // label containing "25TH" + "XMAS".
            "ANNIVERSARY", "EDITION", "SPECIAL", "COLLECTORS", "COLLECTOR'S",
            "DIRECTORS", "DIRECTOR'S", "CUT", "UNRATED", "EXTENDED",
            "REMASTERED", "THEATRICAL",
        };

        // Abbreviations commonly squeezed into disc volume labels
        // (which have tight length limits) that search engines don't
        // expand on their own. Whole-word, case-insensitive. Kept to
        // unambiguous ones only -- e.g. "ST" is left alone since it
        // could be Saint or Street.
        private static readonly (string Pattern, string Replacement)[] Abbreviations =
        {
            (@"\bX[\s-]?MAS\b", "CHRISTMAS"),
            (@"\bVOL\b", "VOLUME"),
            (@"\bPT\b", "PART"),
            (@"\bCOLL\b", "COLLECTION"),
        };

        // For movie/DVD categories, cleans the raw volume label of
        // formatting junk (case markers, disc-type tokens, edition
        // markers, and ordinal numbers like "25TH") that hurts search
        // match quality, after expanding known abbreviations. Games keep
        // their detected title as-is, since that can come from real
        // parsed metadata (PARAM.SFO) rather than a raw volume label.
        public static string BuildSearchQuery(string title, DiscCategory category)
        {
            if (category != DiscCategory.BluRay && category != DiscCategory.Dvd)
                return title;

            var query = title.Replace('_', ' ').Replace('.', ' ');
            foreach (var (pattern, replacement) in Abbreviations)
                query = Regex.Replace(query, pattern, replacement, RegexOptions.IgnoreCase);
            query = Regex.Replace(query, @"\b\d+(ST|ND|RD|TH)\b", " ", RegexOptions.IgnoreCase);
            foreach (var token in VolumeJunkTokens)
                query = Regex.Replace(query, $@"\b{Regex.Escape(token)}\b", " ", RegexOptions.IgnoreCase);
            query = Regex.Replace(query, @"\s+", " ").Trim();
            return string.IsNullOrEmpty(query) ? title.Replace('_', ' ').Trim() : query;
        }

        // Progressively shorter versions of a query, dropping one
        // trailing word at a time: at most 3 retries, never shorter
        // than 2 words (a single word is too vague to trust).
        private static IEnumerable<string> ShorterQueries(string query)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int n = words.Length - 1; n >= 2 && n >= words.Length - 3; n--)
                yield return string.Join(' ', words.Take(n));
        }

        public class MetadataResult
        {
            public string Id { get; set; } = "";
            public string Title { get; set; } = "";
            public string Year { get; set; } = "";
            public string ThumbnailUrl { get; set; } = "";
            public string FullImageUrl { get; set; } = "";
            public string Detail { get; set; } = "";
            public string Source { get; set; } = "";
        }

        public static async Task<List<MetadataResult>> SearchRawg(string query, string apiKey, int maxResults = 10)
        {
            var results = new List<MetadataResult>();
            if (string.IsNullOrWhiteSpace(apiKey)) return results;

            var url = $"https://api.rawg.io/api/games?search={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}&page_size={maxResults}";
            using var resp = await Http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            if (doc.RootElement.TryGetProperty("results", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var img = item.TryGetProperty("background_image", out var bg) ? (bg.GetString() ?? "") : "";
                    var released = item.TryGetProperty("released", out var rel) ? (rel.GetString() ?? "") : "";
                    var platforms = new List<string>();
                    if (item.TryGetProperty("platforms", out var plats) && plats.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in plats.EnumerateArray())
                        {
                            if (p.TryGetProperty("platform", out var plat) && plat.TryGetProperty("name", out var name))
                                platforms.Add(name.GetString() ?? "");
                        }
                    }

                    results.Add(new MetadataResult
                    {
                        Id = item.TryGetProperty("id", out var id) ? id.ToString() : "",
                        Title = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "Unknown") : "Unknown",
                        Year = released.Length >= 4 ? released[..4] : "",
                        ThumbnailUrl = img,
                        FullImageUrl = img,
                        Detail = platforms.Count > 0 ? string.Join(", ", platforms) : "Platform unknown",
                        Source = "RAWG",
                    });
                }
            }
            return results;
        }

        public static async Task<List<MetadataResult>> SearchTmdb(string query, string apiKey, int maxResults = 10)
        {
            var results = await SearchTmdbOnce(query, apiKey, maxResults);
            if (results.Count > 0 || string.IsNullOrWhiteSpace(apiKey)) return results;

            foreach (var shorter in ShorterQueries(query))
            {
                results = await SearchTmdbOnce(shorter, apiKey, maxResults);
                if (results.Count > 0) break;
            }
            return results;
        }

        private static async Task<List<MetadataResult>> SearchTmdbOnce(string query, string apiKey, int maxResults)
        {
            var results = new List<MetadataResult>();
            if (string.IsNullOrWhiteSpace(apiKey)) return results;

            var url = $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(query)}";
            using var resp = await Http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            if (doc.RootElement.TryGetProperty("results", out var items))
            {
                int count = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (count++ >= maxResults) break;
                    var posterPath = item.TryGetProperty("poster_path", out var pp) ? pp.GetString() : null;
                    var releaseDate = item.TryGetProperty("release_date", out var rd) ? (rd.GetString() ?? "") : "";
                    var overview = item.TryGetProperty("overview", out var ov) ? (ov.GetString() ?? "") : "";

                    results.Add(new MetadataResult
                    {
                        Id = item.TryGetProperty("id", out var id) ? id.ToString() : "",
                        Title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? "Unknown") : "Unknown",
                        Year = releaseDate.Length >= 4 ? releaseDate[..4] : "",
                        ThumbnailUrl = !string.IsNullOrEmpty(posterPath) ? $"https://image.tmdb.org/t/p/w200{posterPath}" : "",
                        FullImageUrl = !string.IsNullOrEmpty(posterPath) ? $"https://image.tmdb.org/t/p/w780{posterPath}" : "",
                        Detail = overview.Length > 120 ? overview[..120] : overview,
                        Source = "TMDB",
                    });
                }
            }
            return results;
        }

        private static string SanitizeForFilesystem(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean.Trim();
        }

        private static string ArtCacheRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "art_cache");

        public static string GetArtCacheDir(string cacheKey) =>
            Path.Combine(ArtCacheRoot, SanitizeForFilesystem(cacheKey));

        public class CachedArt
        {
            public string CoverPath = "";
            public MetadataResult? Metadata;
        }

        public static CachedArt? GetCachedArt(string cacheKey)
        {
            var dir = GetArtCacheDir(cacheKey);
            var metaPath = Path.Combine(dir, "metadata.json");
            if (!File.Exists(metaPath)) return null;

            try
            {
                var json = File.ReadAllText(metaPath);
                var metadata = JsonSerializer.Deserialize<MetadataResult>(json);
                var coverPath = Path.Combine(dir, "cover.jpg");
                return new CachedArt
                {
                    CoverPath = File.Exists(coverPath) ? coverPath : "",
                    Metadata = metadata,
                };
            }
            catch
            {
                return null;
            }
        }

        public static async Task<CachedArt> SaveArtworkAndMetadata(string cacheKey, MetadataResult metadata)
        {
            var dir = GetArtCacheDir(cacheKey);
            Directory.CreateDirectory(dir);

            var coverPath = "";
            if (!string.IsNullOrWhiteSpace(metadata.FullImageUrl))
            {
                try
                {
                    var bytes = await Http.GetByteArrayAsync(metadata.FullImageUrl);
                    coverPath = Path.Combine(dir, "cover.jpg");
                    await File.WriteAllBytesAsync(coverPath, bytes);
                }
                catch
                {
                    coverPath = "";
                }
            }

            var metaPath = Path.Combine(dir, "metadata.json");
            var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metaPath, json);

            return new CachedArt { CoverPath = coverPath, Metadata = metadata };
        }
    }
}