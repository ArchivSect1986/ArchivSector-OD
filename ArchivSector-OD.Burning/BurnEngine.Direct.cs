using System.Diagnostics;

namespace ArchivSector_OD.Burning
{
    // Direct writing (v4.0 week 4): ArchivSector-OD's own writer. It sends
    // the drive the write commands itself (through ScsiDrive), instead of
    // asking Windows' burning engine or ImgBurn to do it.
    //
    // Discs so far:
    //   * BD-RE: formatted to the whole disc (no spare areas) if needed,
    //     then written straight onto from sector 0. Stopping is harmless.
    //   * DVD+RW: a brand-new one is formatted first (in the background:
    //     the drive formats while it writes). Then written straight onto
    //     from sector 0, over whatever was there; no erase needed. If the
    //     format wasn't finished, "close session" stops it so the disc
    //     reads in other drives and players. Stopping is harmless.
    //   * CD-R / CD-RW (blank): written as one data track ("track at
    //     once"), then the track and the disc are closed so every drive
    //     can read it. A CD-R stopped part-way can't be used again.
    //   * DVD+R / DVD+R DL and BD-R (blank): written straight on from the
    //     start (no write settings needed), then the track is closed and
    //     the disc finalized, so players and other drives can read it. A
    //     BD-R is used without spare areas: the whole 25 GB, like ImgBurn.
    //   * DVD-R and blank DVD-RW: "disc at once". The drive is told the
    //     exact size first (rounded up to whole 32 KB blocks, padded with
    //     zeros), and closes the disc by itself once the last sector is
    //     written. (A DVD-RW in "overwrite" mode, or one only quick-erased,
    //     may not allow this; a full erase makes it fully blank again.)
    // DVD-R DL and BD-R DL ("layer jump") come later.
    //
    // Steps:
    //   1. check the disc and that the ISO fits
    //   2. take the disc away from Windows (lock + dismount) and stop
    //      the tray being opened mid-write
    //   3. get the disc ready: format a BD-RE / set up a CD's track
    //   4. write the ISO, 64 KB at a time
    //   5. tell the drive to finish writing everything in its buffer
    //   6. CD: close the track and the disc
    //   7. (verify) read every sector back directly and compare with the ISO
    //   8. hand the disc back to Windows
    public static partial class BurnEngine
    {
        private const int DirectChunkSectors = 32;     // 64 KB per write
        private const int ProfileBdRe = 0x43, ProfileCdR = 0x09, ProfileCdRw = 0x0A, ProfileDvdPlusR = 0x1B, ProfileDvdPlusRDl = 0x2B,
            ProfileBdRSrm = 0x41, ProfileDvdDashR = 0x11, ProfileDvdRwSequential = 0x14, ProfileDvdRwOverwrite = 0x13, ProfileDvdPlusRw = 0x1A;

        /// <summary>Can our own writer handle this kind of disc yet?</summary>
        public static bool DirectWriteSupports(string mediaType) => mediaType is "BD-RE" or "DVD+RW" or "CD-R" or "CD-RW" or "DVD+R" or "DVD+R DL" or "BD-R" or "DVD-R" or "DVD-RW";

        /// <summary>Is stopping our own writer harmless on this disc (it can be written again)?</summary>
        public static bool DirectStopIsSafe(string mediaType) => mediaType is "BD-RE" or "DVD+RW";

        /// <summary>
        /// Burns an ISO with ArchivSector-OD's own writer. Same progress and
        /// result as BurnAsync, so the screens treat it alike.
        /// </summary>
        public static Task<Result> BurnDirectAsync(string driveLetter, string isoPath, bool verify,
            IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null, double speedX = 0)
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
                    DirectBurn(letter, isoPath, verify, progress, ct, r, log, speedX);
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

