using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ArchivSector_OD
{
    // Ported from the Python app's find_makemkv_exe(),
    // _resolve_makemkv_disc_index(), _scan_all_movie_titles(), and
    // _poll_decrypt_progress(). Disc-index matching strips ':' and
    // '\' from both sides before comparing drive letters.
    //
    // RenameNewestMkv is new: ported from the rename step inside the
    // Python app's _makemkv_mkv_extract_worker(), a real gap found on
    // a fresh audit -- MakeMKV always writes its own auto-generated
    // filename (something like "Title_t00.mkv"), which this app
    // previously left untouched inside an otherwise title-named
    // folder. Picks by newest write time, not just the first .mkv
    // found, specifically because a leftover file from a prior failed
    // attempt sitting in the same folder could otherwise get renamed
    // instead of the file this run actually just produced.
    //
    // LibreDriveLine: ported from the Python app's
    // _parse_makemkv_progress() check. When MakeMKV prints "Using
    // LibreDrive mode" during a backup or extraction, that line is
    // returned on the result so the bay can confirm LibreDrive on its
    // own, instead of relying only on the manual checkbox.
    public static class MakeMkvService
    {
        private static string? FindLibreDriveLine(string output)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.IndexOf("using libredrive mode", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // Robot-mode lines look like MSG:1011,0,1,"Using LibreDrive mode (...)",...
                // -- pull out just the human-readable quoted part when present.
                var parts = line.Split('"');
                return (parts.Length >= 2 ? parts[1] : line).Trim();
            }
            return null;
        }

        public static string? FindExecutable(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            foreach (var name in new[] { "makemkvcon64.exe", "makemkvcon.exe" })
            {
                var found = FindOnPath(name);
                if (found is not null) return found;
            }

            foreach (var candidate in new[]
            {
                @"C:\Program Files\MakeMKV\makemkvcon64.exe",
                @"C:\Program Files (x86)\MakeMKV\makemkvcon64.exe",
                @"C:\Program Files\MakeMKV\makemkvcon.exe",
                @"C:\Program Files (x86)\MakeMKV\makemkvcon.exe",
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

        public class DiscIndexResult
        {
            public string? DiscIndex;
            public string? ErrorMessage;
            public bool Success => DiscIndex is not null;
        }

        public static DiscIndexResult ResolveDiscIndex(string exePath, string driveLetter)
        {
            var target = driveLetter.ToUpperInvariant().TrimEnd(':', '\\');

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add("disc:-1");

            try
            {
                using var proc = Process.Start(psi);
                if (proc is null)
                    return new DiscIndexResult { ErrorMessage = "Failed to start makemkvcon." };

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                bool exited = proc.WaitForExit(30000);
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    return new DiscIndexResult { ErrorMessage = "Disc index resolution timed out after 30 seconds." };
                }

                foreach (var rawLine in stdout.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (!line.StartsWith("DRV:")) continue;

                    var segments = line.Split('"');
                    for (int i = 1; i < segments.Length; i += 2)
                    {
                        var field = segments[i].Trim().ToUpperInvariant().TrimEnd(':', '\\');
                        if (field == target)
                        {
                            var indexPart = line.Substring(4).Split(',')[0].Trim();
                            return new DiscIndexResult { DiscIndex = indexPart };
                        }
                    }
                }

                return new DiscIndexResult
                {
                    ErrorMessage = stdout.Length == 0
                        ? "No output from makemkvcon -- check it's installed and licensed."
                        : $"No DRV: line matched drive {driveLetter}:."
                };
            }
            catch (Exception ex)
            {
                return new DiscIndexResult { ErrorMessage = $"Disc index resolution failed: {ex.Message}" };
            }
        }

        public class BackupResult
        {
            public bool Success;
            public string? OutputFolder;
            public string? ErrorMessage;
            public string? LibreDriveLine;
        }

        public static BackupResult RunBackup(string exePath, string discIndex, string outputFolder, string? destinationOverride = null, Action<Process>? onProcessStarted = null)
        {
            try
            {
                Directory.CreateDirectory(outputFolder);
                var destinationArg = destinationOverride ?? outputFolder;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add("backup");
                psi.ArgumentList.Add("--decrypt");
                psi.ArgumentList.Add($"disc:{discIndex}");
                psi.ArgumentList.Add(destinationArg);

                using var proc = Process.Start(psi);
                if (proc is null)
                    return new BackupResult { ErrorMessage = "Failed to start makemkvcon." };

                onProcessStarted?.Invoke(proc);

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                var libreLine = FindLibreDriveLine(stdout);

                bool hasFiles = Directory.Exists(outputFolder) &&
                                 Directory.EnumerateFileSystemEntries(outputFolder).Any();
                bool success = proc.ExitCode == 0 && hasFiles;

                if (!success)
                {
                    var captured = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    return new BackupResult
                    {
                        LibreDriveLine = libreLine,
                        ErrorMessage = string.IsNullOrWhiteSpace(captured)
                            ? "MakeMKV backup failed (no output captured)."
                            : captured.Length > 300 ? captured.Substring(captured.Length - 300) : captured
                    };
                }

                return new BackupResult { Success = true, OutputFolder = outputFolder, LibreDriveLine = libreLine };
            }
            catch (Exception ex)
            {
                return new BackupResult { ErrorMessage = $"Backup failed: {ex.Message}" };
            }
        }

        public class TrackInfo
        {
            public string StreamId = "";
            public string Type = "";
            public string LangCode = "";
            public string LangName = "";
            public string Descriptor = "";
        }

        public class TitleInfo
        {
            public string Index = "";
            public int Seconds;
            public long SizeBytes;
            public string Name = "";
            public List<TrackInfo> Tracks = new();
        }

        public static List<TitleInfo> ScanTitles(string exePath, string discIndex)
        {
            var titles = new Dictionary<string, TitleInfo>();
            var tracksByTitle = new Dictionary<string, Dictionary<string, TrackInfo>>();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("info");
            psi.ArgumentList.Add($"disc:{discIndex}");

            using var proc = Process.Start(psi);
            if (proc is null) return new List<TitleInfo>();

            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(240000);

            foreach (var rawLine in stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                if (line.StartsWith("TINFO:"))
                {
                    var parts = line.Substring("TINFO:".Length).Split(',', 4);
                    if (parts.Length < 4) continue;

                    var titleIdx = parts[0].Trim();
                    var attrId = parts[1];
                    var rawValue = parts[3];
                    var value = rawValue.Contains('"') ? rawValue.Split('"')[1] : rawValue.Trim();

                    if (!titles.TryGetValue(titleIdx, out var entry))
                    {
                        entry = new TitleInfo { Index = titleIdx };
                        titles[titleIdx] = entry;
                    }

                    if (attrId == "11" && long.TryParse(value, out var size))
                        entry.SizeBytes = size;
                    else if (attrId == "9")
                    {
                        var bits = value.Split(':');
                        int seconds = 0;
                        bool ok = true;
                        foreach (var b in bits)
                        {
                            if (!int.TryParse(b, out var n)) { ok = false; break; }
                            seconds = seconds * 60 + n;
                        }
                        if (ok) entry.Seconds = seconds;
                    }
                    else if (attrId == "2")
                        entry.Name = value;
                }
                else if (line.StartsWith("SINFO:"))
                {
                    var parts = line.Substring("SINFO:".Length).Split(',', 5);
                    if (parts.Length < 5) continue;

                    var titleIdx = parts[0].Trim();
                    var streamId = parts[1].Trim();
                    var attrId = parts[2];
                    var rawValue = parts[4];
                    var value = rawValue.Contains('"') ? rawValue.Split('"')[1] : rawValue.Trim();

                    var titleTracks = tracksByTitle.TryGetValue(titleIdx, out var tt)
                        ? tt
                        : tracksByTitle[titleIdx] = new Dictionary<string, TrackInfo>();

                    if (!titleTracks.TryGetValue(streamId, out var track))
                    {
                        track = new TrackInfo { StreamId = streamId };
                        titleTracks[streamId] = track;
                    }

                    if (attrId == "1") track.Type = value;
                    else if (attrId == "3") track.LangCode = value;
                    else if (attrId == "4") track.LangName = value;
                    else if (attrId == "30") track.Descriptor = value;
                }
            }

            foreach (var (titleIdx, entry) in titles)
            {
                if (tracksByTitle.TryGetValue(titleIdx, out var tt))
                    entry.Tracks = tt.Values.ToList();
            }

            return titles.Values.OrderByDescending(t => t.Seconds).ToList();
        }

        public class ExtractResult
        {
            public bool Success;
            public string? OutputFolder;
            public string? ErrorMessage;
            public string? LibreDriveLine;
        }

        public static ExtractResult ExtractTitle(string exePath, string discIndex, string titleIndex, string outputFolder, Action<Process>? onProcessStarted = null)
        {
            try
            {
                Directory.CreateDirectory(outputFolder);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add("mkv");
                psi.ArgumentList.Add($"disc:{discIndex}");
                psi.ArgumentList.Add(titleIndex);
                psi.ArgumentList.Add(outputFolder);

                using var proc = Process.Start(psi);
                if (proc is null)
                    return new ExtractResult { ErrorMessage = "Failed to start makemkvcon." };

                onProcessStarted?.Invoke(proc);

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                var libreLine = FindLibreDriveLine(stdout);

                var mkvFiles = Directory.Exists(outputFolder)
                    ? Directory.GetFiles(outputFolder, "*.mkv")
                    : Array.Empty<string>();
                bool success = proc.ExitCode == 0 && mkvFiles.Length > 0;

                if (!success)
                {
                    var captured = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    return new ExtractResult
                    {
                        LibreDriveLine = libreLine,
                        ErrorMessage = string.IsNullOrWhiteSpace(captured)
                            ? "MakeMKV movie extraction failed (no output captured)."
                            : captured.Length > 300 ? captured.Substring(captured.Length - 300) : captured
                    };
                }

                return new ExtractResult { Success = true, OutputFolder = outputFolder, LibreDriveLine = libreLine };
            }
            catch (Exception ex)
            {
                return new ExtractResult { ErrorMessage = $"Movie extraction failed: {ex.Message}" };
            }
        }

        // Finds the most-recently-written .mkv in outputDir and renames
        // it to "{desiredBaseName}.mkv". Returns the new path on
        // success, or null if there was nothing to rename or the
        // rename failed (in which case MakeMKV's original filename is
        // left in place -- a failed rename is never treated as a
        // failed rip).
        public static string? RenameNewestMkv(string outputDir, string desiredBaseName, Action<string>? log = null)
        {
            try
            {
                var mkvFiles = Directory.GetFiles(outputDir, "*.mkv");
                if (mkvFiles.Length == 0) return null;

                if (mkvFiles.Length > 1)
                    log?.Invoke($"[WARNING] Found {mkvFiles.Length} .mkv files in {outputDir} -- picking the most recently written one to rename.");

                var newest = mkvFiles.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                var desiredPath = Path.Combine(outputDir, $"{desiredBaseName}.mkv");

                if (string.Equals(newest, desiredPath, StringComparison.OrdinalIgnoreCase))
                    return newest;

                if (File.Exists(desiredPath))
                {
                    try { File.Delete(desiredPath); } catch { /* best-effort, Move below will surface any real problem */ }
                }

                File.Move(newest, desiredPath);
                return desiredPath;
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARNING] Could not rename the extracted .mkv to '{desiredBaseName}.mkv': {ex.Message}. Keeping MakeMKV's original filename.");
                return null;
            }
        }

        public static long GetFolderSizeBytes(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* skip -- next poll will catch up */ }
            }
            return total;
        }
    }
}