using System.Diagnostics;
using System.Text;

namespace ArchivSector_OD.Burning
{
    // Reading a whole CD to .bin + .cue (v4.0 beta 2), for Copy disc: music
    // CDs, and CDs with a data track plus music tracks (many games), which
    // an ISO can't hold. Every sector is read raw (2352 bytes) into one .bin,
    // and the .cue says where each track and its gap ("INDEX 00") start.
    // The gaps come from the position codes (sub-channel Q) the drive reads
    // with each sector, so the copy keeps them.
    // Enhanced CDs / CD-Extra (v4.0 beta 3): music in session 1, data in
    // session 2. Each session is read up to its own end (the lead-out and
    // the next lead-in between them can't be read and aren't needed), and
    // the .cue marks the sessions the way ImgBurn does (REM SESSION,
    // REM LEAD-OUT, REM LEAD-IN, REM PREGAP), so ImgBurn can burn the copy.
    public static class CdImageReader
    {
        public sealed class TocTrack
        {
            public int Number;
            public bool IsData;
            public int Mode;            // data tracks: 1 or 2
            public long StartLba;       // INDEX 01
            public long PregapLba;      // INDEX 00 (= StartLba when there's no gap)
            public int Session = 1;
        }

        public sealed class Toc
        {
            public List<TocTrack> Tracks = new();
            public long LeadOutLba;
            public int Sessions = 1;
            public List<long> SessionLeadOuts = new();   // where each session ends (full TOC), in order
            public CdText? CdText;                       // album and track names, if the CD has them (beta 4)
            /// <summary>Several sessions, and the drive gave where each one starts and ends.</summary>
            public bool SessionLayoutKnown => Sessions == 1 || (SessionLeadOuts.Count == Sessions
                && Enumerable.Range(1, Sessions).All(s => Tracks.Any(t => t.Session == s))
                && Enumerable.Range(1, Sessions).All(s => Tracks.Where(t => t.Session == s).All(t => t.StartLba < SessionLeadOuts[s - 1]))
                && Enumerable.Range(2, Sessions - 1).All(s => Tracks.First(t => t.Session == s).StartLba > SessionLeadOuts[s - 2]));
            /// <summary>The parts of the disc that hold something: each session, from its start to its lead-out.</summary>
            public List<(long From, long To)> Ranges => Sessions == 1 || !SessionLayoutKnown
                ? new List<(long, long)> { (0, LeadOutLba) }
                : Enumerable.Range(1, Sessions).Select(s => (s == 1 ? 0L : Tracks.First(t => t.Session == s).StartLba, SessionLeadOuts[s - 1])).ToList();
            public long SectorsToRead => Ranges.Sum(r => r.To - r.From);
            public bool HasAudio => Tracks.Any(t => !t.IsData);
            public bool HasData => Tracks.Any(t => t.IsData);
            public long Sectors => LeadOutLba;
            public string Summary
            {
                get
                {
                    int data = Tracks.Count(t => t.IsData), audio = Tracks.Count - data;
                    var kind = data == 0 ? "Music CD" : audio == 0 ? "Data CD" : "CD with data and music";
                    var parts = new List<string>();
                    if (data > 0) parts.Add($"{data} data track{(data == 1 ? "" : "s")}");
                    if (audio > 0) parts.Add($"{audio} music track{(audio == 1 ? "" : "s")}");
                    long secs = (LeadOutLba + 150) / 75;
                    if (Sessions > 1) kind = data > 0 && audio > 0 ? "Enhanced CD (music + data session)" : $"CD with {Sessions} sessions";
                    return $"{kind} · {string.Join(", ", parts)} · {secs / 60}:{secs % 60:D2}";
                }
            }
        }

        public sealed class Progress
        {
            public long SectorsDone, TotalSectors;
            public double BytesPerSec;
            public TimeSpan Remaining;
            public long BadSectors;
            public double Percent => TotalSectors > 0 ? Math.Min(100, 100.0 * SectorsDone / TotalSectors) : 0;
        }

        public sealed class Result
        {
            public bool Ok, Cancelled;
            public string Message = "";
            public string? CuePath, BinPath;
            public long BadSectors;
            public TimeSpan Time;
        }

        private static long BE32(byte[] b, int o) => (long)(int)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        private static int FromBcd(byte b) => (b >> 4) * 10 + (b & 0x0F);

