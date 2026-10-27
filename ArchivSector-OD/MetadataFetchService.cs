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
    // A year in brackets at the end of a search, "(1961)", picks that
    // year's film (v3.3).
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

        // v3.3: a year in brackets at the end -- "One Hundred and One
        // Dalmatians (1961)" or "Dune [1984]" -- is taken off the title and
        // sent to TMDB as the release year, so the right version comes
        // back instead of a remake. (Typed into the search, the "(1961)"
        // was searched as part of the title, and TMDB matched the 1996
        // remake.) Only bracketed years count, so a title that ends in a
        // number, like "Blade Runner 2049", is left alone.
        private const string YearPattern = @"^(?<title>.*?\S)\s*[\(\[]\s*(?<year>(?:18|19|20)\d{2})\s*[\)\]]\s*$";

        public static (string Title, string? Year) SplitYear(string query)
        {
            var m = Regex.Match(query.Trim(), YearPattern);
            return m.Success ? (m.Groups["title"].Value.Trim(), m.Groups["year"].Value) : (query.Trim(), null);
        }

        public static async Task<List<MetadataResult>> SearchTmdb(string query, string apiKey, int maxResults = 20)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return new List<MetadataResult>();
            var (title, year) = SplitYear(query);

            // v3.3: titles written with a number are also searched with the
            // number in words, and the results added after the first ones --
            // "101 Dalmatians" finds the 1961 film, whose title on TMDB is
            // "One Hundred and One Dalmatians", as well as the 1996 one.
            var spelled = SpellOutNumbers(title);
            if (spelled is not null)
            {
                var first = await SearchTmdbByTitle(title, year, apiKey, maxResults);
                var second = await SearchTmdbByTitle(spelled, year, apiKey, maxResults);
                var merged = first.Concat(second.Where(r => !first.Any(f => f.Id == r.Id))).ToList();
                // Exact title matches (either way of writing it) go to the top.
                string a = Simplify(title), b = Simplify(spelled);
                var ordered = merged.OrderBy(r => Simplify(r.Title) == a || Simplify(r.Title) == b ? 0 : 1);
                if (year is not null && int.TryParse(year, out int want))
                    ordered = ordered.ThenBy(r => int.TryParse(r.Year, out var y) ? Math.Abs(y - want) : 9999);
                return ordered.ToList();
            }
            return ExactTitleFirst(await SearchTmdbByTitle(title, year, apiKey, maxResults), title, year);
        }

        // v4.0: a film whose title is exactly what was searched for goes to
        // the top, ahead of sequels and spin-offs TMDB may rank higher
        // ("Jumanji" picked "Jumanji: Open World" for the disc's artwork).
        // Otherwise TMDB's order is kept; with a year, the closest year
        // comes first among the exact matches.
        private static List<MetadataResult> ExactTitleFirst(List<MetadataResult> results, string title, string? year)
        {
            string want = Simplify(title);
            if (want.Length == 0) return results;
            var ordered = results.OrderBy(r => Simplify(r.Title) == want ? 0 : 1);
            if (year is not null && int.TryParse(year, out int y0))
                ordered = ordered.ThenBy(r => int.TryParse(r.Year, out var y) ? Math.Abs(y - y0) : 9999);
            return ordered.ToList();
        }

        // Whole numbers from 1 to 999 standing on their own in a title, in
        // words ("101" -> "One Hundred and One"). Null when there are none.
        // Bigger numbers are left alone (they're usually years, like "1917").
        public static string? SpellOutNumbers(string title)
        {
            bool changed = false;
            var result = Regex.Replace(title, @"(?<![\w.])\d{1,3}(?![\w.])", m =>
            {
                int n = int.Parse(m.Value);
                if (n < 1) return m.Value;
                changed = true;
                return NumberWords(n);
            });
            return changed ? result : null;
        }

        private static string Simplify(string t) =>
            Regex.Replace(t.ToLowerInvariant().Replace("-", " "), @"[^a-z0-9 ]", "").Replace(" ", "");

        private static readonly string[] Ones =
        {
            "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten",
            "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
        };
        private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

        private static string NumberWords(int n)
        {
            string under100(int x) => x < 20 ? Ones[x] : Tens[x / 10] + (x % 10 > 0 ? "-" + Ones[x % 10] : "");
            if (n < 100) return under100(n);
            return Ones[n / 100] + " Hundred" + (n % 100 > 0 ? " and " + under100(n % 100) : "");
        }

        private static async Task<List<MetadataResult>> SearchTmdbByTitle(string title, string? year, string apiKey, int maxResults)
        {
            if (year is not null)
            {
                // That title released that year first...
                var exact = await SearchTmdbOnce(title, apiKey, maxResults, year);
                if (exact.Count > 0) return exact;
                // ...then any year, with films from that year (or the
                // closest year) at the top -- release dates differ between
                // countries, so the year typed may be off by one.
                var any = await SearchTmdbWithShorter(title, apiKey, maxResults);
                int want = int.Parse(year);
                return any.OrderBy(r => int.TryParse(r.Year, out var y) ? Math.Abs(y - want) : 9999).ToList();
            }
            return await SearchTmdbWithShorter(title, apiKey, maxResults);
        }

        private static async Task<List<MetadataResult>> SearchTmdbWithShorter(string query, string apiKey, int maxResults)
        {
            var results = await SearchTmdbOnce(query, apiKey, maxResults);
            if (results.Count > 0) return results;

            foreach (var shorter in ShorterQueries(query))
            {
                results = await SearchTmdbOnce(shorter, apiKey, maxResults);
                if (results.Count > 0) break;
            }
            return results;
        }

        private static async Task<List<MetadataResult>> SearchTmdbOnce(string query, string apiKey, int maxResults, string? year = null)
        {
            var results = new List<MetadataResult>();
            if (string.IsNullOrWhiteSpace(apiKey)) return results;

            var url = $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(apiKey)}&query={Uri.EscapeDataString(query)}"
                    + (year is null ? "" : $"&year={year}");
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