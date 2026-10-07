using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArchivSector_OD
{
    // "Check for Updates": asks GitHub for the latest published release
    // and compares its tag (e.g. "v2.1") to this build's version. It also
    // picks out the release's .zip download (and its SHA-256 checksum,
    // when GitHub provides one) so UpdateInstallService can install it.
    // Runs automatically at startup (Settings can turn that off) and from
    // the Settings window's Check for Updates button.
    //
    // This build's version comes from <Version> in the .csproj, so that
    // one line is the single place to bump it for a new release -- the
    // Settings window reads it from here too.
    public static class UpdateCheckService
    {
        // Fill this in once the GitHub repository exists, as "owner/repo"
        // -- e.g. "ArchivSect1986/ArchivSector-OD". While it's blank, the
        // Check for Updates button just says updates can't be checked yet.
        public const string GitHubRepo = "ArchivSect1986/ArchivSector-OD";

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub's API rejects requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ArchivSector-OD-UpdateCheck");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static Version CurrentVersion =>
            Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

        public static string CurrentVersionDisplay => Display(CurrentVersion);

        public enum UpdateStatus { NotConfigured, UpToDate, UpdateAvailable, NoReleases, Failed }

        public class UpdateResult
        {
            public UpdateStatus Status;
            public string LatestVersion = "";
            public string ReleaseUrl = "";
            public string Message = "";

            // The release's .zip download, for installing it in place.
            // Empty when the release has no .zip attached.
            public string DownloadUrl = "";
            public string AssetName = "";
            public long AssetSize;
            public string Sha256 = ""; // lowercase hex, or "" if GitHub gave none
        }

        public static async Task<UpdateResult> CheckAsync()
        {
            if (string.IsNullOrWhiteSpace(GitHubRepo))
            {
                return new UpdateResult
                {
                    Status = UpdateStatus.NotConfigured,
                    Message = "Update checking isn't set up yet (no GitHub repository configured).",
                };
            }

            try
            {
                // /releases/latest returns the newest published release,
                // skipping drafts and pre-releases.
                using var resp = await Http.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new UpdateResult
                    {
                        Status = UpdateStatus.NoReleases,
                        Message = "No releases found on GitHub yet (or the repository address is wrong).",
                    };
                }
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                var url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(url))
                    url = $"https://github.com/{GitHubRepo}/releases";

                var latest = ParseTag(tag);
                if (latest is null)
                {
                    return new UpdateResult
                    {
                        Status = UpdateStatus.Failed,
                        Message = $"Couldn't read a version number from the latest release tag (\"{tag}\").",
                    };
                }

                if (latest > CurrentVersion)
                {
                    var result = new UpdateResult
                    {
                        Status = UpdateStatus.UpdateAvailable,
                        LatestVersion = Display(latest),
                        ReleaseUrl = url,
                        Message = $"Version {Display(latest)} is available (you have {CurrentVersionDisplay}).",
                    };
                    ReadZipAsset(root, result);
                    return result;
                }

                return new UpdateResult
                {
                    Status = UpdateStatus.UpToDate,
                    LatestVersion = Display(latest),
                    ReleaseUrl = url,
                    Message = $"You're on the latest version ({CurrentVersionDisplay}).",
                };
            }
            catch (Exception ex)
            {
                return new UpdateResult
                {
                    Status = UpdateStatus.Failed,
                    Message = $"Couldn't check for updates: {ex.Message}",
                };
            }
        }

        // Finds the first .zip attached to the release. GitHub lists a
        // "digest" ("sha256:<hex>") for uploaded files, used to check the
        // download wasn't corrupted.
        private static void ReadZipAsset(JsonElement release, UpdateResult result)
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return;
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                result.AssetName = name;
                result.DownloadUrl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                result.AssetSize = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var len) ? len : 0;
                var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() ?? "" : "";
                if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    result.Sha256 = digest.Substring(7).Trim().ToLowerInvariant();
                return;
            }
        }

        // Accepts tags like "v2.0", "2.0.1" or "v2.1-beta" (any text
        // around the numbers is ignored).
        private static Version? ParseTag(string tag)
        {
            var m = Regex.Match(tag ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            int Part(int i) => m.Groups[i].Success && int.TryParse(m.Groups[i].Value, out var n) ? n : 0;
            return new Version(Part(1), Part(2), Part(3), Part(4));
        }

        // Pads missing parts with 0 -- .NET treats a missing part as
        // smaller than 0, so "2.0" would otherwise count as older than
        // "2.0.0.0" and show a bogus update.
        private static Version Normalize(Version v) =>
            new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        private static string Display(Version v) =>
            v.Revision > 0 ? v.ToString(4) : v.Build > 0 ? v.ToString(3) : v.ToString(2);
    }
}