        /// <summary>
        /// The CD's tracks, their gaps and its length, or null with "problem"
        /// set (no CD, not a CD, several sessions, drive busy...).
        /// </summary>
        public static Toc? ReadToc(string driveLetter, out string? problem)
        {
            problem = null;
            var letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
            if (BurnEngine.IsBusy(letter)) { problem = "The drive is busy."; return null; }
            try
            {
                using var drive = ScsiDrive.Open(letter, out problem);
                if (drive is null) return null;
                return ReadToc(drive, out problem);
            }
            catch (Exception ex) { problem = "The CD's track list couldn't be read: " + ex.Message; return null; }
        }

        private static Toc? ReadToc(ScsiDrive drive, out string? problem)
        {
            problem = null;
            var buf = new byte[4 + 100 * 8];
            var rep = drive.Read(new byte[] { 0x43, 0x00, 0x00, 0, 0, 0, 0x01, (byte)(buf.Length >> 8), (byte)buf.Length, 0 }, buf, 10);
            if (!rep.Ok) { problem = "There's no CD with tracks in the drive."; return null; }
            int len = Math.Min(((buf[0] << 8) | buf[1]) + 2, buf.Length);
            var toc = new Toc();
            for (int o = 4; o + 8 <= len; o += 8)
            {
                int no = buf[o + 2];
                long lba = BE32(buf, o + 4);
                bool data = (buf[o + 1] & 0x04) != 0;
                if (no == 0xAA) toc.LeadOutLba = lba;
                else if (no >= 1 && no <= 99) toc.Tracks.Add(new TocTrack { Number = no, IsData = data, StartLba = lba, PregapLba = lba });
            }
            if (toc.Tracks.Count == 0 || toc.LeadOutLba <= 0) { problem = "The CD's track list is empty."; return null; }

            // Sessions (format 1: the first track of the last session).
            var ses = new byte[12];
            rep = drive.Read(new byte[] { 0x43, 0x00, 0x01, 0, 0, 0, 0, 0, 12, 0 }, ses, 10);
            if (rep.Ok) toc.Sessions = Math.Max(1, (int)ses[3]);

            // Several sessions: each track's session, and where each session
            // ends (its lead-out), from the full TOC (format 2). Entries are
            // 11 bytes: session, ADR/control, TNO, POINT, time, 0, PMIN PSEC PFRAME.
            if (toc.Sessions > 1)
            {
                var full = new byte[4 + 11 * 150];
                rep = drive.Read(new byte[] { 0x43, 0x02, 0x02, 0, 0, 0, 0x01, (byte)(full.Length >> 8), (byte)full.Length, 0 }, full, 10);
                if (rep.Ok)
                {
                    int flen = Math.Min(((full[0] << 8) | full[1]) + 2, full.Length);
                    var leadOuts = new SortedDictionary<int, long>();
                    for (int o = 4; o + 11 <= flen; o += 11)
                    {
                        if ((full[o + 1] >> 4) != 1) continue;            // ADR 1 entries only
                        int s = full[o], point = full[o + 3];
                        long at = (full[o + 8] * 60L + full[o + 9]) * 75 + full[o + 10] - 150;
                        if (point == 0xA2) leadOuts[s] = at;
                        else if (point >= 1 && point <= 99)
                        {
                            var t = toc.Tracks.Find(x => x.Number == point);
                            if (t is not null) t.Session = s;
                        }
                    }
                    toc.SessionLeadOuts = leadOuts.Values.ToList();
                }
            }

            // CD-Text: album and track names (beta 4), kept in the copy's .cue.
            try { toc.CdText = CdText.Read(drive); } catch { toc.CdText = null; }

            // Data tracks: mode 1 or mode 2, from the sector header.
            var raw = new byte[2352];
            foreach (var t in toc.Tracks.Where(t => t.IsData))
            {
                long l = t.StartLba;
                rep = drive.Read(new byte[] { 0xBE, 0x00, (byte)(l >> 24), (byte)(l >> 16), (byte)(l >> 8), (byte)l, 0, 0, 1, 0xF8, 0, 0 }, raw, 30);
                t.Mode = rep.Ok && raw[15] == 2 ? 2 : 1;
            }

            // Gaps before tracks 2 and up (within a session; the first track of
            // a later session has the standard 2-second gap after the lead-in).
            for (int i = 1; i < toc.Tracks.Count; i++)
            {
                var t = toc.Tracks[i];
                if (t.Session != toc.Tracks[i - 1].Session) continue;
                long maxGap = Math.Min(t.StartLba - toc.Tracks[i - 1].StartLba - 1, 75 * 20);
                t.PregapLba = t.StartLba - FindGap(drive, t.Number, t.StartLba, maxGap);
            }
            return toc;
        }

