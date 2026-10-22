using System.Diagnostics;

namespace ArchivSector_OD.Burning
{
    // Direct writing (v4.0 week 4): ArchivSector-OD's own writer. It sends
    // the drive the write commands itself (through ScsiDrive), instead of
    // asking Windows' burning engine or ImgBurn to do it.
    //
    // First version: BD-RE only. A BD-RE is the simplest disc to write:
    // once formatted, data is written straight onto it from the first
    // sector, with no tracks or sessions to open and close, and a failed
    // test costs nothing because the disc can be erased and reused. CD-R,
    // DVD and BD-R follow once this is proven.
    //
    // Steps:
    //   1. check it's a BD-RE and the ISO fits the whole disc
    //   2. take the disc away from Windows (lock + dismount) and stop
    //      the tray being opened mid-write
    //   3. format it to the whole disc (no spare areas) if it isn't already
    //   4. write the ISO from sector 0, 64 KB at a time
    //   5. tell the drive to finish writing everything in its buffer
    //   6. (verify) read every sector back directly and compare with the ISO
    //   7. hand the disc back to Windows
    public static partial class BurnEngine
    {
        private const int DirectChunkSectors = 32;     // 64 KB per write
        private const int ProfileBdRe = 0x43;

        /// <summary>Can our own writer handle the disc in this drive yet? (BD-RE only for now.)</summary>
        public static bool DirectWriteSupports(string mediaType) => mediaType == "BD-RE";

