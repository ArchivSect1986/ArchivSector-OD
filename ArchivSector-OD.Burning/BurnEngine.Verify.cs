using System.Diagnostics;

namespace ArchivSector_OD.Burning
{
    // Verify mode (v4.0 week 5): compares the disc in a drive with an ISO,
    // sector by sector, like ImgBurn's "Verify" mode. For discs burned
    // earlier, by any program, or re-checked after time on the shelf.
    // Reads straight from the drive (ScsiDrive), so nothing Windows has
    // cached is compared. Read-only: nothing is written.
    public static partial class BurnEngine
    {
        public sealed class VerifyProgress
        {
            public double Percent;
            public double BytesPerSec;
            public double OneX;
            public TimeSpan Remaining;
            public long Different;          // sectors that read but don't match
            public long Unreadable;         // sectors the drive couldn't read
        }

        public sealed class VerifyResult
        {
            public bool Ok;                 // every sector matched
            public bool Cancelled;
            public string Message = "";
            public long Sectors;
            public long Different;
            public long Unreadable;
            public List<(long Lba, long Count)> BadRanges = new();
            public TimeSpan Time;
            public double AverageBytesPerSec;
        }

        public static Task<VerifyResult> VerifyAgainstIsoAsync(string driveLetter, string isoPath, IProgress<VerifyProgress>? progress,
            CancellationToken ct, Action<string>? log = null)
        {
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new VerifyResult { Message = "This drive is busy burning or checking." });
            return Task.Factory.StartNew(() =>
            {
                var r = new VerifyResult();
                try { VerifyAgainstIso(letter, isoPath, progress, ct, r, log); }
                catch (Exception ex) { r.Message = "The check stopped: " + ex.Message; log?.Invoke("Verify: " + r.Message); }
                finally { Busy.TryRemove(letter, out _); }
                return r;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void VerifyAgainstIso(string letter, string isoPath, IProgress<VerifyProgress>? progress, CancellationToken ct, VerifyResult r, Action<string>? log)
        {
            var iso = new FileInfo(isoPath);
            if (!iso.Exists) { r.Message = "The ISO file isn't there any more."; return; }
            if (iso.Length == 0 || iso.Length % Sector != 0) { r.Message = "This file isn't a disc image made of whole 2 KB sectors."; return; }
            long isoSectors = iso.Length / Sector;

            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }
            if (!WaitReady(drive, ct, TimeSpan.FromSeconds(30))) { r.Message = "There's no readable disc in the drive."; return; }
            FullDriveSpeed(drive);   // read at full speed, even if a slower one was set when burning

            // How much is on the disc?
            var cap = new byte[8];
            var rep = drive.Read(new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, cap, 10);
            long discSectors = rep.Ok ? BE32(cap, 0) + 1 : 0;
            if (rep.Ok && discSectors < isoSectors)
            {
                r.Message = $"The disc holds {discSectors:N0} sectors but the ISO has {isoSectors:N0}, so they can't be the same. (Is it the right disc and ISO?)";
                return;
            }
            double oneX = iso.Length <= 1_000_000_000L ? 153_600 : iso.Length <= 9_000_000_000L ? 1_385_000 : 4_500_000;
            log?.Invoke($"Verify: {letter}: against {isoPath} ({isoSectors:N0} sectors; the disc holds {discSectors:N0})");

            var disc = new byte[DirectChunkSectors * Sector];
            var file = new byte[DirectChunkSectors * Sector];
            long lba = 0;
            long runStart = -1, runCount = 0;
            void Bad(long s, long n)
            {
                if (runStart >= 0 && runStart + runCount == s) runCount += n;
                else
                {
                    if (runStart >= 0) r.BadRanges.Add((runStart, runCount));
                    runStart = s; runCount = n;
                }
            }

            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var window = new Queue<(TimeSpan T, long Lba)>();
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            while (lba < isoSectors)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                int n = (int)Math.Min(DirectChunkSectors, isoSectors - lba);
                int bytes = n * Sector;
                ReadFully(fs, file, bytes);
                var target = bytes == disc.Length ? disc : new byte[bytes];
                var cdb = new byte[] { 0x28, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };
                rep = RetryWhileBusy(() => drive.Read(cdb, target, 60), 100, 50);
                if (rep.Ok)
                {
                    for (int s = 0; s < n; s++)
                        if (!target.AsSpan(s * Sector, Sector).SequenceEqual(file.AsSpan(s * Sector, Sector))) { r.Different++; Bad(lba + s, 1); }
                }
                else
                {
                    // Sector by sector, to find exactly which ones won't read.
                    var one = new byte[Sector];
                    for (int s = 0; s < n; s++)
                    {
                        if (ct.IsCancellationRequested) break;
                        long at = lba + s;
                        var c1 = new byte[] { 0x28, 0, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, 0, 1, 0 };
                        var r1 = RetryWhileBusy(() => drive.Read(c1, one, 30), 20, 50);
                        if (!r1.Ok) { r.Unreadable++; Bad(at, 1); }
                        else if (!one.AsSpan().SequenceEqual(file.AsSpan(s * Sector, Sector))) { r.Different++; Bad(at, 1); }
                    }
                }
                lba += n;

                var now = clock.Elapsed;
                if (now - lastReport > TimeSpan.FromMilliseconds(250) || lba >= isoSectors)
                {
                    lastReport = now;
                    window.Enqueue((now, lba));
                    while (window.Count > 1 && now - window.Peek().T > TimeSpan.FromSeconds(5)) window.Dequeue();
                    var first = window.Peek();
                    double secs = (now - first.T).TotalSeconds;
                    double bps = secs > 0.2 ? (lba - first.Lba) * (double)Sector / secs : 0;
                    progress?.Report(new VerifyProgress
                    {
                        Percent = 100.0 * lba / isoSectors,
                        BytesPerSec = bps,
                        OneX = oneX,
                        Remaining = bps > 0 ? TimeSpan.FromSeconds((isoSectors - lba) * (double)Sector / bps) : TimeSpan.Zero,
                        Different = r.Different,
                        Unreadable = r.Unreadable,
                    });
                }
            }
            if (runStart >= 0) r.BadRanges.Add((runStart, runCount));
            r.Sectors = Math.Min(lba, isoSectors);
            r.Time = clock.Elapsed;
            r.AverageBytesPerSec = r.Time.TotalSeconds > 0 ? r.Sectors * (double)Sector / r.Time.TotalSeconds : 0;
            long bad = r.Different + r.Unreadable;
            r.Ok = !r.Cancelled && bad == 0;
            r.Message = r.Cancelled
                ? $"Stopped at {100.0 * r.Sectors / isoSectors:0.0}%." + (bad > 0 ? $" {bad:N0} sector(s) didn't match so far." : " Everything checked so far matched.")
                : bad == 0 ? "The disc matches the ISO: every sector is identical."
                : $"The disc doesn't match the ISO: {r.Different:N0} sector(s) differ and {r.Unreadable:N0} couldn't be read.";
            log?.Invoke($"Verify: {r.Message} ({r.Time:hh\\:mm\\:ss})");
            if (r.BadRanges.Count > 0)
                log?.Invoke("Verify: problem sectors at " + string.Join(", ", r.BadRanges.Take(20).Select(b => b.Count == 1 ? $"{b.Lba:N0}" : $"{b.Lba:N0}-{b.Lba + b.Count - 1:N0}"))
                            + (r.BadRanges.Count > 20 ? $" and {r.BadRanges.Count - 20} more ranges" : ""));
        }
    }
}