        // How many sectors before "start" still belong to track "no" (its
        // INDEX 00 gap). The gap is one stretch, so a binary search on "does
        // the sector k before the start say track no?" finds its length.
        private static long FindGap(ScsiDrive drive, int no, long start, long maxGap)
        {
            if (maxGap <= 0) return 0;
            bool? BelongsTo(long k)
            {
                // Q data only carries the track number in most sectors (ADR 1);
                // if this one carries something else, look at its neighbours.
                foreach (long d in new long[] { 0, 1, -1, 2, -2 })
                {
                    long kk = k + d;
                    if (kk < 1 || kk > maxGap) continue;
                    var q = ReadQ(drive, start - kk);
                    if (q is null) continue;
                    var (adr, track, _) = q.Value;
                    if (adr != 1) continue;
                    return track == no;
                }
                return null;
            }
            if (BelongsTo(1) != true) return 0;
            long lo = 1, hi = maxGap;           // lo: known in the gap
            if (BelongsTo(hi) == true) return hi;
            while (hi - lo > 1)
            {
                long mid = (lo + hi) / 2;
                var b = BelongsTo(mid);
                if (b == true) lo = mid; else hi = mid;
            }
            return lo;
        }

        // The formatted Q sub-channel of one sector: ADR, track, index.
        private static (int Adr, int Track, int Index)? ReadQ(ScsiDrive drive, long lba)
        {
            var buf = new byte[2352 + 16];
            var rep = drive.Read(new byte[] { 0xBE, 0x00, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, 0, 1, 0xF8, 0x02, 0 }, buf, 30);
            if (!rep.Ok) return null;
            int q = 2352;
            return (buf[q] & 0x0F, FromBcd(buf[q + 1]), FromBcd(buf[q + 2]));
        }