        private static void DirectBurn(string letter, string isoPath, bool verify, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log, double speedX = 0)
        {
            var iso = new FileInfo(isoPath);
            if (!iso.Exists) { r.Message = "The ISO file isn't there any more."; return; }
            if (iso.Length == 0 || iso.Length % Sector != 0) { r.Message = "This file isn't a disc image made of whole 2 KB sectors, so it can't be burned as-is."; return; }
            long isoSectors = iso.Length / Sector;

            // Until something is sent that changes the disc (a format, a
            // track reservation, the first write), another program can take
            // over if this writer can't go on.
            r.DiscUntouched = true;
            progress?.Report(new Progress { Phase = "Preparing", Percent = -1 });
            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }
            if (!WaitReady(drive, ct, TimeSpan.FromSeconds(30))) { r.Message = "The drive didn't get ready (is a disc in it?)."; return; }

            // 1. Which disc?
            var cfg = new byte[8];
            var rep = drive.Read(new byte[] { 0x46, 0x02, 0, 0, 0, 0, 0, 0, 8, 0 }, cfg, 10);
            int profile = rep.Ok ? (cfg[6] << 8) | cfg[7] : 0;
            bool isCd = profile is ProfileCdR or ProfileCdRw;
            // DVD+R and BD-R are written and closed the same way ("sequential").
            bool isDvdPlusR = profile is ProfileDvdPlusR or ProfileDvdPlusRDl or ProfileBdRSrm;
            bool isDvdDashR = profile is ProfileDvdDashR or ProfileDvdRwSequential;
            if (profile == ProfileDvdRwOverwrite)
            {
                r.Message = "This DVD-RW is in \"overwrite\" mode, which ArchivSector-OD's own writer doesn't write yet. Give it a full erase (🧽 Erase this disc… → Full erase) to make it fully blank, or burn with ImgBurn or Windows.";
                return;
            }
            bool isDvdPlusRw = profile == ProfileDvdPlusRw;
            if (profile != ProfileBdRe && !isDvdPlusRw && !isCd && !isDvdPlusR && !isDvdDashR)
            {
                r.Message = "ArchivSector-OD's own writer writes BD-R, BD-RE, DVD+R, DVD+RW, DVD-R, DVD-RW, CD-R and CD-RW discs so far. Use ImgBurn or Windows for this disc.";
                return;
            }
            string discName = profile switch
            {
                ProfileBdRe => "BD-RE", ProfileDvdPlusRw => "DVD+RW", ProfileCdR => "CD-R", ProfileCdRw => "CD-RW",
                ProfileDvdPlusR => "DVD+R", ProfileDvdPlusRDl => "DVD+R DL",
                ProfileBdRSrm => "BD-R", ProfileDvdRwSequential => "DVD-RW", _ => "DVD-R",
            };
            bool reusable = profile is ProfileBdRe or ProfileDvdPlusRw;
            bool erasable = profile is ProfileCdRw or ProfileDvdRwSequential;
            string afterFailure = reusable ? $"The {discName} can simply be written again." : $"A {discName} written part-way can't be used again{(erasable ? " until it's erased" : "")}.";

