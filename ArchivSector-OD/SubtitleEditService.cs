using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ArchivSector_OD
{
    // SRT subtitles from a Movie-Only MKV (v4.0 beta 6).
    //
    // A DVD's or Blu-ray's subtitles are pictures (VobSub on DVD, PGS on
    // Blu-ray), not text, so making an .srt means reading those pictures (OCR).
    // That's done by Subtitle Edit, a free program the user installs
    // themselves (like MakeMKV): ArchivSector-OD doesn't do OCR of its own.
    //
    // For each subtitle track in the MKV:
    //   1. mkvextract (from MKVToolNix, next to mkvmerge) copies the track out
    //      as it is: a .sup (Blu-ray), a .sub + .idx (DVD), or an .srt when the
    //      track is already text.
    //   2. Subtitle Edit's command line turns a picture track into an .srt:
    //      SubtitleEdit /convert "x.sup" srt /ocrengine:tesseract ...
    //      (Tesseract first, then Subtitle Edit's own nOCR if Tesseract made
    //      nothing). Subtitle Edit reads with the OCR language last picked in
    //      its own window.
    //   3. The .srt is saved next to the MKV as "Movie.eng.srt" (the track's
    //      language; ".forced" for a forced track; a number when a language
    //      has more than one), the names Plex, Jellyfin and VLC pick up.
    // The temporary files are deleted afterwards. The MKV isn't touched: its
    // own subtitles stay in it, so nothing is lost if the OCR misreads.
    //
    // "Exact" mode just opens the MKV in Subtitle Edit's own window, where its
    // OCR asks about each letter shape it doesn't know (much more accurate).
    //
    // Beta 6: the command line above is Subtitle Edit 4's. Subtitle Edit 5
    // (the new version, built on Avalonia) has no /convert: given one, it
    // opens its normal windows instead, so the "automatic" SRT turned into
    // the user doing the OCR by hand. Make SRT therefore needs 4.0.x (its
    // portable zip comes with Tesseract and English); Exact works with both.
    public static class SubtitleEditService
    {
        /// <summary>SubtitleEdit.exe's main version (4, 5...), or null if it can't be read.</summary>
        public static int? MajorVersion(string exePath)
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(exePath);
                if (v.FileMajorPart > 0) return v.FileMajorPart;
                var p = (v.ProductVersion ?? "").Trim().TrimStart('v', 'V');
                int dot = p.IndexOf('.');
                return int.TryParse(dot > 0 ? p[..dot] : p, out var m) && m > 0 ? m : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Whether this Subtitle Edit can make an .srt by itself from the command
        /// line: version 4 can, version 5 can't. An unknown version is given the
        /// benefit of the doubt.
        /// </summary>
        public static bool CanConvertFromCommandLine(string exePath) =>
            MajorVersion(exePath) is not int m || m < 5;

        public static string? FindExecutable(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            var candidates = new List<string>();
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Subtitle Edit", "SubtitleEdit.exe"));
                candidates.Add(Path.Combine(root, "SubtitleEdit", "SubtitleEdit.exe"));
            }
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return null;
        }

        /// <summary>mkvextract.exe, which MKVToolNix installs next to mkvmerge.exe.</summary>
        public static string? FindMkvextract(string? mkvmergePath)
        {
            if (string.IsNullOrWhiteSpace(mkvmergePath)) return null;
            var dir = Path.GetDirectoryName(mkvmergePath);
            if (dir is null) return null;
            var exe = Path.Combine(dir, "mkvextract.exe");
            return File.Exists(exe) ? exe : null;
        }

        public sealed class SubTrack
        {
            public int Id;                 // mkvmerge's / mkvextract's track ID
            public string Codec = "";      // e.g. "HDMV PGS", "VobSub", "SubRip/SRT"
            public string CodecId = "";    // e.g. "S_HDMV/PGS", "S_VOBSUB", "S_TEXT/UTF8"
            public string Language = "";   // e.g. "eng"
            public string Name = "";
            public bool Forced;
            public bool IsText => CodecId.StartsWith("S_TEXT", StringComparison.OrdinalIgnoreCase);
            public bool IsPgs => CodecId.StartsWith("S_HDMV/PGS", StringComparison.OrdinalIgnoreCase);
            public bool IsVobSub => CodecId.StartsWith("S_VOBSUB", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The MKV's subtitle tracks, from "mkvmerge -J". Null if mkvmerge couldn't read it.</summary>
        public static List<SubTrack>? ListSubtitleTracks(string mkvmergePath, string mkvPath, Action<string>? log = null)
        {
            try
            {
                var (code, stdout, stderr) = Run(mkvmergePath, new[] { "-J", mkvPath }, TimeSpan.FromMinutes(2), null);
                if (code != 0) { log?.Invoke($"SRT: mkvmerge couldn't read {Path.GetFileName(mkvPath)} (exit {code}): {Short(stderr)}"); return null; }
                using var doc = JsonDocument.Parse(stdout);
                var list = new List<SubTrack>();
                if (!doc.RootElement.TryGetProperty("tracks", out var tracks)) return list;
                foreach (var t in tracks.EnumerateArray())
                {
                    if (!t.TryGetProperty("type", out var type) || type.GetString() != "subtitles") continue;
                    var s = new SubTrack
                    {
                        Id = t.TryGetProperty("id", out var id) ? id.GetInt32() : -1,
                        Codec = t.TryGetProperty("codec", out var codec) ? codec.GetString() ?? "" : "",
                    };
                    if (t.TryGetProperty("properties", out var p))
                    {
                        if (p.TryGetProperty("codec_id", out var cid)) s.CodecId = cid.GetString() ?? "";
                        if (p.TryGetProperty("language", out var lang)) s.Language = lang.GetString() ?? "";
                        if (p.TryGetProperty("track_name", out var name)) s.Name = name.GetString() ?? "";
                        if (p.TryGetProperty("forced_track", out var forced) && forced.ValueKind == JsonValueKind.True) s.Forced = true;
                    }
                    if (s.Id >= 0) list.Add(s);
                }
                return list;
            }
            catch (Exception ex)
            {
                log?.Invoke($"SRT: couldn't list the subtitle tracks: {ex.Message}");
                return null;
            }
        }

        public sealed class Outcome
        {
            public List<string> Made = new();       // the .srt files saved
            public List<string> Problems = new();   // tracks that didn't become an .srt, and why
            public bool Cancelled;
        }

        /// <summary>
        /// Makes an .srt next to the MKV for each subtitle track. onProcess is
        /// given each program as it starts (so Cancel can stop it); progress
        /// gets "track 2 of 5 (eng)" lines.
        /// </summary>
        public static Outcome MakeSrts(string subtitleEditPath, string mkvmergePath, string mkvextractPath, string mkvPath,
            Func<bool> cancelled, Action<Process>? onProcess, Action<string>? progress, Action<string>? log)
        {
            var outcome = new Outcome();
            var tracks = ListSubtitleTracks(mkvmergePath, mkvPath, log);
            if (tracks is null) { outcome.Problems.Add("mkvmerge couldn't read the MKV's tracks"); return outcome; }
            if (tracks.Count == 0) { outcome.Problems.Add("the MKV has no subtitle tracks"); return outcome; }

            var dir = Path.GetDirectoryName(mkvPath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(mkvPath);
            var work = Path.Combine(dir, "_srt_temp_" + baseName);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); Directory.CreateDirectory(work); }
            catch (Exception ex) { outcome.Problems.Add("couldn't make a temporary folder: " + ex.Message); return outcome; }

            log?.Invoke($"SRT: {tracks.Count} subtitle track(s) in {Path.GetFileName(mkvPath)}: " +
                        string.Join(", ", tracks.Select(t => $"{t.Id} {t.Codec} {Lang(t)}{(t.Forced ? " forced" : "")}")));

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int n = 0; n < tracks.Count; n++)
                {
                    if (cancelled()) { outcome.Cancelled = true; break; }
                    var t = tracks[n];
                    string label = $"{Lang(t)}{(t.Forced ? ", forced" : "")}";
                    progress?.Invoke($"track {n + 1} of {tracks.Count} ({label})");

                    // 1. Copy the track out as it is.
                    string ext = t.IsText ? ".srt" : t.IsPgs ? ".sup" : t.IsVobSub ? ".sub" : "";
                    if (ext.Length == 0)
                    {
                        outcome.Problems.Add($"track {t.Id} ({t.Codec}): a subtitle format that can't be turned into SRT here");
                        continue;
                    }
                    if (t.CodecId.StartsWith("S_TEXT", StringComparison.OrdinalIgnoreCase) && !t.CodecId.Equals("S_TEXT/UTF8", StringComparison.OrdinalIgnoreCase))
                        ext = t.CodecId.Equals("S_TEXT/ASS", StringComparison.OrdinalIgnoreCase) ? ".ass"
                            : t.CodecId.Equals("S_TEXT/SSA", StringComparison.OrdinalIgnoreCase) ? ".ssa" : ".txt";
                    var extracted = Path.Combine(work, $"track{t.Id}{ext}");
                    var (xcode, _, xerr) = Run(mkvextractPath, new[] { mkvPath, "tracks", $"{t.Id}:{extracted}" }, TimeSpan.FromMinutes(30), onProcess);
                    if (cancelled()) { outcome.Cancelled = true; break; }
                    if (xcode >= 2 || !File.Exists(extracted))
                    {
                        outcome.Problems.Add($"track {t.Id} ({label}): mkvextract couldn't copy it out ({Short(xerr)})");
                        continue;
                    }

                    // 2. Turn it into an .srt.
                    string? srt = null;
                    if (ext == ".srt")
                    {
                        srt = extracted;   // already text
                    }
                    else
                    {
                        foreach (var engine in t.IsText ? new[] { "" } : new[] { "tesseract", "nOCR" })
                        {
                            srt = Convert(subtitleEditPath, extracted, work, engine, onProcess, log);
                            if (srt is not null || cancelled()) break;
                            if (engine.Length > 0) log?.Invoke($"SRT: track {t.Id}: {engine} made nothing");
                        }
                        if (cancelled()) { outcome.Cancelled = true; break; }
                    }
                    if (srt is null || new FileInfo(srt).Length == 0)
                    {
                        outcome.Problems.Add($"track {t.Id} ({label}): Subtitle Edit didn't make an .srt");
                        continue;
                    }

                    // 3. Save it next to the MKV.
                    var final = Path.Combine(dir, FinalName(baseName, t, usedNames));
                    try
                    {
                        File.Copy(srt, final, overwrite: true);
                        outcome.Made.Add(final);
                        log?.Invoke($"SRT: saved {Path.GetFileName(final)}");
                    }
                    catch (Exception ex)
                    {
                        outcome.Problems.Add($"track {t.Id} ({label}): couldn't save {Path.GetFileName(final)}: {ex.Message}");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { /* best effort */ }
            }
            return outcome;
        }

        /// <summary>Opens the MKV in Subtitle Edit's own window, for its exact, letter-by-letter OCR.</summary>
        public static bool OpenInSubtitleEdit(string subtitleEditPath, string mkvPath)
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = subtitleEditPath, UseShellExecute = false };
                psi.ArgumentList.Add(mkvPath);
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        // One picture track to .srt with Subtitle Edit's command line. Returns the .srt, or null.
        private static string? Convert(string subtitleEditPath, string input, string outFolder, string engine, Action<Process>? onProcess, Action<string>? log)
        {
            var outName = Path.GetFileNameWithoutExtension(input) + (engine.Length > 0 ? "." + engine : "") + ".srt";
            var expected = Path.Combine(outFolder, outName);
            var before = Directory.GetFiles(outFolder, "*.srt").ToHashSet(StringComparer.OrdinalIgnoreCase);

            var args = new List<string> { "/convert", input, "srt", $"/outputfolder:{outFolder}", $"/outputfilename:{outName}", "/overwrite" };
            if (engine.Length > 0) args.Add($"/ocrengine:{engine}");
            args.Add("/FixCommonErrors");
            var (code, stdout, stderr) = Run(subtitleEditPath, args, TimeSpan.FromHours(2), onProcess);

            if (File.Exists(expected) && new FileInfo(expected).Length > 0) return expected;
            // Some versions ignore /outputfilename: take the new .srt it wrote instead.
            var made = Directory.GetFiles(outFolder, "*.srt").Where(f => !before.Contains(f) && new FileInfo(f).Length > 0).ToList();
            if (made.Count == 1) return made[0];
            log?.Invoke($"SRT: Subtitle Edit ({(engine.Length > 0 ? engine : "convert")}) exit {code}: {Short(stdout + " " + stderr)}");
            return null;
        }

        // "Movie.eng.srt", "Movie.eng.forced.srt", "Movie.eng.2.srt"; "Movie.srt" when the language is unknown.
        private static string FinalName(string baseName, SubTrack t, HashSet<string> used)
        {
            string lang = t.Language is "" or "und" ? "" : "." + t.Language;
            string stem = baseName + lang + (t.Forced ? ".forced" : "");
            string name = stem + ".srt";
            for (int k = 2; used.Contains(name); k++) name = $"{stem}.{k}.srt";
            used.Add(name);
            return name;
        }

        private static string Lang(SubTrack t) => t.Language is "" or "und" ? "no language" : t.Language;

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 300 ? s[..300] + "…" : s.Length == 0 ? "no details" : s;
        }

        // Runs a program and reads both of its outputs at once (reading one
        // after the other can hang when the second one fills up).
        private static (int Code, string Stdout, string Stderr) Run(string exe, IEnumerable<string> args, TimeSpan timeout, Action<Process>? onProcess)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start " + Path.GetFileName(exe));
            onProcess?.Invoke(proc);
            var errTask = System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd());
            var outTask = System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd());
            if (!proc.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "timed out");
            }
            proc.WaitForExit();
            return (proc.ExitCode, outTask.Result, errTask.Result);
        }
    }
}
