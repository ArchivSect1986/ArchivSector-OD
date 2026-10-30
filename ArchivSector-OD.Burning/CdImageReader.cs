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
    // Discs with more than one session (CD-Extra / Enhanced CD) aren't
    // handled: ImgBurn or Redumper can copy those.
    public static class CdImageReader
    {
        public sealed class TocTrack
        {
            public int Number;
            public bool IsData;
            public int Mode;            // data tracks: 1 or 2
            public long StartLba;       // INDEX 01
            public long PregapLba;      // INDEX 00 (= StartLba when there's no gap)
        }

        public sealed class Toc
        {
            public List<TocTrack> Tracks = new();
            public long LeadOutLba;
            public int Sessions = 1;
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

            // Data tracks: mode 1 or mode 2, from the sector header.
            var raw = new byte[2352];
            foreach (var t in toc.Tracks.Where(t => t.IsData))
            {
                long l = t.StartLba;
                rep = drive.Read(new byte[] { 0xBE, 0x00, (byte)(l >> 24), (byte)(l >> 16), (byte)(l >> 8), (byte)l, 0, 0, 1, 0xF8, 0, 0 }, raw, 30);
                t.Mode = rep.Ok && raw[15] == 2 ? 2 : 1;
            }

            // Gaps before tracks 2 and up.
            for (int i = 1; i < toc.Tracks.Count; i++)
            {
                var t = toc.Tracks[i];
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
            if (toc.Sessions > 1) { r.Message = "This CD has more than one session (CD-Extra / Enhanced CD), which Copy disc can't copy yet. ImgBurn or Redumper can."; return; }
            log?.Invoke($"Read CD: {letter}: {toc.Summary}; " + string.Join(", ", toc.Tracks.Select(t =>
                $"T{t.Number} {(t.IsData ? "MODE" + t.Mode : "AUDIO")} @{t.StartLba}" + (t.PregapLba < t.StartLba ? $" (gap {t.StartLba - t.PregapLba})" : ""))));

            // Full speed: the drive's own defaults back first (a slower write
            // speed set earlier by a burn, ours or another program's, can
            // stay in force), then "as fast as you can".
            BurnEngine.FullDriveSpeed(drive);
            Directory.CreateDirectory(folder);
            string bin = Path.Combine(folder, name + ".bin"), cue = Path.Combine(folder, name + ".cue");
            long total = toc.LeadOutLba;
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
            long firstAudio = toc.Tracks.Where(t => !t.IsData).Select(t => (long)t.PregapLba).DefaultIfEmpty(total).Min();
            var partClock = Stopwatch.StartNew();
            bool inAudio = firstAudio == 0;
            static string X(long sectors, TimeSpan t) => t.TotalSeconds > 0.5 ? $"{sectors / 75.0 / t.TotalSeconds:0.0}x" : "?";
            using (var fs = new FileStream(bin, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                for (long lba = 0; lba < total;)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; r.Message = "Stopped."; return; }
                    int n = (int)Math.Min(N, total - lba);
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
                    if ((int)(lba * 10 / Math.Max(1, total)) > lastTenth && lba < total)
                    {
                        lastTenth = (int)(lba * 10 / total);
                        log?.Invoke($"Read CD: {lastTenth * 10}% after {clock.Elapsed:mm\\:ss}, last 10% at {X(lba - tenthFrom, tenthClock.Elapsed)} ({(lba > firstAudio ? "music" : "data")})");
                        tenthFrom = lba;
                        tenthClock.Restart();
                    }
                    if (!inAudio && lba >= firstAudio && firstAudio > 0)
                    {
                        inAudio = true;
                        log?.Invoke($"Read CD: data part ({firstAudio:N0} sectors) read at {X(firstAudio, partClock.Elapsed)} on average");
                        partClock.Restart();
                    }
                    var now = clock.Elapsed;
                    if (now - lastReport > TimeSpan.FromMilliseconds(250) || lba >= total)
                    {
                        lastReport = now;
                        window.Enqueue((now, lba));
                        while (window.Count > 1 && now - window.Peek().T > TimeSpan.FromSeconds(5)) window.Dequeue();
                        var first = window.Peek();
                        double secs = (now - first.T).TotalSeconds;
                        double bps = secs > 0.2 ? (lba - first.Done) * 2352.0 / secs : 0;
                        progress?.Report(new Progress
                        {
                            SectorsDone = lba, TotalSectors = total, BytesPerSec = bps, BadSectors = r.BadSectors,
                            Remaining = bps > 0 ? TimeSpan.FromSeconds((total - lba) * 2352.0 / bps) : TimeSpan.Zero,
                        });
                    }
                }
            }

            if (inAudio && total > firstAudio)
                log?.Invoke($"Read CD: music part ({total - firstAudio:N0} sectors) read at {X(total - firstAudio, partClock.Elapsed)} on average");

            // The .cue: one .bin; times are sectors from the start of the .bin (LBA 0).
            static string Msf(long s) => $"{s / 75 / 60:D2}:{s / 75 % 60:D2}:{s % 75:D2}";
            var sb = new StringBuilder();
            sb.AppendLine($"REM Copied by ArchivSector-OD");
            sb.AppendLine($"FILE \"{Path.GetFileName(bin)}\" BINARY");
            foreach (var t in toc.Tracks)
            {
                sb.AppendLine($"  TRACK {t.Number:D2} {(t.IsData ? $"MODE{t.Mode}/2352" : "AUDIO")}");
                if (t.Number > 1 && t.PregapLba < t.StartLba) sb.AppendLine($"    INDEX 00 {Msf(t.PregapLba)}");
                sb.AppendLine($"    INDEX 01 {Msf(t.StartLba)}");
            }
            File.WriteAllText(cue, sb.ToString());
            r.Ok = true;
            r.BinPath = bin;
            r.CuePath = cue;
        }
    }
}