        /// <summary>
        /// Burns an ISO to a BD-RE with ArchivSector-OD's own writer. Same
        /// progress and result as BurnAsync, so the screens treat it alike.
        /// Cancelling is safe on a BD-RE: the disc can be written again.
        /// </summary>
        public static Task<Result> BurnDirectAsync(string driveLetter, string isoPath, bool verify,
            IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null)
        {
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new Result { Message = "This drive is already burning." });
            return Task.Factory.StartNew(() =>
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                var r = new Result();
                try
                {
                    DirectBurn(letter, isoPath, verify, progress, ct, r, log);
                }
                catch (Exception ex)
                {
                    r.Message = (r.Burned ? "Burned, but checking the disc failed: " : "The burn failed: ") + ex.Message;
                    log?.Invoke("Direct write: " + r.Message);
                }
                finally
                {
                    Busy.TryRemove(letter, out _);
                    SetThreadExecutionState(ES_CONTINUOUS);
                    PausePolling(letter, TimeSpan.FromSeconds(5));
                }
                return r;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void DirectBurn(string letter, string isoPath, bool verify, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log)
        {
            var iso = new FileInfo(isoPath);
            if (!iso.Exists) { r.Message = "The ISO file isn't there any more."; return; }
            if (iso.Length == 0 || iso.Length % Sector != 0) { r.Message = "This file isn't a disc image made of whole 2 KB sectors, so it can't be burned as-is."; return; }
            long isoSectors = iso.Length / Sector;

            progress?.Report(new Progress { Phase = "Preparing", Percent = -1 });
            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }

            if (!WaitReady(drive, ct, TimeSpan.FromSeconds(30))) { r.Message = "The drive didn't get ready (is a disc in it?)."; return; }

            // 1. A BD-RE?
            var cfg = new byte[8];
            var rep = drive.Read(new byte[] { 0x46, 0x02, 0, 0, 0, 0, 0, 0, 8, 0 }, cfg, 10);
            int profile = rep.Ok ? (cfg[6] << 8) | cfg[7] : 0;
            if (profile != ProfileBdRe)
            {
                r.Message = "ArchivSector-OD's own writer only writes BD-RE discs so far. Use ImgBurn or Windows for this disc.";
                return;
            }

            // How big can it be formatted, and is it formatted already?
            var fc = new byte[252];
            rep = drive.Read(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0, 252, 0 }, fc, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say how big the BD-RE is: " + rep.Describe(); return; }
            long current = BE32(fc, 4);
            bool formatted = (fc[8] & 3) == 2;
            long wholeDisc = 0;
            for (int off = 12; off + 8 <= 4 + fc[3] && off + 8 <= fc.Length; off += 8)
                if ((fc[off + 4] >> 2) == 0x31) wholeDisc = Math.Max(wholeDisc, BE32(fc, off));
            long capacity = formatted ? current : wholeDisc;
            bool needFormat = !formatted || (wholeDisc > 0 && current < wholeDisc && isoSectors > current);
            if (needFormat) capacity = wholeDisc;
            if (capacity <= 0) { r.Message = "The drive didn't offer a size to format this BD-RE to."; return; }
            if (isoSectors > capacity)
            {
                r.Message = $"The ISO ({Fmt(iso.Length)}) is bigger than this BD-RE ({Fmt(capacity * Sector)}).";
                return;
            }
            log?.Invoke($"Direct write: BD-RE in {letter}:, {(formatted ? $"formatted to {current:N0} sectors" : "not formatted")}, whole disc {wholeDisc:N0} sectors; ISO {isoSectors:N0} sectors ({iso.Length:N0} bytes)");

            // 2. Take the disc from Windows; keep the tray shut while writing.
            drive.LockAndDismount();
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);   // PREVENT MEDIUM REMOVAL
            try
            {
                // 3. Format to the whole disc (no spare areas), like ImgBurn.
                if (needFormat)
                {
                    progress?.Report(new Progress { Phase = "Formatting", Percent = -1 });
                    log?.Invoke($"Direct write: formatting the BD-RE to {wholeDisc:N0} sectors (no spare areas)");
                    var param = new byte[12];
                    param[1] = 0x02;                       // IMMED: answer at once, format in the background
                    param[3] = 8;                          // one 8-byte format descriptor
                    param[4] = (byte)(wholeDisc >> 24); param[5] = (byte)(wholeDisc >> 16);
                    param[6] = (byte)(wholeDisc >> 8); param[7] = (byte)wholeDisc;
                    param[8] = 0x31 << 2;                  // format type 31h: full disc, no spare areas
                    rep = drive.Write(new byte[] { 0x04, 0x11, 0, 0, 0, 0 }, param, 60);
                    if (!rep.Ok) { r.Message = "The drive wouldn't format the BD-RE: " + rep.Describe(); return; }
                    if (!WaitFormatted(drive, progress, ct, TimeSpan.FromMinutes(30)))
                    {
                        r.Message = ct.IsCancellationRequested ? "Cancelled while formatting. The BD-RE can be used again." : "The format didn't finish in time.";
                        r.Cancelled = ct.IsCancellationRequested;
                        return;
                    }
                }

                // 4. Write.
                var clock = Stopwatch.StartNew();
                var buf = new byte[DirectChunkSectors * Sector];
                long lba = 0;
                double speed = 0;
                var speedClock = Stopwatch.StartNew();
                long speedFrom = 0;
                var lastReport = TimeSpan.Zero;
                using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                {
                    while (lba < isoSectors)
                    {
                        if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                        int n = (int)Math.Min(DirectChunkSectors, isoSectors - lba);
                        int bytes = n * Sector;
                        ReadFully(fs, buf, bytes);
                        var data = bytes == buf.Length ? buf : buf.AsSpan(0, bytes).ToArray();
                        var cdb = new byte[] { 0x2A, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };

                        rep = drive.Write(cdb, data, 60);
                        for (int tries = 0; !rep.Ok && rep.IsTryAgain && tries < 200; tries++)
                        {
                            Thread.Sleep(50);
                            rep = drive.Write(cdb, data, 60);
                        }
                        if (!rep.Ok)
                        {
                            r.Message = $"The write failed at sector {lba:N0} ({100.0 * lba / isoSectors:0.0}%): {rep.Describe()}. The BD-RE can be erased and used again.";
                            log?.Invoke("Direct write: " + r.Message);
                            return;
                        }
                        lba += n;

                        if (speedClock.Elapsed.TotalSeconds >= 1)
                        {
                            double now = (lba - speedFrom) * (double)Sector / speedClock.Elapsed.TotalSeconds;
                            speed = speed <= 0 ? now : speed * 0.7 + now * 0.3;
                            speedFrom = lba;
                            speedClock.Restart();
                        }
                        if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250) || lba >= isoSectors)
                        {
                            lastReport = clock.Elapsed;
                            progress?.Report(new Progress
                            {
                                Phase = "Writing",
                                Percent = 100.0 * lba / isoSectors,
                                BytesPerSec = speed,
                                Remaining = speed > 0 ? TimeSpan.FromSeconds((isoSectors - lba) * (double)Sector / speed) : TimeSpan.Zero,
                            });
                        }
                    }
                }

