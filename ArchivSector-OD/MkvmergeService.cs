using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ArchivSector_OD
{
    // Ported from the Python app's find_mkvmerge_exe(),
    // _trim_tracks_with_mkvmerge(), and _reconcile_track_ids_by_language().
    //
    // Exists because makemkvcon's own CLI has no way to select
    // individual tracks by ID (confirmed against its own man page),
    // and its --profile flag, which in principle can filter by
    // language, was tested twice against a real disc and both times
    // extracted every track regardless, with no error to signal it.
    // So MakeMKV is never asked to filter anything -- it always
    // extracts every track, and mkvmerge (a separate, well-documented
    // tool from MKVToolNix) does the actual selection here, as a
    // post-processing pass on the finished file.
    //
    // Disc-side stream_ids don't map onto mkvmerge's own track IDs in
    // the freshly-muxed file -- the two numbering schemes are
    // unrelated -- so this identifies the real file via "mkvmerge -J"
    // (the officially documented JSON identification mode) and pairs
    // its tracks against the scanned disc tracks, matched by type AND
    // language, positionally WITHIN each language group -- not
    // globally. This is per-language rather than one global count
    // check because of a real, confirmed Blu-ray quirk: many Blu-rays
    // author two PGS subtitle streams per language (a full one plus a
    // tiny "forced-only" companion sharing the same original disc
    // stream), and MakeMKV's own default extraction can silently drop
    // some of those companions for some languages but not others,
    // independent of any filtering. Reconciling per-language means
    // languages that extracted cleanly still get trimmed exactly, and
    // only a genuinely ambiguous language falls back to keeping all
    // of its own tracks, rather than risking a wrong cut anywhere.
    public static class MkvmergeService
    {
        public static string? FindExecutable(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            foreach (var name in new[] { "mkvmerge.exe", "mkvmerge" })
            {
                var found = FindOnPath(name);
                if (found is not null) return found;
            }

            foreach (var candidate in new[]
            {
                @"C:\Program Files\MKVToolNix\mkvmerge.exe",
                @"C:\Program Files (x86)\MKVToolNix\mkvmerge.exe",
            })
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string? FindOnPath(string exeName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry -- skip it */ }
            }
            return null;
        }

        public class ActualTrack
        {
            public int Id;
            public string Type = ""; // "audio" / "subtitles" / "video"
            public string Language = "";
        }

        public static List<ActualTrack>? IdentifyTracks(string mkvmergePath, string mkvFilePath, Action<string>? log = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mkvmergePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-J");
                psi.ArgumentList.Add(mkvFilePath);

                using var proc = Process.Start(psi);
                if (proc is null) { log?.Invoke("Failed to start mkvmerge -J."); return null; }

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(60000);

                if (proc.ExitCode != 0)
                {
                    log?.Invoke($"mkvmerge -J failed (exit {proc.ExitCode}), skipping track trim: {Truncate(stderr, 500)}");
                    return null;
                }

                using var doc = JsonDocument.Parse(stdout);
                var result = new List<ActualTrack>();
                if (doc.RootElement.TryGetProperty("tracks", out var tracksEl))
                {
                    foreach (var t in tracksEl.EnumerateArray())
                    {
                        var type = t.TryGetProperty("type", out var typeEl) ? (typeEl.GetString() ?? "") : "";
                        var id = t.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                        var lang = "";
                        if (t.TryGetProperty("properties", out var propsEl) && propsEl.TryGetProperty("language", out var langEl))
                            lang = langEl.GetString() ?? "";
                        result.Add(new ActualTrack { Id = id, Type = type, Language = lang });
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not identify tracks for trimming, skipping: {ex.Message}");
                return null;
            }
        }

        private static string Truncate(string s, int max) => s.Length > max ? s.Substring(0, max) : s;

        public static List<int> ReconcileTrackIds(
            Dictionary<string, List<MakeMkvService.TrackInfo>> scannedByLang,
            Dictionary<string, List<ActualTrack>> actualByLang,
            HashSet<string> chosenStreamIds,
            string kindLabel,
            Action<string>? log = null)
        {
            var keepIds = new List<int>();
            foreach (var (lang, scannedTracks) in scannedByLang)
            {
                actualByLang.TryGetValue(lang, out var actualTracks);
                actualTracks ??= new List<ActualTrack>();

                int chosenCount = scannedTracks.Count(t => chosenStreamIds.Contains(t.StreamId));
                if (chosenCount == 0) continue;

                if (chosenCount == scannedTracks.Count)
                {
                    if (scannedTracks.Count > 0 && actualTracks.Count == 0)
                        log?.Invoke($"[WARNING] All of {kindLabel} language '{lang}' was checked, but MakeMKV's extraction produced none of it -- nothing to keep for this language.");
                    keepIds.AddRange(actualTracks.Select(t => t.Id));
                    continue;
                }

                if (actualTracks.Count == scannedTracks.Count)
                {
                    for (int i = 0; i < scannedTracks.Count; i++)
                        if (chosenStreamIds.Contains(scannedTracks[i].StreamId))
                            keepIds.Add(actualTracks[i].Id);
                }
                else
                {
                    log?.Invoke($"[WARNING] Couldn't precisely trim {kindLabel} language '{lang}' -- MakeMKV's own extraction produced {actualTracks.Count} track(s) for it instead of the {scannedTracks.Count} scanned, and only some were checked, so all of that language's tracks are being kept to avoid cutting the wrong one.");
                    keepIds.AddRange(actualTracks.Select(t => t.Id));
                }
            }
            return keepIds;
        }

        public class TrimResult
        {
            public bool Trimmed;
            public string? Message;
        }

        public static TrimResult TrimTracks(
            string mkvmergePath,
            string mkvFilePath,
            List<MakeMkvService.TrackInfo> scannedAudio,
            List<MakeMkvService.TrackInfo> scannedSub,
            HashSet<string> chosenAudioIds,
            HashSet<string> chosenSubIds,
            Action<string>? log = null)
        {
            if (chosenAudioIds.Count == scannedAudio.Count && chosenSubIds.Count == scannedSub.Count)
            {
                log?.Invoke("All tracks were checked -- nothing to trim, no mkvmerge pass needed.");
                return new TrimResult { Trimmed = false, Message = "All tracks kept" };
            }

            var actual = IdentifyTracks(mkvmergePath, mkvFilePath, log);
            if (actual is null)
                return new TrimResult { Trimmed = false, Message = "Could not identify output tracks" };

            var actualAudio = actual.Where(t => t.Type == "audio").ToList();
            var actualSub = actual.Where(t => t.Type == "subtitles").ToList();

            Dictionary<string, List<T>> GroupByLang<T>(List<T> list, Func<T, string> langFn) =>
                list.GroupBy(t => (langFn(t) ?? "").Trim().ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());

            var scannedAudioByLang = GroupByLang(scannedAudio, t => t.LangCode);
            var actualAudioByLang = GroupByLang(actualAudio, t => t.Language);
            var scannedSubByLang = GroupByLang(scannedSub, t => t.LangCode);
            var actualSubByLang = GroupByLang(actualSub, t => t.Language);

            var keepAudioIds = ReconcileTrackIds(scannedAudioByLang, actualAudioByLang, chosenAudioIds, "audio", log);
            var keepSubIds = ReconcileTrackIds(scannedSubByLang, actualSubByLang, chosenSubIds, "subtitle", log);

            if (scannedAudio.Count > 0 && keepAudioIds.Count == 0)
            {
                log?.Invoke("[WARNING] Track trim would remove all audio -- skipping to avoid producing a silent file.");
                return new TrimResult { Trimmed = false, Message = "Would remove all audio -- skipped" };
            }

            var trimmedPath = mkvFilePath + ".trimmed.mkv";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = mkvmergePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add(trimmedPath);
                if (keepAudioIds.Count > 0)
                {
                    psi.ArgumentList.Add("--audio-tracks");
                    psi.ArgumentList.Add(string.Join(",", keepAudioIds));
                }
                if (scannedSub.Count > 0)
                {
                    if (keepSubIds.Count > 0)
                    {
                        psi.ArgumentList.Add("--subtitle-tracks");
                        psi.ArgumentList.Add(string.Join(",", keepSubIds));
                    }
                    else
                    {
                        psi.ArgumentList.Add("--no-subtitles");
                    }
                }
                psi.ArgumentList.Add(mkvFilePath);

                log?.Invoke($"Trimming to exact tracks: {keepAudioIds.Count} audio, {keepSubIds.Count} subtitle");

                using var proc = Process.Start(psi);
                if (proc is null) return new TrimResult { Trimmed = false, Message = "Failed to start mkvmerge" };

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(600000);

                // mkvmerge exit codes: 0 = success, 1 = success with warnings, 2 = failure.
                if ((proc.ExitCode != 0 && proc.ExitCode != 1) || !File.Exists(trimmedPath))
                {
                    log?.Invoke($"[WARNING] mkvmerge trim failed (exit {proc.ExitCode}), keeping untrimmed file: {Truncate(!string.IsNullOrWhiteSpace(stderr) ? stderr : stdout, 500)}");
                    try { if (File.Exists(trimmedPath)) File.Delete(trimmedPath); } catch { }
                    return new TrimResult { Trimmed = false, Message = "mkvmerge trim failed" };
                }

                try
                {
                    File.Move(trimmedPath, mkvFilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[WARNING] Could not swap in the trimmed file: {ex.Message}");
                    return new TrimResult { Trimmed = false, Message = "Could not swap trimmed file in" };
                }

                var msg = $"Trimmed to exactly {keepAudioIds.Count} audio + {keepSubIds.Count} subtitle track(s).";
                log?.Invoke(msg);
                return new TrimResult { Trimmed = true, Message = msg };
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARNING] mkvmerge trim failed to run: {ex.Message}");
                return new TrimResult { Trimmed = false, Message = $"mkvmerge trim failed to run: {ex.Message}" };
            }
        }
    }
}