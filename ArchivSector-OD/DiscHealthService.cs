using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ArchivSector_OD
{
    // Disc health check: reads every sector of the disc in the drive, front
    // to back, timing each read. Reports sectors that can't be read at all
    // and areas that read much slower than their neighbours -- usually the
    // first sign of wear, scratches, dirt or a disc starting to rot, often
    // long before files stop opening. Read-only: nothing is ever written.
    //
    // How it reads: the drive is opened as a raw device (\\.\E:) and read in
    // 64 KB chunks (32 sectors). CDs are read by their table of contents:
    // music tracks in raw audio mode (so music CDs can be checked too), and
    // data sectors that a normal read can't open (PS1 video/audio, mixed
    // discs) are re-read in raw mode before being called unreadable. A chunk that fails is re-read one sector at
    // a time to find exactly which sectors are bad. A long damaged stretch
    // is skipped over in growing steps (and counted as unreadable), because
    // a drive can spend seconds retrying every bad sector.
    public static class DiscHealthService
    {
        public const int SectorSize = 2048;
        private const int ChunkSectors = 32;
        public const int Segments = 100;

        public enum Level { Healthy = 0, Slow = 1, Damaged = 2 }

        public sealed class Progress
        {
            public long SectorsDone;
            public long TotalSectors;
            public double CurrentBytesPerSec;
            public long BadSectors;
            public long SlowSectors;
            public TimeSpan Elapsed;
            public double[] SegmentSpeed = new double[Segments];   // bytes/sec, 0 = not read yet
            public long[] SegmentBad = new long[Segments];
            public long[] SegmentSlow = new long[Segments];
            public string Media = "";
            public double OneX;                                    // bytes/sec of "1x" for this media
        }

        public sealed class Result
        {
            public bool Completed;
            public bool Cancelled;
            public string? Error;
            public Level Verdict;
            public string VerdictTitle = "";
            public string VerdictText = "";
            public long TotalSectors;
            public long BadSectors;
            public long SlowSectors;
            public bool SkippedDamagedAreas;
            public double AverageBytesPerSec;
            public TimeSpan Elapsed;
            public List<(long Lba, long Count)> BadRanges = new();
            public Progress Final = new();
            public string? ReportPath;
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
        private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        private const int ERROR_NOT_READY = 21;
        private const int ERROR_NO_MEDIA = 1112;
        private const int ERROR_MEDIA_CHANGED = 1110;
        private const uint IOCTL_CDROM_READ_TOC = 0x00024000;
        private const uint IOCTL_CDROM_RAW_READ = 0x0002403E;
        private const int RawSectorSize = 2352;
        private const int CdChunkSectors = 24;       // 24 x 2352 bytes stays under the usual 64 KB transfer limit
        private enum TrackMode { YellowMode2 = 0, XAForm2 = 1, CDDA = 2 }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            out long outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        // "1x" read speed for the kind of disc, by size.
        private static (string Media, double OneX) MediaFor(long bytes) =>
            bytes <= 1_000_000_000L ? ("CD", 153_600)
            : bytes <= 9_000_000_000L ? ("DVD", 1_385_000)
            : ("Blu-ray", 4_500_000);

        public static string FormatSpeed(double bytesPerSec, double oneX) =>
            bytesPerSec <= 0 ? "—" : $"{bytesPerSec / 1_000_000:0.0} MB/s ({bytesPerSec / Math.Max(1, oneX):0.0}x)";

        // ------------------------------------------------------------------
        // The scan
        // ------------------------------------------------------------------
        public static Task<Result> RunAsync(string driveLetter, IProgress<Progress>? progress, CancellationToken ct) =>
            Task.Factory.StartNew(() => Run(driveLetter, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static Result Run(string driveLetter, IProgress<Progress>? progress, CancellationToken ct)
        {
            var r = new Result();
            var p = r.Final;
            var letter = driveLetter.TrimEnd(':', '\\');
            using var h = CreateFile($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                r.Error = e == 5
                    ? $"Windows didn't allow reading drive {letter}: directly. Try running ArchivSector-OD as administrator (right-click → Run as administrator)."
                    : $"Couldn't open drive {letter}: (error {e}). Is a disc in the drive?";
                return r;
            }

            // Let reads go past the end of the file system to the real end of the disc.
            DeviceIoControl(h, FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            // A CD's table of contents gives its real end (including music
            // tracks after a data track) and which tracks are audio.
            var cdTracks = ReadCdToc(h, out long leadOut);
            bool cd = cdTracks is not null && leadOut > 0 && leadOut * (long)SectorSize < 1_000_000_000L;

            long totalBytes = 0;
            if (cd) totalBytes = leadOut * SectorSize;
            else if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long len, 8, out _, IntPtr.Zero))
                totalBytes = len;
            if (totalBytes <= 0)
            {
                try { totalBytes = new DriveInfo($"{letter}:\\").TotalSize; } catch { }
            }
            if (totalBytes <= 0) { r.Error = "Couldn't find out how big the disc is."; return r; }

            long total = totalBytes / SectorSize;
            (p.Media, p.OneX) = MediaFor(totalBytes);
            p.TotalSectors = r.TotalSectors = total;

            // Unbuffered reads (so every read really comes from the disc, even on
            // "Check again") need a memory buffer aligned to 4 KB.
            IntPtr raw = Marshal.AllocHGlobal(Math.Max(ChunkSectors * SectorSize, CdChunkSectors * RawSectorSize) + 4096);
            IntPtr buf = new IntPtr((raw.ToInt64() + 4095) & ~4095L);
            var sw = Stopwatch.StartNew();
            var chunkClock = new Stopwatch();
            var lastReport = TimeSpan.Zero;
            var recent = new Queue<double>();      // recent chunk times (ms), for "slow" detection
            var segBytes = new long[Segments];
            var segSecs = new double[Segments];
            long readBytesTotal = 0;
            double readSecsTotal = 0;
            long badRunStart = -1, badRunCount = 0;
            int skipChunks = 1;                    // grows while a damaged stretch continues
            bool justRecovered = false;

            int Seg(long sector) => (int)Math.Min(Segments - 1, sector * Segments / Math.Max(1, total));

            // CD-aware reads. Audio sectors: raw audio mode. Data sectors: a
            // normal read, then (one sector at a time) raw modes as a fallback.
            bool IsAudio(long sector) => cd && cdTracks!.Any(t => t.Audio && sector >= t.Start && sector < t.End);
            long TrackEnd(long sector)
            {
                if (!cd) return total;
                foreach (var t in cdTracks!) if (sector >= t.Start && sector < t.End) return Math.Min(t.End, total);
                return total;
            }
            int ReadAny(long first, int count) =>
                IsAudio(first) ? RawRead(h, buf, first, count, TrackMode.CDDA) : ReadSectors(h, buf, first, count);
            int ReadOne(long sector)
            {
                if (IsAudio(sector)) return RawRead(h, buf, sector, 1, TrackMode.CDDA);
                int e = ReadSectors(h, buf, sector, 1);
                if (e == 0 || !cd || IsDiscGone(e)) return e;
                if (RawRead(h, buf, sector, 1, TrackMode.XAForm2) == 0) return 0;
                if (RawRead(h, buf, sector, 1, TrackMode.YellowMode2) == 0) return 0;
                return e;
            }

            void MarkBad(long first, long count)
            {
                if (count <= 0) return;
                p.BadSectors += count;
                for (long s = first; s < first + count; s += ChunkSectors)
                    p.SegmentBad[Seg(s)] += Math.Min(ChunkSectors, first + count - s);
                if (badRunStart >= 0 && badRunStart + badRunCount == first) badRunCount += count;
                else
                {
                    if (badRunStart >= 0) r.BadRanges.Add((badRunStart, badRunCount));
                    badRunStart = first; badRunCount = count;
                }
            }

            long lba = 0;
            try
            {
                while (lba < total)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                    // On a CD a chunk never crosses into the next track.
                    int n = (int)Math.Min(cd ? CdChunkSectors : ChunkSectors, TrackEnd(lba) - lba);
                    if (n <= 0) n = (int)Math.Min(ChunkSectors, total - lba);
                    int bytes = n * SectorSize;

                    chunkClock.Restart();
                    int err = ReadAny(lba, n);
                    chunkClock.Stop();

                    if (err == 0)
                    {
                        double ms = chunkClock.Elapsed.TotalMilliseconds;
                        int seg = Seg(lba);
                        segBytes[seg] += bytes;
                        segSecs[seg] += ms / 1000.0;
                        readBytesTotal += bytes;
                        readSecsTotal += ms / 1000.0;

                        // Slow = much slower than the chunks just before it. The
                        // first few seconds (spin-up) and the chunk right after a
                        // damaged area (the drive re-seeking) aren't judged.
                        if (sw.Elapsed.TotalSeconds > 3 && !justRecovered && recent.Count >= 16)
                        {
                            var sorted = recent.OrderBy(x => x).ToArray();
                            double median = sorted[sorted.Length / 2];
                            if (ms > Math.Max(4 * median, 40))
                            {
                                p.SlowSectors += n;
                                p.SegmentSlow[seg] += n;
                            }
                        }
                        recent.Enqueue(ms);
                        if (recent.Count > 64) recent.Dequeue();
                        if (ms > 0) p.CurrentBytesPerSec = bytes / (ms / 1000.0);
                        justRecovered = false;
                        skipChunks = 1;
                        lba += n;
                    }
                    else if (IsDiscGone(err))
                    {
                        r.Error = "The disc was removed or the drive stopped responding.";
                        break;
                    }
                    else
                    {
                        // Find the bad sectors one by one. Four bad sectors in a
                        // row: treat the rest of this chunk as bad, then skip
                        // ahead (1, 2, 4 … up to 256 chunks) while it stays bad.
                        int good = 0, consecutiveBad = 0;
                        bool chunkAllBad = true;
                        for (int i = 0; i < n; i++)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (consecutiveBad >= 4)
                            {
                                MarkBad(lba + i, n - i);
                                break;
                            }
                            int e1 = ReadOne(lba + i);
                            if (e1 == 0) { good++; consecutiveBad = 0; chunkAllBad = false; }
                            else if (IsDiscGone(e1)) { r.Error = "The disc was removed or the drive stopped responding."; break; }
                            else { MarkBad(lba + i, 1); consecutiveBad++; }
                        }
                        if (r.Error is not null || ct.IsCancellationRequested) { r.Cancelled = ct.IsCancellationRequested; break; }
                        lba += n;
                        justRecovered = true;

                        if (chunkAllBad && lba < total)
                        {
                            long skip = Math.Min((long)skipChunks * ChunkSectors, total - lba);
                            if (skipChunks > 1)
                            {
                                MarkBad(lba, skip);
                                r.SkippedDamagedAreas = true;
                                lba += skip;
                            }
                            skipChunks = Math.Min(skipChunks * 2, 256);
                        }
                    }

                    p.SectorsDone = Math.Min(lba, total);
                    if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(250) || lba >= total)
                    {
                        lastReport = sw.Elapsed;
                        p.Elapsed = sw.Elapsed;
                        for (int s = 0; s < Segments; s++)
                            p.SegmentSpeed[s] = segSecs[s] > 0 ? segBytes[s] / segSecs[s] : 0;
                        progress?.Report(Snapshot(p));
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = $"The scan stopped: {ex.Message}";
            }

            finally
            {
                Marshal.FreeHGlobal(raw);
            }

            if (badRunStart >= 0) r.BadRanges.Add((badRunStart, badRunCount));
            p.Elapsed = r.Elapsed = sw.Elapsed;
            for (int s = 0; s < Segments; s++)
                p.SegmentSpeed[s] = segSecs[s] > 0 ? segBytes[s] / segSecs[s] : 0;
            r.BadSectors = p.BadSectors;
            r.SlowSectors = p.SlowSectors;
            r.AverageBytesPerSec = readSecsTotal > 0 ? readBytesTotal / readSecsTotal : 0;
            r.Completed = !r.Cancelled && r.Error is null && lba >= total;
            Judge(r);
            return r;
        }

        // Returns 0 on success, otherwise the Win32 error code.
        private static int ReadSectors(SafeFileHandle h, IntPtr buf, long lba, int count)
        {
            uint want = (uint)(count * SectorSize);
            if (!SetFilePointerEx(h, lba * SectorSize, out _, 0)) return Marshal.GetLastWin32Error();
            if (!ReadFile(h, buf, want, out uint got, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                return e == 0 ? 23 : e;
            }
            return got == want ? 0 : 23; // short read (past the end): treat as a read error
        }

        // Raw CD read (IOCTL_CDROM_RAW_READ): audio, or data sectors a normal read can't open.
        private static int RawRead(SafeFileHandle h, IntPtr buf, long lba, int count, TrackMode mode)
        {
            var info = new byte[16];
            BitConverter.GetBytes(lba * SectorSize).CopyTo(info, 0);   // offset is given in 2048-byte units
            BitConverter.GetBytes((uint)count).CopyTo(info, 8);
            BitConverter.GetBytes((int)mode).CopyTo(info, 12);
            uint size = (uint)(count * RawSectorSize);
            if (DeviceIoControl(h, IOCTL_CDROM_RAW_READ, info, (uint)info.Length, buf, size, out uint got, IntPtr.Zero))
                return got > 0 ? 0 : 23;
            int e = Marshal.GetLastWin32Error();
            return e == 0 ? 23 : e;
        }

        // A CD's tracks from its table of contents (null if it isn't a CD).
        private static List<(long Start, long End, bool Audio)>? ReadCdToc(SafeFileHandle h, out long leadOut)
        {
            leadOut = 0;
            var toc = new byte[4 + 100 * 8];
            if (!DeviceIoControl(h, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, toc, (uint)toc.Length, out uint got, IntPtr.Zero) || got < 12)
                return null;
            int first = toc[2], last = toc[3];
            if (first < 1 || last < first || last > 99) return null;
            static long Lba(byte[] t, int i) => ((long)t[4 + i * 8 + 5] * 60 + t[4 + i * 8 + 6]) * 75 + t[4 + i * 8 + 7] - 150;
            int count = last - first + 1;
            var list = new List<(long, long, bool)>();
            for (int i = 0; i < count; i++)
            {
                long start = Math.Max(0, Lba(toc, i));
                long end = Lba(toc, i + 1);                       // next track, or the lead-out after the last one
                bool audio = (toc[4 + i * 8 + 1] & 0x04) == 0;    // control bit 2 = data track
                if (end > start) list.Add((start, end, audio));
            }
            leadOut = Lba(toc, count);
            return list.Count > 0 ? list : null;
        }

        private static bool IsDiscGone(int err) => err is ERROR_NOT_READY or ERROR_NO_MEDIA or ERROR_MEDIA_CHANGED;

        private static Progress Snapshot(Progress p) => new()
        {
            SectorsDone = p.SectorsDone, TotalSectors = p.TotalSectors, CurrentBytesPerSec = p.CurrentBytesPerSec,
            BadSectors = p.BadSectors, SlowSectors = p.SlowSectors, Elapsed = p.Elapsed,
            SegmentSpeed = (double[])p.SegmentSpeed.Clone(), SegmentBad = (long[])p.SegmentBad.Clone(),
            SegmentSlow = (long[])p.SegmentSlow.Clone(), Media = p.Media, OneX = p.OneX,
        };

        private static void Judge(Result r)
        {
            double slowPct = r.TotalSectors > 0 ? 100.0 * r.SlowSectors / r.TotalSectors : 0;
            if (r.BadSectors > 0)
            {
                r.Verdict = Level.Damaged;
                r.VerdictTitle = "Damaged";
                r.VerdictText = $"{r.BadSectors:N0} sector(s) ({FormatBytes(r.BadSectors * SectorSize)}) couldn't be read. " +
                                "Clean the disc and run the check again. If it's still damaged, make a backup of what's readable as soon as possible.";
            }
            else if (slowPct >= 0.5)
            {
                r.Verdict = Level.Slow;
                r.VerdictTitle = "Readable, with slow areas";
                r.VerdictText = $"Every sector read, but {slowPct:0.#}% of the disc read slowly. That's often the first sign of wear, dirt or scratches. " +
                                "Clean the disc, and make sure you have a backup.";
            }
            else
            {
                r.Verdict = Level.Healthy;
                r.VerdictTitle = "Healthy";
                r.VerdictText = "Every sector read cleanly at a steady speed.";
            }
            if (!r.Completed)
                r.VerdictText = (r.Cancelled ? "Stopped early, so only part of the disc was checked. " : "The scan didn't finish. ") + r.VerdictText;
        }

        public static string FormatBytes(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : $"{b / 1024.0:0} KB";

        // ------------------------------------------------------------------
        // Report + History
        // ------------------------------------------------------------------
        public static string ReportsFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchivSector-OD", "HealthReports");

        public static void SaveReport(Result r, string driveLetter, string discTitle, string driveModel)
        {
            try
            {
                Directory.CreateDirectory(ReportsFolder);
                var safe = string.Join("_", (string.IsNullOrWhiteSpace(discTitle) ? "Disc" : discTitle).Split(Path.GetInvalidFileNameChars())).Trim();
                if (safe.Length > 60) safe = safe.Substring(0, 60);
                var path = Path.Combine(ReportsFolder, $"{DateTime.Now:yyyy-MM-dd_HHmm}_{safe}.txt");
                var p = r.Final;
                var sb = new StringBuilder();
                sb.AppendLine($"ARCHIVSECTOR-OD v{UpdateCheckService.CurrentVersionDisplay} - DISC HEALTH CHECK");
                sb.AppendLine($"Date:        {DateTime.Now:yyyy-MM-dd HH:mm}");
                sb.AppendLine($"Disc:        {discTitle}");
                sb.AppendLine($"Drive:       {driveLetter}: {driveModel}".TrimEnd());
                sb.AppendLine($"Media:       {p.Media}, {FormatBytes(r.TotalSectors * SectorSize)} ({r.TotalSectors:N0} sectors)");
                sb.AppendLine($"Checked:     {p.SectorsDone:N0} of {r.TotalSectors:N0} sectors{(r.Completed ? "" : " (not finished)")}");
                sb.AppendLine($"Time:        {r.Elapsed:hh\\:mm\\:ss}");
                sb.AppendLine($"Avg speed:   {FormatSpeed(r.AverageBytesPerSec, p.OneX)}");
                sb.AppendLine();
                sb.AppendLine($"RESULT:      {r.VerdictTitle.ToUpperInvariant()}");
                sb.AppendLine($"             {r.VerdictText}");
                sb.AppendLine($"Unreadable:  {r.BadSectors:N0} sector(s){(r.SkippedDamagedAreas ? " (includes long damaged stretches that were skipped over)" : "")}");
                sb.AppendLine($"Slow:        {r.SlowSectors:N0} sector(s)");
                if (r.Error is not null) sb.AppendLine($"Note:        {r.Error}");
                sb.AppendLine();
                if (r.BadRanges.Count > 0)
                {
                    sb.AppendLine("UNREADABLE AREAS (sector number, how many, position on disc)");
                    foreach (var (lba, count) in r.BadRanges.Take(500))
                        sb.AppendLine($"  {lba,12:N0}  {count,8:N0}  at {100.0 * lba / Math.Max(1, r.TotalSectors):0.0}% from the start (inner edge)");
                    if (r.BadRanges.Count > 500) sb.AppendLine($"  … and {r.BadRanges.Count - 500:N0} more");
                    sb.AppendLine();
                }
                sb.AppendLine("READ SPEED ACROSS THE DISC (start = inner edge, end = outer edge)");
                for (int s = 0; s < Segments; s += 5)
                {
                    double avg = 0; int c = 0; long bad = 0, slow = 0;
                    for (int k = s; k < s + 5 && k < Segments; k++)
                    {
                        if (p.SegmentSpeed[k] > 0) { avg += p.SegmentSpeed[k]; c++; }
                        bad += p.SegmentBad[k]; slow += p.SegmentSlow[k];
                    }
                    sb.AppendLine($"  {s,3}-{s + 4,3}%  {FormatSpeed(c > 0 ? avg / c : 0, p.OneX),-22} unreadable {bad,7:N0}  slow {slow,7:N0}");
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                r.ReportPath = path;

                HistoryService.Append(new HistoryEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    DriveLetter = driveLetter,
                    DriveModel = driveModel,
                    Title = string.IsNullOrWhiteSpace(discTitle) ? "Disc" : discTitle,
                    System = "Disc health check",
                    OutputPath = path,
                    Capacity = $"{p.Media} {FormatBytes(r.TotalSectors * SectorSize)}",
                    RedumpMatch = $"{r.VerdictTitle}{(r.Completed ? "" : " (partial)")}",
                });
            }
            catch { /* the result is still shown on screen */ }
        }
    }
}