        /// <summary>
        /// Reads the whole CD into "folder\name.bin" + "name.cue". The drive is
        /// read as fast as it goes; a sector that won't read after a few tries
        /// becomes zeros and is counted.
        /// </summary>
        public static Task<Result> ReadToBinCueAsync(string driveLetter, string folder, string name, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null) =>
            Task.Factory.StartNew(() =>
            {
                var r = new Result();
                var clock = Stopwatch.StartNew();
                try { ReadToBinCue(driveLetter.TrimEnd(':', '\\').ToUpperInvariant(), folder, name, progress, ct, r, log); }
                catch (Exception ex) { r.Message = "The read stopped: " + ex.Message; }
                r.Time = clock.Elapsed;
                log?.Invoke("Read CD: " + (r.Ok ? $"finished, {r.BadSectors:N0} unreadable sector(s), in {r.Time:hh\\:mm\\:ss}" : r.Message));
                return r;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static void ReadToBinCue(string letter, string folder, string name, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log)
        {
            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }
            var toc = ReadToc(drive, out problem);
            if (toc is null) { r.Message = problem ?? "The CD's track list couldn't be read."; return; }
            if (!toc.SessionLayoutKnown) { r.Message = "This CD has more than one session, but the drive didn't give where each one starts and ends, so it can't be copied here. ImgBurn or Redumper can."; return; }
            log?.Invoke($"Read CD: {letter}: {toc.Summary}; " + string.Join(", ", toc.Tracks.Select(t =>
                (toc.Sessions > 1 ? $"S{t.Session} " : "") +
                $"T{t.Number} {(t.IsData ? "MODE" + t.Mode : "AUDIO")} @{t.StartLba}" + (t.PregapLba < t.StartLba ? $" (gap {t.StartLba - t.PregapLba})" : "")))
                + (toc.Sessions > 1 ? "; sessions end at " + string.Join(", ", toc.SessionLeadOuts) : "")
                + (toc.CdText is not null ? "; CD-Text: " + string.Join(" | ", toc.CdText.Lines().Select(l => $"{l.Label}: {l.Value}")) : ""));

            // Full speed: the drive's own defaults back first (a slower write
            // speed set earlier by a burn, ours or another program's, can
            // stay in force), then "as fast as you can".
            BurnEngine.FullDriveSpeed(drive);
            Directory.CreateDirectory(folder);
            string bin = Path.Combine(folder, name + ".bin"), cue = Path.Combine(folder, name + ".cue");
            var ranges = toc.Ranges;
            long total = toc.SectorsToRead;
            const int N = 26;
            var buf = new byte[N * 2352];
            var one = new byte[2352];
            var lastReport = TimeSpan.Zero;
            var clock = Stopwatch.StartNew();
            var window = new Queue<(TimeSpan T, long Done)>();
            // For the log: the reading speed every 10%, and for the data and
            // music parts, so a slow part shows (1x = 75 sectors a second).
            int lastTenth = 0;
            var tenthClock = Stopwatch.StartNew();
            long tenthFrom = 0;
            long firstAudio = toc.Tracks.Where(t => !t.IsData).Select(t => (long)t.PregapLba).DefaultIfEmpty(toc.LeadOutLba).Min();
            var partClock = Stopwatch.StartNew();
            bool inAudio = firstAudio == 0;
            bool partLog = toc.Sessions == 1;   // data part / music part (an Enhanced CD has its music first)
            string KindAt(long at) => toc.Tracks.LastOrDefault(t => t.PregapLba <= at)?.IsData == true ? "data" : "music";
            long done = 0;
            static string X(long sectors, TimeSpan t) => t.TotalSeconds > 0.5 ? $"{sectors / 75.0 / t.TotalSeconds:0.0}x" : "?";
            using (var fs = new FileStream(bin, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                foreach (var (from, to) in ranges)
                for (long lba = from; lba < to;)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; r.Message = "Stopped."; return; }
                    int n = (int)Math.Min(N, to - lba);
                    var target = n == N ? buf : new byte[n * 2352];
                    long l = lba;
                    var rep = drive.Read(new byte[] { 0xBE, 0x00, (byte)(l >> 24), (byte)(l >> 16), (byte)(l >> 8), (byte)l, 0, 0, (byte)n, 0xF8, 0, 0 }, target, 60);
                    if (rep.Ok) fs.Write(target, 0, n * 2352);
                    else
                    {
                        // One at a time, a few tries each, to find the bad ones.
                        for (int s = 0; s < n; s++)
                        {
                            long at = lba + s;
                            bool ok = false;
                            for (int attempt = 0; attempt < 4 && !ok; attempt++)
                                ok = drive.Read(new byte[] { 0xBE, 0x00, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, 0, 1, 0xF8, 0, 0 }, one, 30).Ok;
                            if (!ok) { Array.Clear(one); r.BadSectors++; }
                            fs.Write(one, 0, 2352);
                        }
                    }
                    lba += n;
                    done += n;
                    if ((int)(done * 10 / Math.Max(1, total)) > lastTenth && done < total)
                    {
                        lastTenth = (int)(done * 10 / total);
                        log?.Invoke($"Read CD: {lastTenth * 10}% after {clock.Elapsed:mm\\:ss}, last 10% at {X(done - tenthFrom, tenthClock.Elapsed)} ({KindAt(lba - 1)})");
                        tenthFrom = done;
                        tenthClock.Restart();
                    }
                    if (partLog && !inAudio && lba >= firstAudio && firstAudio > 0)
                    {
                        inAudio = true;
                        log?.Invoke($"Read CD: data part ({firstAudio:N0} sectors) read at {X(firstAudio, partClock.Elapsed)} on average");
                        partClock.Restart();
                    }
                    var now = clock.Elapsed;
                    if (now - lastReport > TimeSpan.FromMilliseconds(250) || done >= total)
                    {
                        lastReport = now;
                        window.Enqueue((now, done));
                        while (window.Count > 1 && now - window.Peek().T > TimeSpan.FromSeconds(5)) window.Dequeue();
                        var first = window.Peek();
                        double secs = (now - first.T).TotalSeconds;
                        double bps = secs > 0.2 ? (done - first.Done) * 2352.0 / secs : 0;
                        progress?.Report(new Progress
                        {
                            SectorsDone = done, TotalSectors = total, BytesPerSec = bps, BadSectors = r.BadSectors,
                            Remaining = bps > 0 ? TimeSpan.FromSeconds((total - done) * 2352.0 / bps) : TimeSpan.Zero,
                        });
                    }
                }
            }

            if (partLog && inAudio && toc.LeadOutLba > firstAudio)
                log?.Invoke($"Read CD: music part ({toc.LeadOutLba - firstAudio:N0} sectors) read at {X(toc.LeadOutLba - firstAudio, partClock.Elapsed)} on average");

            // The .cue: one .bin; times are sectors from the start of the .bin (LBA 0).
            static string Msf(long s) => $"{s / 75 / 60:D2}:{s / 75 % 60:D2}:{s % 75:D2}";
            // Several sessions: the .bin holds the sessions one after the other,
            // so a sector's place in it is counted within the parts read.
            long InBin(long at)
            {
                long before = 0;
                foreach (var (from, to) in ranges)
                {
                    if (at < to) return before + Math.Max(0, at - from);
                    before += to - from;
                }
                return before;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"REM Copied by ArchivSector-OD");
            // CD-Text, the .cue way (TITLE / PERFORMER / SONGWRITER, the album's
            // before the first FILE, each track's under its TRACK line).
            static string Q(string s) => "\"" + s.Replace('"', '\'') + "\"";
            var text = toc.CdText;
            void TextLines(int track, string indent)
            {
                if (text is null) return;
                foreach (var field in new[] { "TITLE", "PERFORMER", "SONGWRITER" })
                {
                    var v = text.Get(field, track);
                    if (v.Length > 0) sb.AppendLine($"{indent}{field} {Q(v)}");
                }
                foreach (var field in new[] { "COMPOSER", "ARRANGER", "MESSAGE" })
                {
                    var v = text.Get(field, track);
                    if (v.Length > 0) sb.AppendLine($"{indent}REM {field} {Q(v)}");
                }
            }
            if (text is not null && text.Codes.TryGetValue(0, out var upc) && upc.All(char.IsDigit) && upc.Length is 12 or 13)
                sb.AppendLine($"CATALOG {upc.PadLeft(13, '0')}");
            TextLines(0, "");
            if (toc.Sessions > 1) sb.AppendLine("REM SESSION 01");
            sb.AppendLine($"FILE \"{Path.GetFileName(bin)}\" BINARY");
            for (int i = 0; i < toc.Tracks.Count; i++)
            {
                var t = toc.Tracks[i];
                if (toc.Sessions > 1 && i > 0 && t.Session != toc.Tracks[i - 1].Session)
                {
                    // Between sessions: the previous lead-out (6750 sectors after the
                    // first session, 2250 after later ones), the new lead-in, and
                    // the gap before the first track; none of it is in the .bin.
                    int prev = toc.Tracks[i - 1].Session;
                    long between = t.StartLba - toc.SessionLeadOuts[prev - 1];
                    long leadOut = prev == 1 ? 6750 : 2250, leadIn = 4500;
                    long pregap = between - leadOut - leadIn;
                    if (pregap < 0) { leadIn = Math.Max(0, between - leadOut - 150); pregap = between - leadOut - leadIn; }
                    sb.AppendLine($"REM LEAD-OUT {Msf(leadOut)}");
                    sb.AppendLine($"REM SESSION {t.Session:D2}");
                    sb.AppendLine($"REM LEAD-IN {Msf(leadIn)}");
                    if (pregap > 0) sb.AppendLine($"REM PREGAP {Msf(pregap)}");
                }
                sb.AppendLine($"  TRACK {t.Number:D2} {(t.IsData ? $"MODE{t.Mode}/2352" : "AUDIO")}");
                TextLines(t.Number, "    ");
                if (text is not null && text.Codes.TryGetValue(t.Number, out var isrc) && isrc.Length == 12) sb.AppendLine($"    ISRC {isrc}");
                if (t.Number > 1 && t.PregapLba < t.StartLba) sb.AppendLine($"    INDEX 00 {Msf(InBin(t.PregapLba))}");
                sb.AppendLine($"    INDEX 01 {Msf(InBin(t.StartLba))}");
            }
            File.WriteAllText(cue, sb.ToString());
            r.Ok = true;
            r.BinPath = bin;
            r.CuePath = cue;
        }
    }
}