            // 2. Take the disc from Windows; keep the tray shut while writing.
            drive.LockAndDismount();
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);   // PREVENT MEDIUM REMOVAL
            try
            {
                // 3. Get the disc ready; where writing starts.
                long startLba;
                int cdTrack = 1;
                long padSectors = 0;    // DVD-R: zeros written after the ISO to fill the reserved size
                bool stopBackgroundFormat = false;   // DVD+RW: "close session" at the end
                if (isCd)
                {
                    if (!PrepareCd(drive, discName, isoSectors, iso.Length, r, log, out startLba, out cdTrack)) return;
                }
                else if (isDvdPlusR)
                {
                    if (!PrepareDvdPlusR(drive, discName, isoSectors, iso.Length, r, log, out startLba, out cdTrack)) return;
                }
                else if (isDvdPlusRw)
                {
                    if (!PrepareDvdPlusRw(drive, isoSectors, iso.Length, r, log, out stopBackgroundFormat)) return;
                    startLba = 0;
                }
                else if (isDvdDashR)
                {
                    if (!PrepareDvdDashR(drive, discName, isoSectors, iso.Length, r, log, out startLba, out padSectors)) return;
                }
                else
                {
                    if (!PrepareBdRe(drive, letter, isoSectors, iso.Length, progress, ct, r, log)) return;
                    startLba = 0;
                }

                // The speed asked for (0 = the fastest the drive offers, set above).
                SetDirectWriteSpeed(drive, profile, speedX, startLba + isoSectors + padSectors - 1, log);

                // 4. Write.
                r.DiscUntouched = false;
                var clock = Stopwatch.StartNew();
                long written = WriteSectors(drive, isoPath, startLba, isoSectors, padSectors, progress, ct, r, log, afterFailure);
                if (written < 0) return;   // failed; r.Message says why

                // 5. Empty the drive's buffer onto the disc (also after a cancel,
                // so what was written is complete).
                progress?.Report(new Progress { Phase = "Finalizing", Percent = -1 });
                rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 600), 300, 1000);
                // DVD+RW whose format was still running: stop it (also after a
                // cancel), so the disc is readable everywhere.
                if (isDvdPlusRw && stopBackgroundFormat) StopDvdPlusRwFormat(drive, progress, log);
                if (r.Cancelled)
                {
                    r.BurnTime = clock.Elapsed;
                    r.Message = $"Cancelled at {100.0 * written / isoSectors:0.0}%. {afterFailure}";
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }
                if (!rep.Ok)
                {
                    r.Message = "The drive didn't confirm the last of the data was written: " + rep.Describe() + " " + afterFailure;
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }

                // 6. CD: close the track, then close the disc (finalize) so
                // every drive and player can read it.
                if (isCd && !CloseCd(drive, cdTrack, progress, ct, r, log)) return;
                if (isDvdPlusR && !CloseDvdPlusR(drive, discName, cdTrack, progress, r, log)) return;
                // DVD-R (disc at once): the drive closes the disc by itself
                // after the last sector; wait until it's done.
                if (isDvdDashR)
                {
                    progress?.Report(new Progress { Phase = "Closing the disc", Percent = -1 });
                    if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(15)))
                    {
                        r.Message = $"The drive didn't finish closing the {discName} in time.";
                        log?.Invoke("Direct write: " + r.Message);
                        return;
                    }
                    log?.Invoke($"Direct write: {discName} closed by the drive (disc at once)");
                }

                r.BurnTime = clock.Elapsed;
                r.Burned = true;
                RestoreDriveSpeed(drive, profile, speedX);   // the verify reads at full speed
                log?.Invoke($"Direct write: {isoSectors:N0} sectors written to the {discName} in {r.BurnTime:hh\\:mm\\:ss} (average {Fmt((long)(iso.Length / Math.Max(1, r.BurnTime.TotalSeconds)))}/s)");

                // 7. Verify: read every sector back straight from the disc
                // (not through Windows, so nothing cached is compared).
                if (verify && !ct.IsCancellationRequested)
                {
                    var vclock = Stopwatch.StartNew();
                    DirectVerify(drive, isoPath, startLba, isoSectors, progress, ct, r);
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
                RestoreDriveSpeed(drive, profile, speedX);
                drive.Command(new byte[] { 0x1E, 0, 0, 0, 0, 0 }, 10);   // ALLOW MEDIUM REMOVAL
                drive.Unlock();
            }
        }

        // ---- BD-RE: format to the whole disc (no spare areas) if needed ----
        private static bool PrepareBdRe(ScsiDrive drive, string letter, long isoSectors, long isoBytes, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log)
        {
            var fc = new byte[252];
            var rep = drive.Read(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0, 252, 0 }, fc, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say how big the BD-RE is: " + rep.Describe(); return false; }
            long current = BE32(fc, 4);
            bool formatted = (fc[8] & 3) == 2;
            long wholeDisc = 0;
            for (int off = 12; off + 8 <= 4 + fc[3] && off + 8 <= fc.Length; off += 8)
                if ((fc[off + 4] >> 2) == 0x31) wholeDisc = Math.Max(wholeDisc, BE32(fc, off));
            bool needFormat = !formatted || (wholeDisc > 0 && current < wholeDisc && isoSectors > current);
            long capacity = needFormat ? wholeDisc : current;
            if (capacity <= 0) { r.Message = "The drive didn't offer a size to format this BD-RE to."; return false; }
            if (isoSectors > capacity) { r.Message = $"The ISO ({Fmt(isoBytes)}) is bigger than this BD-RE ({Fmt(capacity * Sector)})."; return false; }
            string state = formatted ? $"formatted to {current:N0} sectors" : "not formatted";
            log?.Invoke($"Direct write: BD-RE in {letter}:, {state}, whole disc {wholeDisc:N0} sectors; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes)");
            if (!needFormat) return true;

            progress?.Report(new Progress { Phase = "Formatting", Percent = -1 });
            log?.Invoke($"Direct write: formatting the BD-RE to {wholeDisc:N0} sectors (no spare areas)");
            r.DiscUntouched = false;
            var param = new byte[12];
            param[1] = 0x02;                       // IMMED: answer at once, format in the background
            param[3] = 8;                          // one 8-byte format descriptor
            param[4] = (byte)(wholeDisc >> 24); param[5] = (byte)(wholeDisc >> 16);
            param[6] = (byte)(wholeDisc >> 8); param[7] = (byte)wholeDisc;
            param[8] = 0x31 << 2;                  // format type 31h: full disc, no spare areas
            rep = drive.Write(new byte[] { 0x04, 0x11, 0, 0, 0, 0 }, param, 60);
            if (!rep.Ok) { r.Message = "The drive wouldn't format the BD-RE: " + rep.Describe(); return false; }
            if (!WaitFormatted(drive, progress, ct, TimeSpan.FromMinutes(30)))
            {
                r.Cancelled = ct.IsCancellationRequested;
                r.Message = r.Cancelled ? "Cancelled while formatting. The BD-RE can be used again." : "The format didn't finish in time.";
                return false;
            }
            return true;
        }

        // ---- DVD+RW: format a brand-new disc (in the background), then write from sector 0 ----
        // A DVD+RW is written over in place, like a BD-RE: no erase needed.
        // A new one must be formatted first, but the drive does that in the
        // background ("format type 26h"), so writing can start straight away.
        private static bool PrepareDvdPlusRw(ScsiDrive drive, long isoSectors, long isoBytes, Result r, Action<string>? log, out bool stopBackgroundFormat)
        {
            stopBackgroundFormat = false;

            // Background format state (disc information, byte 7):
            // 0 = never formatted, 1 = started but stopped part-way,
            // 2 = formatting right now, 3 = fully formatted.
            var di = new byte[34];
            var rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say what's on the DVD+RW: " + rep.Describe(); return false; }
            int bgFormat = di[7] & 3;

            var fc = new byte[252];
            rep = drive.Read(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0, 252, 0 }, fc, 10);
            long capacity = rep.Ok ? BE32(fc, 4) : 0;
            if (capacity <= 0) capacity = 2_295_104;                  // a standard 4.7 GB DVD+RW
            if (isoSectors > capacity) { r.Message = $"The ISO ({Fmt(isoBytes)}) is bigger than this DVD+RW ({Fmt(capacity * Sector)})."; return false; }

            string state = bgFormat switch { 0 => "not formatted", 1 => "partly formatted", 2 => "formatting", _ => "formatted" };
            log?.Invoke($"Direct write: DVD+RW, {state}, {capacity:N0} sectors; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes); written from sector 0");

            if (bgFormat == 0)
            {
                // FORMAT UNIT, type 26h (DVD+RW full format), size "all of it",
                // IMMED so the drive answers at once and formats as it goes.
                var param = new byte[12];
                param[1] = 0x02;                   // IMMED
                param[3] = 8;                      // one 8-byte format descriptor
                param[4] = param[5] = param[6] = param[7] = 0xFF;
                param[8] = 0x26 << 2;
                r.DiscUntouched = false;
                rep = drive.Write(new byte[] { 0x04, 0x11, 0, 0, 0, 0 }, param, 60);
                if (!rep.Ok) { r.Message = "The drive wouldn't format the DVD+RW: " + rep.Describe(); return false; }
                log?.Invoke("Direct write: new DVD+RW, formatting in the background while writing");
                WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(2));
            }
            stopBackgroundFormat = bgFormat != 3;

            // Fastest write speed the drive offers for this disc.
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);
            return true;
        }

        // CLOSE SESSION on a DVD+RW stops an unfinished background format and
        // writes what other drives and players need to read the disc.
        private static void StopDvdPlusRwFormat(ScsiDrive drive, IProgress<Progress>? progress, Action<string>? log)
        {
            progress?.Report(new Progress { Phase = "Closing the disc", Percent = -1 });
            var rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);
            if (rep.Ok) WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(10));
            log?.Invoke(rep.Ok ? "Direct write: DVD+RW background format stopped (session closed)"
                               : "Direct write: the drive didn't accept \"close session\" on the DVD+RW: " + rep.Describe());
        }

        // ---- CD-R / CD-RW: set up one data track, written "track at once" ----
        private static bool PrepareCd(ScsiDrive drive, string discName, long isoSectors, long isoBytes, Result r, Action<string>? log, out long startLba, out int track)
        {
            startLba = 0;
            track = 1;

            // Blank?
            var di = new byte[34];
            var rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say what's on the CD: " + rep.Describe(); return false; }
            if ((di[2] & 3) != 0) { r.Message = $"The {discName} isn't blank." + (discName == "CD-RW" ? " Erase it first (🧽 Erase this disc…)." : ""); return false; }
            track = Math.Max(1, di[6] | (di[11] << 8));

            // Write parameters (mode page 05): track at once, a data track
            // (mode 1, 2048-byte sectors), the disc closed at the end, and
            // the drive's buffer-underrun protection on.
            var mode = new byte[256];
            rep = drive.Read(new byte[] { 0x5A, 0x08, 0x05, 0, 0, 0, 0, 0x01, 0x00, 0 }, mode, 10);
            if (!rep.Ok) { r.Message = "The drive didn't share its write settings: " + rep.Describe(); return false; }
            int bdl = (mode[6] << 8) | mode[7];
            int p = 8 + bdl;
            if ((mode[p] & 0x3F) != 0x05) { r.Message = "The drive's write settings page wasn't where expected."; return false; }
            int pageLen = mode[p + 1] + 2;
            var param = new byte[8 + pageLen];
            Array.Copy(mode, p, param, 8, pageLen);
            param[8] &= 0x3F;                      // clear the "saveable" flag; the page code stays 05
            param[8 + 2] = 0x40 | 0x01;            // buffer-underrun protection on, write type 1 = track at once, no test
            param[8 + 3] = 0x04;                   // no more sessions after this (closed disc); track mode 4 = data
            param[8 + 4] = 0x08;                   // data block type 8 = mode 1, 2048-byte sectors
            param[8 + 5] = 0;                      // link size
            param[8 + 7] = 0;                      // host application code
            param[8 + 8] = 0x00;                   // session format: CD-ROM
            if (pageLen > 15) { param[8 + 10] = param[8 + 11] = param[8 + 12] = param[8 + 13] = 0; }   // packet size: none
            if (pageLen > 15) { param[8 + 14] = 0; param[8 + 15] = 150; }                               // pause between tracks: 2 s
            rep = drive.Write(new byte[] { 0x55, 0x10, 0, 0, 0, 0, 0, (byte)(param.Length >> 8), (byte)param.Length, 0 }, param, 10);
            if (!rep.Ok) { r.Message = "The drive didn't accept the CD write settings: " + rep.Describe(); return false; }

            // Fastest write speed the drive offers for this disc (it picks the best it can).
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);

            // Where to start, and how much fits.
            var ti = new byte[48];
            rep = drive.Read(new byte[] { 0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, 0, 48, 0 }, ti, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say where the CD's track starts: " + rep.Describe(); return false; }
            bool nwaValid = (ti[7] & 0x01) != 0;
            startLba = nwaValid ? BE32(ti, 12) : 0;
            long free = BE32(ti, 16);
            if (isoSectors > free)
            {
                r.Message = $"The ISO ({isoSectors:N0} sectors) is bigger than this {discName} ({free:N0} sectors free).";
                return false;
            }
            log?.Invoke($"Direct write: blank {discName}, track {track} starts at sector {startLba:N0}, {free:N0} sectors free; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes); track at once");
            return true;
        }

        // ---- DVD+R / DVD+R DL: written straight on; no write settings needed ----
        private static bool PrepareDvdPlusR(ScsiDrive drive, string discName, long isoSectors, long isoBytes, Result r, Action<string>? log, out long startLba, out int track)
        {
            startLba = 0;
            track = 1;
            var di = new byte[34];
            var rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = $"The drive didn't say what's on the {discName}: " + rep.Describe(); return false; }
            if ((di[2] & 3) != 0) { r.Message = $"The {discName} isn't blank."; return false; }
            track = Math.Max(1, di[6] | (di[11] << 8));

            // Fastest write speed the drive offers for this disc.
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);

            var ti = new byte[48];
            rep = drive.Read(new byte[] { 0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, 0, 48, 0 }, ti, 10);
            if (!rep.Ok) { r.Message = $"The drive didn't say where the {discName}'s track starts: " + rep.Describe(); return false; }
            bool nwaValid = (ti[7] & 0x01) != 0;
            startLba = nwaValid ? BE32(ti, 12) : 0;
            long free = BE32(ti, 16);
            if (isoSectors > free)
            {
                r.Message = $"The ISO ({isoSectors:N0} sectors) is bigger than this {discName} ({free:N0} sectors free).";
                return false;
            }
            log?.Invoke($"Direct write: blank {discName}, track {track} starts at sector {startLba:N0}, {free:N0} sectors free; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes)");
            return true;
        }

        // ---- DVD-R: disc at once ----
        // Write settings: disc at once, one data track, closed at the end,
        // buffer-underrun protection on. Then the track is reserved at the
        // exact size to be written: whole 16-sector (32 KB) blocks, the
        // smallest piece a DVD is written in, so the ISO is followed by up
        // to 15 sectors of zeros.
        private static bool PrepareDvdDashR(ScsiDrive drive, string discName, long isoSectors, long isoBytes, Result r, Action<string>? log, out long startLba, out long padSectors)
        {
            startLba = 0;
            padSectors = 0;
            var di = new byte[34];
            var rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = $"The drive didn't say what's on the {discName}: " + rep.Describe(); return false; }
            if ((di[2] & 3) != 0) { r.Message = $"The {discName} isn't blank." + (discName == "DVD-RW" ? " Erase it first (🧽 Erase this disc…; a full erase if a quick one isn't enough)." : ""); return false; }
            int track = Math.Max(1, di[6] | (di[11] << 8));

            var ti = new byte[48];
            rep = drive.Read(new byte[] { 0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, 0, 48, 0 }, ti, 10);
            if (!rep.Ok) { r.Message = $"The drive didn't say how much fits on the {discName}: " + rep.Describe(); return false; }
            long free = BE32(ti, 16);
            long reserve = (isoSectors + 15) / 16 * 16;
            if (reserve > free)
            {
                r.Message = $"The ISO ({isoSectors:N0} sectors) is bigger than this {discName} ({free:N0} sectors free).";
                return false;
            }

            var mode = new byte[256];
            rep = drive.Read(new byte[] { 0x5A, 0x08, 0x05, 0, 0, 0, 0, 0x01, 0x00, 0 }, mode, 10);
            if (!rep.Ok) { r.Message = "The drive didn't share its write settings: " + rep.Describe(); return false; }
            int p = 8 + ((mode[6] << 8) | mode[7]);
            if ((mode[p] & 0x3F) != 0x05) { r.Message = "The drive's write settings page wasn't where expected."; return false; }
            int pageLen = mode[p + 1] + 2;
            var param = new byte[8 + pageLen];
            Array.Copy(mode, p, param, 8, pageLen);
            param[8] &= 0x3F;
            param[8 + 2] = 0x40 | 0x02;            // buffer-underrun protection on, write type 2 = disc at once, no test
            param[8 + 3] = 0x05;                   // no more sessions (closed disc); track mode 5 (DVD data)
            param[8 + 4] = 0x08;                   // data block type 8 = 2048-byte sectors
            param[8 + 5] = 0;                      // link size
            rep = drive.Write(new byte[] { 0x55, 0x10, 0, 0, 0, 0, 0, (byte)(param.Length >> 8), (byte)param.Length, 0 }, param, 10);
            if (!rep.Ok) { r.Message = $"The drive didn't accept the {discName} write settings" + (discName == "DVD-RW" ? " (a quick-erased DVD-RW may need a full erase first)" : "") + ": " + rep.Describe(); return false; }

            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);

            // RESERVE TRACK: the size (in sectors) goes in bytes 5-8.
            r.DiscUntouched = false;
            rep = drive.Command(new byte[] { 0x53, 0, 0, 0, 0, (byte)(reserve >> 24), (byte)(reserve >> 16), (byte)(reserve >> 8), (byte)reserve, 0 }, 60);
            if (!rep.Ok) { r.Message = $"The drive wouldn't set aside the {discName}'s track" + (discName == "DVD-RW" ? " (a quick-erased DVD-RW may need a full erase first)" : "") + ": " + rep.Describe(); return false; }
            // The drive may record the reservation now; wait until it's ready for data.
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(3));

            padSectors = reserve - isoSectors;
            log?.Invoke($"Direct write: blank {discName}, {free:N0} sectors free; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes), track reserved at {reserve:N0} sectors{(padSectors > 0 ? $" ({padSectors} sectors of zeros after the ISO)" : "")}; disc at once");
            return true;
        }

        // Close the track, then finalize the disc (so DVD players and other
        // drives can read it). A drive that doesn't know "finalize" for this
        // disc gets "close session", which also leaves it readable.
        private static bool CloseDvdPlusR(ScsiDrive drive, string discName, int track, IProgress<Progress>? progress, Result r, Action<string>? log)
        {
            progress?.Report(new Progress { Phase = "Closing the disc", Percent = -1 });
            var rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x01, 0, (byte)(track >> 8), (byte)track, 0, 0, 0, 0 }, 120), 120, 1000);
            if (!rep.Ok) { r.Message = $"The drive wouldn't close the {discName}'s track: " + rep.Describe(); log?.Invoke("Direct write: " + r.Message); return false; }
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(5));

            rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x06, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);   // finalize disc
            if (!rep.Ok && rep.SenseKey == 5)
            {
                log?.Invoke("Direct write: the drive didn't accept \"finalize\"; closing the session instead");
                rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);
            }
            if (!rep.Ok) { r.Message = $"The drive wouldn't close the {discName}: " + rep.Describe(); log?.Invoke("Direct write: " + r.Message); return false; }
            if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(15)))
            {
                r.Message = $"The drive didn't finish closing the {discName} in time.";
                log?.Invoke("Direct write: " + r.Message);
                return false;
            }
            log?.Invoke($"Direct write: {discName} track closed and disc finalized");
            return true;
        }

        // Close the track, then the session (this finalizes the disc, since no
        // more sessions were asked for). The drive writes the CD's lead-in and
        // lead-out here, which takes a minute or two.
        private static bool CloseCd(ScsiDrive drive, int track, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log)
        {
            progress?.Report(new Progress { Phase = "Closing the disc", Percent = -1 });
            var rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x01, 0, (byte)(track >> 8), (byte)track, 0, 0, 0, 0 }, 120), 120, 1000);
            if (!rep.Ok) { r.Message = "The drive wouldn't close the CD's track: " + rep.Describe(); log?.Invoke("Direct write: " + r.Message); return false; }
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(5));

            rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);
            if (!rep.Ok) { r.Message = "The drive wouldn't close the CD: " + rep.Describe(); log?.Invoke("Direct write: " + r.Message); return false; }
            if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(10)))
            {
                r.Message = "The drive didn't finish closing the CD in time.";
                log?.Invoke("Direct write: " + r.Message);
                return false;
            }
            log?.Invoke("Direct write: track and disc closed (finalized)");
            return true;
        }

        // ---- the write loop (every disc type) ----
        // Returns how many sectors were written (fewer if cancelled), or -1 if
        // a write failed (r.Message then says where and why).
        private static long WriteSectors(ScsiDrive drive, string isoPath, long startLba, long isoSectors, long padSectors, IProgress<Progress>? progress,
            CancellationToken ct, Result r, Action<string>? log, string afterFailure)
        {
            long total = isoSectors + Math.Max(0, padSectors);
            var zeros = new byte[DirectChunkSectors * Sector];
            var clock = Stopwatch.StartNew();
            var buf = new byte[DirectChunkSectors * Sector];
            long done = 0;
            double speed = 0;
            var speedClock = Stopwatch.StartNew();
            long speedFrom = 0;
            var lastReport = TimeSpan.Zero;
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            while (done < total)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                // The ISO, then (DVD-R) zeros up to the reserved size; a chunk
                // never mixes the two.
                bool inIso = done < isoSectors;
                int n = (int)Math.Min(DirectChunkSectors, inIso ? isoSectors - done : total - done);
                int bytes = n * Sector;
                byte[] data;
                if (inIso)
                {
                    ReadFully(fs, buf, bytes);
                    data = bytes == buf.Length ? buf : buf.AsSpan(0, bytes).ToArray();
                }
                else data = bytes == zeros.Length ? zeros : new byte[bytes];
                long lba = startLba + done;
                var cdb = new byte[] { 0x2A, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };

                // A drive that's busy (buffer full, or still calibrating the laser /
                // recording a DVD-R's reservation before the first data, which
                // can take a minute or more) is asked again for up to 3 minutes.
                var rep = RetryWhileBusyFor(() => drive.Write(cdb, data, 60), TimeSpan.FromMinutes(3), 50);
                if (!rep.Ok)
                {
                    r.Message = $"The write failed at sector {lba:N0} ({100.0 * done / total:0.0}%): {rep.Describe()}. {afterFailure}";
                    log?.Invoke("Direct write: " + r.Message);
                    return -1;
                }
                done += n;

                if (speedClock.Elapsed.TotalSeconds >= 1)
                {
                    double now = (done - speedFrom) * (double)Sector / speedClock.Elapsed.TotalSeconds;
                    speed = speed <= 0 ? now : speed * 0.7 + now * 0.3;
                    speedFrom = done;
                    speedClock.Restart();
                }
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250) || done >= total)
                {
                    lastReport = clock.Elapsed;
                    progress?.Report(new Progress
                    {
                        Phase = "Writing",
                        Percent = 100.0 * done / total,
                        BytesPerSec = speed,
                        Remaining = speed > 0 ? TimeSpan.FromSeconds((total - done) * (double)Sector / speed) : TimeSpan.Zero,
                    });
                }
            }
            return Math.Min(done, isoSectors);
        }

        // Like RetryWhileBusy, but for a length of time rather than a count.
        private static ScsiDrive.Reply RetryWhileBusyFor(Func<ScsiDrive.Reply> send, TimeSpan limit, int waitMs)
        {
            var clock = Stopwatch.StartNew();
            var rep = send();
            while (!rep.Ok && rep.IsTryAgain && clock.Elapsed < limit)
            {
                Thread.Sleep(rep.Asc == 0x04 && rep.Ascq is 0x07 or 0x08 ? 500 : waitMs);   // a long operation: check less often
                rep = send();
            }
            return rep;
        }

        // A drive that's only busy (buffer full, still writing) is asked again.
        private static ScsiDrive.Reply RetryWhileBusy(Func<ScsiDrive.Reply> send, int tries, int waitMs)
        {
            var rep = send();
            for (int i = 0; !rep.Ok && rep.IsTryAgain && i < tries; i++)
            {
                Thread.Sleep(waitMs);
                rep = send();
            }
            return rep;
        }

        private static void DirectVerify(ScsiDrive drive, string isoPath, long startLba, long isoSectors, IProgress<Progress>? progress, CancellationToken ct, Result r)
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
                long at = startLba + lba;
                var cdb = new byte[] { 0x28, 0, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, (byte)(n >> 8), (byte)n, 0 };
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
