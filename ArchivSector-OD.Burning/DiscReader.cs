using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArchivSector_OD.Burning
{
    // Read mode (v4.0 week 3): copies a data disc to an ISO, sector by
    // sector, like ImgBurn's "Create image file from disc". For data discs
    // (backups, software, photo and game discs): it reads exactly what the
    // drive returns and does nothing to get past copy protection, so movie
    // discs belong in the drive bays' rips instead (DescribeDisc says so).
    //
    // Bad sectors: each one is retried; after that the read either stops
    // (the default, so a damaged copy is never mistaken for a good one) or,
    // if asked, fills the sector with zeros, notes it and carries on.
    public static class DiscReader
    {
        public const int SectorSize = 2048;
        private const int ChunkSectors = 32;            // 64 KB per read

        public sealed class DiscInfo
        {
            public string DriveLetter = "";
            public bool Ready;
            public string Label = "";
            public string FileSystem = "";
            public long Bytes;
            public string Kind = "";                    // "CD", "DVD", "Blu-ray"
            public bool IsMovieDisc;                    // VIDEO_TS / BDMV: use the drive bays instead
            public bool IsAudioCd;                      // no data track: an ISO can't hold music
            public string? Problem;                     // why it can't be read here, if it can't
        }

        public sealed class Options
        {
            public string DriveLetter = "";
            public string OutputPath = "";
            public int Retries = 3;                     // extra tries per bad sector
            public bool ContinueOnErrors;               // zeros for unreadable sectors instead of stopping
        }

        public sealed class Progress
        {
            public long SectorsDone;
            public long TotalSectors;
            public double BytesPerSec;
            public double OneX;
            public TimeSpan Remaining;
            public long BadSectors;
            public double Percent => TotalSectors > 0 ? Math.Min(100, 100.0 * SectorsDone / TotalSectors) : 0;
        }

        public sealed class Result
        {
            public bool Ok;
            public bool Cancelled;
            public string Message = "";
            public string? IsoPath;
            public long Bytes;
            public long BadSectors;
            public List<(long Lba, long Count)> BadRanges = new();
            public TimeSpan Time;
            public double AverageBytesPerSec;
        }

        // What's in the drive (quick: the file system, not the sectors).
        public static DiscInfo DescribeDisc(string driveLetter)
        {
            var letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
            var d = new DiscInfo { DriveLetter = letter };
            try
            {
                var di = new DriveInfo(letter + ":\\");
                d.Ready = di.IsReady;
                if (!d.Ready)
                {
                    d.Problem = "No readable disc in the drive. (A blank disc, or a music CD, can't be read to an ISO.)";
                    d.IsAudioCd = LooksLikeAudioCd(letter);
                    if (d.IsAudioCd) d.Problem = "This is a music CD. An ISO can only hold data, not CD audio tracks.";
                    return d;
                }
                d.Label = SafeLabel(di);
                d.FileSystem = di.DriveFormat;
                d.Bytes = di.TotalSize;
                d.Kind = d.Bytes <= 1_000_000_000L ? "CD" : d.Bytes <= 9_000_000_000L ? "DVD" : "Blu-ray";
                var root = di.RootDirectory.FullName;
                d.IsMovieDisc = Directory.Exists(Path.Combine(root, "VIDEO_TS")) || Directory.Exists(Path.Combine(root, "BDMV"));
                if (d.IsMovieDisc)
                    d.Problem = "This is a movie disc. Use its drive bay (Raw ISO or Full Backup) to back it up; Read mode is for data discs.";
            }
            catch (Exception ex) { d.Problem = "The disc couldn't be read: " + ex.Message; }
            return d;
        }

        public static Task<Result> ReadAsync(Options o, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null) =>
            Task.Factory.StartNew(() => Read(o, progress, ct, log), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        private static Result Read(Options o, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log)
        {
            var r = new Result();
            var sw = Stopwatch.StartNew();
            var letter = o.DriveLetter.TrimEnd(':', '\\').ToUpperInvariant();
            var outFull = Path.GetFullPath(o.OutputPath);
            if (outFull.StartsWith(letter + ":", StringComparison.OrdinalIgnoreCase))
            {
                r.Message = "The ISO can't be saved onto the disc being read.";
                return r;
            }

            using var h = CreateFile($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                r.Message = e == 5
                    ? $"Windows didn't allow reading drive {letter}: directly. Try running ArchivSector-OD as administrator."
                    : $"Couldn't open drive {letter}: (error {e}). Is a disc in the drive?";
                return r;
            }
            DeviceIoControl(h, FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

            long totalBytes = 0;
            if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long len, 8, out _, IntPtr.Zero)) totalBytes = len;
            if (totalBytes <= 0) { try { totalBytes = new DriveInfo(letter + ":\\").TotalSize; } catch { } }
            if (totalBytes <= 0) { r.Message = "Couldn't find out how big the disc is."; return r; }
            long total = totalBytes / SectorSize;
            double oneX = totalBytes <= 1_000_000_000L ? 153_600 : totalBytes <= 9_000_000_000L ? 1_385_000 : 4_500_000;

            // Room for the ISO?
            try
            {
                var root = Path.GetPathRoot(outFull);
                if (!string.IsNullOrEmpty(root))
                {
                    var di = new DriveInfo(root);
                    if (di.IsReady && di.AvailableFreeSpace < totalBytes + 64L * 1024 * 1024)
                    {
                        r.Message = $"There isn't enough free space on {root} for the ISO ({Fmt(totalBytes)} needed, {Fmt(di.AvailableFreeSpace)} free).";
                        return r;
                    }
                }
            }
            catch { }

            log?.Invoke($"Read: {letter}: -> {outFull} ({totalBytes:N0} bytes, {total:N0} sectors, retries {o.Retries}, {(o.ContinueOnErrors ? "continue past bad sectors" : "stop at a bad sector")})");
            Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
            var temp = outFull + ".partial";

            // Unbuffered reads need a 4 KB aligned buffer.
            IntPtr raw = Marshal.AllocHGlobal(ChunkSectors * SectorSize + 4096);
            IntPtr buf = new IntPtr((raw.ToInt64() + 4095) & ~4095L);
            var managed = new byte[ChunkSectors * SectorSize];
            var zeros = new byte[SectorSize];
            long badRunStart = -1, badRunCount = 0;
            long lba = 0;
            bool keep = false;
            try
            {
                using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var lastReport = TimeSpan.Zero;
                var speedWindow = new Queue<(TimeSpan T, long Sectors)>();

                while (lba < total)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(ChunkSectors, total - lba);
                    int err = ReadSectors(h, buf, lba, n);
                    if (err == 0)
                    {
                        Marshal.Copy(buf, managed, 0, n * SectorSize);
                        file.Write(managed, 0, n * SectorSize);
                        lba += n;
                    }
                    else if (IsDiscGone(err))
                    {
                        r.Message = "The disc was removed or the drive stopped responding.";
                        return r;
                    }
                    else
                    {
                        // Sector by sector, with retries.
                        for (int i = 0; i < n; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            long s = lba + i;
                            int e1 = ReadSectors(h, buf, s, 1);
                            for (int t = 0; e1 != 0 && !IsDiscGone(e1) && t < o.Retries; t++)
                            {
                                ct.ThrowIfCancellationRequested();
                                e1 = ReadSectors(h, buf, s, 1);
                            }
                            if (e1 == 0)
                            {
                                Marshal.Copy(buf, managed, 0, SectorSize);
                                file.Write(managed, 0, SectorSize);
                                continue;
                            }
                            if (IsDiscGone(e1))
                            {
                                r.Message = "The disc was removed or the drive stopped responding.";
                                return r;
                            }
                            r.BadSectors++;
                            if (badRunStart >= 0 && badRunStart + badRunCount == s) badRunCount++;
                            else
                            {
                                if (badRunStart >= 0) r.BadRanges.Add((badRunStart, badRunCount));
                                badRunStart = s; badRunCount = 1;
                            }
                            log?.Invoke($"Read: sector {s:N0} couldn't be read (error {e1}) after {o.Retries + 1} tries");
                            if (!o.ContinueOnErrors)
                            {
                                r.BadRanges.Add((badRunStart, badRunCount));
                                r.Message = $"Stopped: sector {s:N0} couldn't be read after {o.Retries + 1} tries ({100.0 * s / total:0.0}% through the disc). " +
                                            "The disc may be scratched or dirty. Clean it and try again, or tick \"Keep going past unreadable sectors\".";
                                return r;
                            }
                            file.Write(zeros, 0, SectorSize);
                        }
                        lba += n;
                    }

                    var now = sw.Elapsed;
                    if (now - lastReport > TimeSpan.FromMilliseconds(250) || lba >= total)
                    {
                        lastReport = now;
                        speedWindow.Enqueue((now, lba));
                        while (speedWindow.Count > 1 && now - speedWindow.Peek().T > TimeSpan.FromSeconds(5)) speedWindow.Dequeue();
                        var first = speedWindow.Peek();
                        double secs = (now - first.T).TotalSeconds;
                        double bps = secs > 0.2 ? (lba - first.Sectors) * (double)SectorSize / secs : 0;
                        progress?.Report(new Progress
                        {
                            SectorsDone = lba,
                            TotalSectors = total,
                            BytesPerSec = bps,
                            OneX = oneX,
                            BadSectors = r.BadSectors,
                            Remaining = bps > 0 ? TimeSpan.FromSeconds((total - lba) * (double)SectorSize / bps) : TimeSpan.Zero,
                        });
                    }
                }
                file.Flush();
                keep = true;
            }
            catch (OperationCanceledException)
            {
                r.Cancelled = true;
                r.Message = "Cancelled. No ISO was saved.";
                log?.Invoke("Read cancelled.");
                return r;
            }
            catch (Exception ex)
            {
                r.Message = "The read stopped: " + ex.Message;
                log?.Invoke("Read failed: " + ex.Message);
                return r;
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
                if (!keep) { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }

            if (badRunStart >= 0) r.BadRanges.Add((badRunStart, badRunCount));
            File.Move(temp, outFull, overwrite: true);
            sw.Stop();
            r.Ok = true;
            r.IsoPath = outFull;
            r.Bytes = new FileInfo(outFull).Length;
            r.Time = sw.Elapsed;
            r.AverageBytesPerSec = r.Time.TotalSeconds > 0 ? r.Bytes / r.Time.TotalSeconds : 0;
            r.Message = r.BadSectors == 0
                ? $"Disc read: {Path.GetFileName(outFull)}, {Fmt(r.Bytes)}. Every sector read cleanly."
                : $"Disc read with problems: {r.BadSectors:N0} sector(s) couldn't be read and were filled with zeros. Files in those areas will be damaged in the ISO.";
            log?.Invoke($"Read finished: {outFull} ({r.Bytes:N0} bytes, {r.BadSectors:N0} unreadable sectors) in {r.Time:hh\\:mm\\:ss}");
            if (r.BadRanges.Count > 0)
                log?.Invoke("Read: unreadable sectors at " + string.Join(", ", r.BadRanges.Take(20).Select(b => b.Count == 1 ? $"{b.Lba:N0}" : $"{b.Lba:N0}-{b.Lba + b.Count - 1:N0}"))
                            + (r.BadRanges.Count > 20 ? $" and {r.BadRanges.Count - 20} more ranges" : ""));
            return r;
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        private const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
        private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        private const uint IOCTL_CDROM_READ_TOC = 0x00024000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            out long outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

        // 0 = read OK, otherwise the Windows error code.
        private static int ReadSectors(SafeFileHandle h, IntPtr buf, long lba, int count)
        {
            uint want = (uint)(count * SectorSize);
            if (!SetFilePointerEx(h, lba * SectorSize, out _, 0)) return Marshal.GetLastWin32Error();
            if (!ReadFile(h, buf, want, out uint got, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                return e == 0 ? 23 : e;
            }
            return got == want ? 0 : 23;
        }

        // Not ready / no media / media changed: the disc is gone, not damaged.
        private static bool IsDiscGone(int err) => err is 21 or 1112 or 1110;

        // A music CD has a table of contents but no data track Windows can open.
        private static bool LooksLikeAudioCd(string letter)
        {
            try
            {
                using var h = CreateFile($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return false;
                var toc = new byte[804];
                if (!DeviceIoControl(h, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, toc, (uint)toc.Length, out uint got, IntPtr.Zero) || got < 12) return false;
                int first = toc[2], last = toc[3];
                for (int t = 0; t <= last - first; t++)
                {
                    int control = toc[4 + t * 8 + 1] & 0x0F;
                    if ((control & 0x04) != 0) return false;   // a data track
                }
                return true;
            }
            catch { return false; }
        }

        private static string SafeLabel(DriveInfo di)
        {
            try { return string.IsNullOrWhiteSpace(di.VolumeLabel) ? "DISC" : di.VolumeLabel; }
            catch { return "DISC"; }
        }

        private static string Fmt(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB"
            : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB"
            : $"{b / 1024.0:0} KB";
    }
}
