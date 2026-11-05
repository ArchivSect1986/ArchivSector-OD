using System.IO;
using System.Text;

namespace ArchivSector_OD
{
    // Saved telemetry: everything a drive bay shows in "Process Telemetry"
    // (and TargetFill's results) is also written to a log file, so it can be
    // read after the app is closed. One file per day in
    // %AppData%\ArchivSector-OD\Logs, each line tagged with where it came
    // from ("E:", "TargetFill"). Logs older than 30 days are removed
    // automatically. Writing never throws -- a full disk or a locked file
    // just means that line isn't saved.
    public static class LogService
    {
        private static readonly object Gate = new();
        private static bool cleaned;
        private const int KeepDays = 30;

        public static string LogFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchivSector-OD", "Logs");

        public static string TodayLogPath => Path.Combine(LogFolder, $"ArchivSector-OD_{DateTime.Now:yyyy-MM-dd}.log");

        public static void Write(string source, string text)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] [{source}] {text}{Environment.NewLine}";
                lock (Gate)
                {
                    Directory.CreateDirectory(LogFolder);
                    if (!cleaned) { cleaned = true; RemoveOldLogs(); }
                    File.AppendAllText(TodayLogPath, line, Encoding.UTF8);
                }
            }
            catch { /* logging must never get in the way */ }
        }

        private static void RemoveOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-KeepDays);
                foreach (var f in Directory.GetFiles(LogFolder, "ArchivSector-OD_*.log", SearchOption.AllDirectories))   // the readable copies too
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        // Opens today's log in the default text editor (or the Logs folder
        // if nothing has been logged today yet). Beta 4: laid out like
        // ImgBurn's log, newest job first (see ReadableCopy). The saved log
        // itself stays as it's written, oldest line first (each line is
        // added to the end, which never rewrites the file); what opens is a
        // copy, remade every time, in the Logs\Readable folder.
        public static void OpenToday()
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                string target = LogFolder;
                if (File.Exists(TodayLogPath))
                {
                    target = TodayLogPath;
                    try { target = ReadableCopy(TodayLogPath); } catch { }
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch { }
        }

        // ---- The readable copy (beta 4) ----
        // Lines are gathered into jobs, one per drive at a time: a burn (from
        // choosing the image to its verify), a burn queue, a Verify disc, an
        // erase, a game dump, a Drive info report... Jobs are listed newest
        // first, each under its own header, with its lines in time order and
        // marked I (information), W (warning: stopped, skipped) or E (error),
        // as ImgBurn does.

        private sealed class LogJob
        {
            public string Source = "";
            public string Kind = "";
            public TimeSpan Start, Last;
            public bool Started;           // a burn has started (choosing again is a new job)
            public int Order;              // for jobs that start in the same second
            public readonly List<(TimeSpan Time, string Text, List<string> More)> Lines = new();
        }

        private static string ReadableCopy(string logPath)
        {
            string[] raw;
            lock (Gate)
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                raw = sr.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            }

            var jobs = new List<LogJob>();
            var current = new Dictionary<string, LogJob>(StringComparer.OrdinalIgnoreCase);
            LogJob? lastJob = null;
            foreach (var line in raw)
            {
                if (!TryParse(line, out var time, out var src, out var text))
                {
                    // A line under an entry (Drive info's report): it stays with it.
                    if (lastJob is not null && lastJob.Lines.Count > 0) lastJob.Lines[^1].More.Add(line);
                    continue;
                }
                current.TryGetValue(src, out var job);
                string? kind = StartsJob(text, job);
                if (job is null || kind is not null || time - job.Last > TimeSpan.FromMinutes(45) || time < job.Last)
                {
                    job = new LogJob { Source = src, Kind = kind ?? KindOf(text), Start = time, Order = jobs.Count };
                    jobs.Add(job);
                    current[src] = job;
                }
                if (text.StartsWith("Burn started", StringComparison.Ordinal) || text.StartsWith("Burn queue started", StringComparison.Ordinal)) job.Started = true;
                job.Last = time;
                job.Lines.Add((time, text, new List<string>()));
                lastJob = job;
            }

            // The day the log is for: from its name (ArchivSector-OD_2026-11-04.log), else when it was last written.
            var stamp = Path.GetFileNameWithoutExtension(logPath).Replace("ArchivSector-OD_", "");
            if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                day = File.GetLastWriteTime(logPath).Date;

            var sb = new StringBuilder();
            sb.AppendLine(@"; //****************************************\\");
            sb.AppendLine($";   ArchivSector-OD - Log ({AppVersionText()})");
            sb.AppendLine($";   {day:dddd, dd MMMM yyyy}, newest job first");
            sb.AppendLine($";   Opened {DateTime.Now:HH:mm:ss}. The saved log ({Path.GetFileName(logPath)}) is oldest first.");
            sb.AppendLine(@"; \\****************************************//");
            sb.AppendLine(";");
            sb.AppendLine(";   I = information   W = warning   E = error");
            sb.AppendLine(";");
            foreach (var job in jobs.OrderByDescending(j => j.Start).ThenByDescending(j => j.Order))
            {
                string span = job.Last > job.Start ? $"{Hms(job.Start)} - {Hms(job.Last)}" : Hms(job.Start);
                sb.AppendLine();
                sb.AppendLine(@"; //----------------------------------------\\");
                sb.AppendLine($";   {job.Source}  {job.Kind}  ({span})");
                sb.AppendLine(@"; \\----------------------------------------//");
                foreach (var (t, text, more) in job.Lines)
                {
                    sb.AppendLine($"{Level(text)} {Hms(t)} {text}");
                    foreach (var m in more)
                        sb.AppendLine(m.Trim().Length == 0 ? "" : "           " + m);
                }
            }
            sb.AppendLine();
            sb.AppendLine("; End of log.");

            var dir = Path.Combine(LogFolder, "Readable");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileNameWithoutExtension(logPath) + " (readable).log");
            File.WriteAllText(copy, sb.ToString(), Encoding.UTF8);
            return copy;
        }

        // "[12:34:56] [G:] text" -> its parts.
        private static bool TryParse(string line, out TimeSpan time, out string source, out string text)
        {
            time = default; source = ""; text = "";
            if (line.Length < 13 || line[0] != '[' || line[9] != ']' || line[10] != ' ' || line[11] != '[') return false;
            if (!TimeSpan.TryParseExact(line.Substring(1, 8), @"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture, out time)) return false;
            int close = line.IndexOf("] ", 12, StringComparison.Ordinal);
            if (close < 0) return false;
            source = line.Substring(12, close - 12);
            text = line[(close + 2)..];
            return true;
        }

        // The lines that begin a new job, and what kind it is (null: carries on the current one).
        private static string? StartsJob(string text, LogJob? job)
        {
            bool S(string p) => text.StartsWith(p, StringComparison.Ordinal);
            if (S("Burn queue started")) return "Burn queue";
            if (S("Burn: chose")) return job is not null && job.Kind == "Burn" && !job.Started ? null : "Burn";
            if (S("Burn started")) return job is not null && job.Kind == "Burn" && !job.Started ? null : "Burn";
            if (S("Verify: ") && text.Contains(" against ")) return "Verify disc";
            if (S("Erase: ") && text.EndsWith("started", StringComparison.Ordinal)) return "Erase";
            if (S("Game Disc: locating")) return "Game disc dump";
            if (S("Game Disc: dump started")) return job is not null && job.Kind == "Game disc dump" && job.Lines.Count == 1 ? null : "Game disc dump";
            if (S("Drive info:")) return "Drive info";
            if (S("Movie-Only: resolving")) return "Movie-Only";
            if (S("Auto-fetching artwork")) return job is not null && job.Kind == "Artwork" ? null : "Artwork";
            if (S("Eject requested") || S("Close tray requested")) return job is not null && job.Kind == "Tray" ? null : "Tray";
            return null;
        }

        // A job that begins with some other line: named after its first word(s).
        private static string KindOf(string text)
        {
            if (text.StartsWith("Eject", StringComparison.Ordinal) || text.StartsWith("Close", StringComparison.Ordinal)) return "Tray";
            int colon = text.IndexOf(':');
            var k = colon > 0 && colon <= 24 ? text[..colon] : "Drive";
            return k switch
            {
                "Direct write" => "Burn",
                _ => k,
            };
        }

        // I, W or E for a line, from what it says.
        private static char Level(string text)
        {
            bool Has(string w) => text.Contains(w, StringComparison.OrdinalIgnoreCase);
            if (Has("lookup failed") || Has("timed out")) return 'W';
            if (Has("failed") || Has("didn't match") || Has("don't match") || Has("doesn't match") || Has("problem sectors") || Has("drive said no") || Has("isn't ready to burn")
                || Has("couldn't") || Has("can't be") || Has("isn't blank") || Has("too big") || Has("not verified")) return 'E';
            if (Has("stopped") || Has("skipped") || Has("cancel") || Has("wasn't fully verified") || Has("took ") && Has(" to answer")) return 'W';
            return 'I';
        }

        private static string Hms(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        private static string AppVersionText()
        {
            try
            {
                var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                return v is null ? "ArchivSector-OD" : $"version {v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "ArchivSector-OD"; }
        }
    }
}