                // 5. Empty the drive's buffer onto the disc (also after a cancel,
                // so what was written is complete).
                progress?.Report(new Progress { Phase = "Finalizing", Percent = -1 });
                rep = drive.Command(new byte[] { 0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 600);
                for (int tries = 0; !rep.Ok && rep.IsTryAgain && tries < 300; tries++)
                {
                    Thread.Sleep(1000);
                    rep = drive.Command(new byte[] { 0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 600);
                }
                r.BurnTime = clock.Elapsed;
                if (r.Cancelled)
                {
                    r.Message = $"Cancelled at {100.0 * lba / isoSectors:0.0}%. The BD-RE can be erased and used again.";
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }
                if (!rep.Ok)
                {
                    r.Message = "The drive didn't confirm the last of the data was written: " + rep.Describe();
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }
                r.Burned = true;
                log?.Invoke($"Direct write: {isoSectors:N0} sectors written in {r.BurnTime:hh\\:mm\\:ss} (average {Fmt((long)(iso.Length / Math.Max(1, r.BurnTime.TotalSeconds)))}/s)");

                // 6. Verify: read every sector back straight from the disc
                // (not through Windows, so nothing cached is compared).
                if (verify && !ct.IsCancellationRequested)
                {
                    var vclock = Stopwatch.StartNew();
                    DirectVerify(drive, isoPath, isoSectors, progress, ct, r);
                    r.VerifyTime = vclock.Elapsed;
                    log?.Invoke(r.Verified == true
                        ? $"Direct write: verified, every sector matches ({r.VerifyTime:hh\\:mm\\:ss})"
                        : r.Verified == false ? $"Direct write: VERIFY FAILED, {r.MismatchedSectors:N0} sector(s) differ"
                        : "Direct write: verify stopped");
                }

                r.Message = r.Verified == true ? "Burned with ArchivSector-OD's own writer and verified: every byte on the disc matches the ISO."
                    : r.Verified == false ? (r.Message.Length > 0 ? r.Message : $"Burned, but {r.MismatchedSectors:N0} sector(s) on the disc don't match the ISO. Don't rely on this disc.")
                    : r.Cancelled ? "Burned with ArchivSector-OD's own writer. The check was stopped, so the disc wasn't fully verified."
                    : "Burned with ArchivSector-OD's own writer (not verified).";
            }
            finally
            {
                drive.Command(new byte[] { 0x1E, 0, 0, 0, 0, 0 }, 10);   // ALLOW MEDIUM REMOVAL
                drive.Unlock();
            }
        }

        private static void DirectVerify(ScsiDrive drive, string isoPath, long isoSectors, IProgress<Progress>? progress, CancellationToken ct, Result r)
        {
            var disc = new byte[DirectChunkSectors * Sector];
            var file = new byte[DirectChunkSectors * Sector];
            long lba = 0, mismatched = 0;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            while (lba < isoSectors)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; r.Verified = null; return; }
                int n = (int)Math.Min(DirectChunkSectors, isoSectors - lba);
                int bytes = n * Sector;
                ReadFully(fs, file, bytes);
                var target = bytes == disc.Length ? disc : new byte[bytes];
                var cdb = new byte[] { 0x28, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };
                var rep = drive.Read(cdb, target, 60);
                for (int tries = 0; !rep.Ok && rep.IsTryAgain && tries < 100; tries++) { Thread.Sleep(50); rep = drive.Read(cdb, target, 60); }
                if (!rep.Ok)
                {
                    mismatched += n;   // unreadable counts as not matching
                }
                else
                {
                    for (int s = 0; s < n; s++)
                        if (!target.AsSpan(s * Sector, Sector).SequenceEqual(file.AsSpan(s * Sector, Sector))) mismatched++;
                }
                lba += n;
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250) || lba >= isoSectors)
                {
                    lastReport = clock.Elapsed;
                    double bps = lba * (double)Sector / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                    progress?.Report(new Progress
                    {
                        Phase = "Verifying",
                        Percent = 100.0 * lba / isoSectors,
                        BytesPerSec = bps,
                        Remaining = bps > 0 ? TimeSpan.FromSeconds((isoSectors - lba) * (double)Sector / bps) : TimeSpan.Zero,
                    });
                }
            }
            r.MismatchedSectors = mismatched;
            r.Verified = mismatched == 0;
        }

        // TEST UNIT READY until the drive is ready (it may still be spinning up).
        private static bool WaitReady(ScsiDrive drive, CancellationToken ct, TimeSpan limit)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < limit && !ct.IsCancellationRequested)
            {
                var rep = drive.Command(new byte[] { 0x00, 0, 0, 0, 0, 0 }, 10);
                if (rep.Ok) return true;
                if (rep.SenseKey == 6) continue;                                 // "disc changed": just ask again
                if (rep.SenseKey == 2 && rep.Asc == 0x3A) return false;          // no disc
                Thread.Sleep(500);
            }
            return false;
        }

        // While a format runs in the background, the drive answers "not
        // ready, format in progress" and REQUEST SENSE gives how far it is.
        private static bool WaitFormatted(ScsiDrive drive, IProgress<Progress>? progress, CancellationToken ct, TimeSpan limit)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < limit)
            {
                Thread.Sleep(1000);
                var tur = drive.Command(new byte[] { 0x00, 0, 0, 0, 0, 0 }, 10);
                if (tur.Ok) return true;
                var sense = new byte[18];
                var rs = drive.Read(new byte[] { 0x03, 0, 0, 0, 18, 0 }, sense, 10);
                double pct = -1;
                if (rs.Ok && (sense[15] & 0x80) != 0)                            // progress is valid
                    pct = ((sense[16] << 8) | sense[17]) * 100.0 / 65536;
                progress?.Report(new Progress { Phase = "Formatting", Percent = pct });
                // A format can't be stopped once started; a cancel just stops
                // waiting for it at the end.
                if (!tur.IsTryAgain && tur.SenseKey != 6) return false;
            }
            return false;
        }

        private static void ReadFully(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException("The ISO ended early.");
                got += n;
            }
        }

        private static long BE32(byte[] b, int o) =>
            o + 3 < b.Length ? ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3] : 0;
    }
}
