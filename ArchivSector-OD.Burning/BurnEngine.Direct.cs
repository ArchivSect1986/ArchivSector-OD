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
    //   * DVD-R and blank DVD-RW: "disc at once", like ImgBurn. The drive
    //     is told the exact size first (rounded up to whole 32 KB blocks,
    //     padded with zeros), and closes the disc by itself once the last
    //     sector is written. A DVD-RW quick-erased by ArchivSector-OD (the
    //     drive's "minimal blank") takes this too.
    //     If a drive refuses disc at once on a DVD-RW, "incremental" (like
    //     growisofs) is tried: 32 KB packets from where the drive says,
    //     then the track and the disc are closed.
    //   * DVD-RW in "overwrite" mode (formatted by another program; v4.0
    //     beta 3): written straight onto from sector 0 like a DVD+RW, in
    //     whole 32 KB blocks; no erase needed. Stopping is harmless.
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
            IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null, double speedX = 0, long layerBreak = 0)
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
                    DirectBurn(letter, isoPath, verify, progress, ct, r, log, speedX, layerBreak);
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

        private static void DirectBurn(string letter, string isoPath, bool verify, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log, double speedX = 0, long layerBreak = 0)
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
            bool isDvdDashR = profile == ProfileDvdDashR;
            bool isDvdRwSequential = profile == ProfileDvdRwSequential;
            bool isDvdRwOverwrite = profile == ProfileDvdRwOverwrite;
            bool isDvdPlusRw = profile == ProfileDvdPlusRw;
            if (profile != ProfileBdRe && !isDvdPlusRw && !isCd && !isDvdPlusR && !isDvdDashR && !isDvdRwSequential && !isDvdRwOverwrite)
            {
                r.Message = "ArchivSector-OD's own writer writes BD-R, BD-RE, DVD+R, DVD+RW, DVD-R, DVD-RW, CD-R and CD-RW discs so far. Use ImgBurn or Windows for this disc.";
                return;
            }
            string discName = profile switch
            {
                ProfileBdRe => "BD-RE", ProfileDvdPlusRw => "DVD+RW", ProfileCdR => "CD-R", ProfileCdRw => "CD-RW",
                ProfileDvdPlusR => "DVD+R", ProfileDvdPlusRDl => "DVD+R DL",
                ProfileBdRSrm => "BD-R", ProfileDvdRwSequential or ProfileDvdRwOverwrite => "DVD-RW", _ => "DVD-R",
            };
            bool reusable = profile is ProfileBdRe or ProfileDvdPlusRw or ProfileDvdRwOverwrite;
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
                bool dvdRwIncremental = false;       // DVD-RW written incrementally (the drive refused disc at once)
                if (isCd)
                {
                    if (!PrepareCd(drive, discName, isoSectors, iso.Length, r, log, out startLba, out cdTrack)) return;
                }
                else if (isDvdPlusR)
                {
                    if (!PrepareDvdPlusR(drive, discName, isoPath, isoSectors, iso.Length, r, log, out startLba, out cdTrack, layerBreak)) return;
                }
                else if (isDvdPlusRw)
                {
                    if (!PrepareDvdPlusRw(drive, isoSectors, iso.Length, r, log, out stopBackgroundFormat)) return;
                    startLba = 0;
                }
                else if (isDvdDashR)
                {
                    if (!PrepareDvdDashR(drive, discName, isoSectors, iso.Length, r, log, out startLba, out padSectors, out _)) return;
                }
                else if (isDvdRwSequential)
                {
                    if (!PrepareDvdDashR(drive, discName, isoSectors, iso.Length, r, log, out startLba, out padSectors, out bool daoRefused))
                    {
                        if (!daoRefused) return;
                        // Refused outright (nothing reserved): try incremental instead.
                        log?.Invoke($"Direct write: {r.Message}; trying incremental instead");
                        r.Message = "";
                        r.DiscUntouched = true;
                        if (!PrepareDvdRwIncremental(drive, isoSectors, iso.Length, r, log, out startLba, out padSectors, out cdTrack)) return;
                        dvdRwIncremental = true;
                    }
                }
                else if (isDvdRwOverwrite)
                {
                    if (!PrepareDvdRwOverwrite(drive, isoSectors, iso.Length, r, log, out padSectors)) return;
                    startLba = 0;
                }
                else
                {
                    if (!PrepareBdRe(drive, letter, isoSectors, iso.Length, progress, ct, r, log)) return;
                    startLba = 0;
                }

                // Beta 5: first the drive's own speeds back (SET STREAMING
                // "restore defaults"), so nothing another program left set
                // (MakeMKV's reading speeds, say) holds the burn back; then
                // the speed asked for (0 = the fastest the drive offers).
                FullDriveSpeed(drive);
                SetDirectWriteSpeed(drive, profile, speedX, startLba + isoSectors + padSectors - 1, log);

                // Beta 5: calibrate the laser before the first write, as
                // ImgBurn does ("Performing OPC"). Left to itself, a drive
                // does it on the first write, and on a dual-layer BD-R that
                // can take over a minute, longer than the write was given.
                if (!isCd && !CalibrateLaser(drive, log))
                {
                    // The drive stopped answering altogether: writing would
                    // only hang the same way. Nothing has been written.
                    r.DiscUntouched = true;
                    r.NoFallback = true;   // another program would hang on the drive too
                    r.Message = $"The drive stopped answering while getting ready to write (no reply for 5 minutes). Nothing was written: the {discName} is still blank. "
                        + "Eject it and put it back in, or restart the PC (a drive just used by MakeMKV in LibreDrive mode may need that before it writes again), then try again, or use another writer.";
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }

                // 4. Write.
                r.DiscUntouched = false;
                var clock = Stopwatch.StartNew();
                long written = WriteSectors(drive, isoPath, startLba, isoSectors, padSectors, progress, ct, r, log, afterFailure);
                // DVD-RW: the drive refused the very first write (nothing was
                // written), so try its other write settings.
                if (written == WriteRefusedAtStart && dvdRwIncremental)
                {
                    int variant = 0;
                    while (written == WriteRefusedAtStart)
                    {
                        variant++;
                        if (!SetDvdRwWriteMode(drive, ref variant, r, log, out string how)) break;
                        log?.Invoke($"Direct write: trying again with other DVD-RW write settings ({how})");
                        r.Message = "";
                        written = WriteSectors(drive, isoPath, startLba, isoSectors, padSectors, progress, ct, r, log, afterFailure);
                    }
                }
                if (written == WriteRefusedAtStart && (dvdRwIncremental || isDvdRwOverwrite))
                {
                    // Nothing reached the disc: another program can still take over.
                    r.DiscUntouched = true;
                    r.Message = "The drive wouldn't let ArchivSector-OD's own writer write this DVD-RW with any of its write settings (the log has the details). Nothing was written; the disc is unchanged.";
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }
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
                    r.Message = "The drive didn't confirm the last of the data was written: " + rep.Describe() + "." + DiscProblemHint(rep) + " " + afterFailure;
                    log?.Invoke("Direct write: " + r.Message);
                    return;
                }

                // 6. CD: close the track, then close the disc (finalize) so
                // every drive and player can read it.
                if (isCd && !CloseCd(drive, cdTrack, progress, ct, r, log)) return;
                if (isDvdPlusR && !CloseDvdPlusR(drive, discName, cdTrack, progress, r, log)) return;
                if (dvdRwIncremental && !CloseTrackAndDisc(drive, discName, cdTrack, progress, r, log)) return;
                // DVD-R (disc at once): the drive closes the disc by itself
                // after the last sector; wait until it's done.
                if (isDvdDashR || (isDvdRwSequential && !dvdRwIncremental))
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
                // Beta 4: the tray is cycled first (like ImgBurn), so the disc is read fresh.
                if (verify && !ct.IsCancellationRequested && !CycleTray(drive, progress, ct, log, "Direct write"))
                    verify = false;
                // Beta 5: what a burned DVD+R / DVD+R DL tells players it is (its book type).
                if (discName.StartsWith("DVD+R", StringComparison.Ordinal) && ReadDvdLayout(drive, 0x00) is { } layout)
                    log?.Invoke($"Direct write: the disc's book type is {layout.BookName}" + (layout.L0Sectors > 0 ? $"; layer 0 holds {layout.L0Sectors:N0} sectors" : ""));
                if (verify && !ct.IsCancellationRequested)
                {
                    var vclock = Stopwatch.StartNew();
                    DirectVerify(drive, isoPath, startLba, isoSectors, progress, ct, r, log);
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
        private static bool PrepareDvdPlusR(ScsiDrive drive, string discName, string isoPath, long isoSectors, long isoBytes, Result r, Action<string>? log, out long startLba, out int track, long layerBreak = 0)
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
            // Beta 4: an Xbox 360 XGD3 game is bigger than a DVD+R DL and needs
            // its layer change at 2,133,520. That only works once the disc's
            // capacity has been raised: by Burner Max firmware, or by the
            // BurnerMAX Payload Tool sent to the drive with the blank in.
            bool xgd3 = discName == "DVD+R DL" && IsXbox360Xgd3(isoPath, isoBytes);
            if (xgd3)
            {
                string? why = null;
                if (isoSectors > free)
                    why = $"This is an Xbox 360 XGD3 game: it needs {isoSectors:N0} sectors and this DVD+R DL has {free:N0}. Raise the disc's capacity first: run the BurnerMAX Payload Tool on this drive with the blank in (each new blank needs it), or use a drive with Burner Max firmware, then burn again.";
                else if (!StealthPresent(isoPath, Xgd3StealthAt))
                    why = XboxSecurityDataBlank;
                else if (!SetLayerBreak(drive, Xgd3LayerBreak, log))
                    why = $"This is an Xbox 360 XGD3 game, which needs the layer change at sector {Xgd3LayerBreak:N0}, and the drive didn't accept it. Its capacity may not have been raised far enough: send the BurnerMAX payload again, then burn again.";
                if (why is not null)
                {
                    r.Message = why + " Nothing was written.";
                    r.NoFallback = true;
                    log?.Invoke("Direct write: " + r.Message);
                    return false;
                }
                log?.Invoke($"Direct write: blank DVD+R DL with raised capacity ({free:N0} sectors free); Xbox 360 XGD3 game image: layer break set at sector {Xgd3LayerBreak:N0}, as a console needs");
                return true;
            }
            if (isoSectors > free)
            {
                r.Message = $"The ISO ({isoSectors:N0} sectors) is bigger than this {discName} ({free:N0} sectors free).";
                return false;
            }
            log?.Invoke($"Direct write: blank {discName}, track {track} starts at sector {startLba:N0}, {free:N0} sectors free; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes)");

            // Beta 4: an Xbox 360 game (XGD2) has to change layers at an exact
            // place to work in a console; set it before anything is written.
            // A layer break from a .dvd file is used as given.
            bool xgd2 = discName == "DVD+R DL" && IsXbox360Xgd2(isoPath, isoBytes);
            if (discName == "DVD+R DL" && layerBreak > 0 && !xgd2)
            {
                if (!SetLayerBreak(drive, layerBreak, log))
                {
                    r.Message = $"The .dvd file asks for the layer change at sector {layerBreak:N0}, and the drive didn't accept it. Nothing was written: try ImgBurn with the .dvd file.";
                    r.NoFallback = true;
                    return false;
                }
                log?.Invoke($"Direct write: layer break set at sector {layerBreak:N0}, from the .dvd file");
            }
            else if (layerBreak > 0 && discName != "DVD+R DL")
                log?.Invoke($"Direct write: the .dvd file's layer break isn't needed on a single-layer {discName}");
            if (xgd2)
            {
                if (layerBreak > 0 && layerBreak != Xgd2LayerBreak)
                    log?.Invoke($"Direct write: the .dvd file says {layerBreak:N0}, but an Xbox 360 XGD2 game needs {Xgd2LayerBreak:N0}; using {Xgd2LayerBreak:N0}");
                // A Redump image keeps the disc's security data (SS, DMI, PFI)
                // apart; burned without it, a console only offers "Play DVD".
                if (!StealthPresent(isoPath, Xgd2StealthAt))
                {
                    r.Message = XboxSecurityDataBlank + " Nothing was written.";
                    r.NoFallback = true;
                    log?.Invoke("Direct write: " + r.Message);
                    return false;
                }
                if (!SetLayerBreak(drive, Xgd2LayerBreak, log))
                {
                    r.Message = $"This is an Xbox 360 game, which needs the layer change at sector {Xgd2LayerBreak:N0}, and the drive didn't accept it. Nothing was written: burn it with ImgBurn instead.";
                    return false;
                }
                log?.Invoke($"Direct write: Xbox 360 game (XGD2) image: layer break set at sector {Xgd2LayerBreak:N0}, as a console needs");
            }
            return true;
        }

        // ---- DVD+R DL layer break (beta 4) ----

        /// <summary>"XGD2" or "XGD3" for an Xbox 360 game image, else null (for the Write panel).</summary>
        public static string? XboxGameKind(string isoPath)
        {
            try
            {
                long bytes = new FileInfo(isoPath).Length;
                return IsXbox360Xgd2(isoPath, bytes) ? "XGD2" : IsXbox360Xgd3(isoPath, bytes) ? "XGD3" : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// For an Xbox 360 game image: true if its security data (PFI, DMI,
        /// SS) is in place, false if it's blank or couldn't be read (a disc
        /// burned from it would only show "Play DVD"). Null if it isn't an
        /// Xbox 360 game. For the Write panel and the burn queue, so a bad
        /// image is caught when it's picked, whatever program would burn it.
        /// </summary>
        public static bool? XboxSecurityDataPresent(string isoPath)
        {
            try
            {
                long bytes = new FileInfo(isoPath).Length;
                if (IsXbox360Xgd2(isoPath, bytes)) return StealthPresent(isoPath, Xgd2StealthAt);
                if (IsXbox360Xgd3(isoPath, bytes)) return StealthPresent(isoPath, Xgd3StealthAt);
                return null;
            }
            catch { return null; }
        }

        /// <summary>Why an Xbox 360 image with blank security data isn't burned (shown on the Write panel and in the queue).</summary>
        public const string XboxSecurityDataBlank = "This Xbox 360 game image's security data (SS, DMI and PFI) is blank or couldn't be read, so a console would only show \"Play DVD\". Run abgx360 with AutoFix on the ISO first; it patches them in.";

        /// <summary>Sectors an XGD3 game needs on the disc.</summary>
        public const long Xgd3Sectors = Xgd3Bytes / Sector;
        /// <summary>
        /// The sectors the blank in a drive can take, asked straight from the
        /// drive (so a capacity raised by the BurnerMAX payload shows at once),
        /// or 0 if the drive is busy or can't say.
        /// </summary>
        public static long BlankWritableSectors(string driveLetter)
        {
            var letter = Norm(driveLetter);
            if (IsBusy(letter)) return 0;
            try
            {
                using var drive = ScsiDrive.Open(letter, out _);
                if (drive is null) return 0;
                var di = new byte[34];
                if (!drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10).Ok) return 0;
                int track = Math.Max(1, di[6] | (di[11] << 8));
                var ti = new byte[48];
                if (!drive.Read(new byte[] { 0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, 0, 48, 0 }, ti, 10).Ok) return 0;
                return BE32(ti, 16);
            }
            catch { return 0; }
        }

        private const long Xgd2Bytes = 7_838_695_424L;   // every Xbox 360 XGD2 game image is this size
        private const long Xgd2LayerBreak = 1_913_760;
        private const long Xgd3Bytes = 8_738_846_720L;   // every Xbox 360 XGD3 game image is this size
        private const long Xgd3LayerBreak = 2_133_520;
        // Where the security data sits in each kind of image (PFI, DMI, SS; a sector each).
        private static readonly long[] Xgd2StealthAt = { 0xFD8E800L, 0xFD8F000L, 0xFD8F800L };
        private static readonly long[] Xgd3StealthAt = { 0x2076800L, 0x2077000L, 0x2077800L };

        // An Xbox 360 XGD3 game: its size, and the game file system's marker
        // 32 sectors into the game partition, which starts at 0x2080000.
        private static bool IsXbox360Xgd3(string isoPath, long isoBytes)
        {
            if (isoBytes != Xgd3Bytes) return false;
            try
            {
                using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Position = 0x2080000L + 32 * 2048;
                var b = new byte[20];
                if (fs.Read(b, 0, 20) != 20) return false;
                return System.Text.Encoding.ASCII.GetString(b) == "MICROSOFT*XBOX*MEDIA";
            }
            catch { return false; }
        }

        // An Xbox 360 XGD2 game: its size, and the game file system's marker
        // ("MICROSOFT*XBOX*MEDIA") 32 sectors into the game partition, which
        // starts at 0xFD90000 in the image.
        private static bool IsXbox360Xgd2(string isoPath, long isoBytes)
        {
            if (isoBytes != Xgd2Bytes) return false;
            try
            {
                using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Position = 0xFD90000L + 32 * 2048;
                var b = new byte[20];
                if (fs.Read(b, 0, 20) != 20) return false;
                return System.Text.Encoding.ASCII.GetString(b) == "MICROSOFT*XBOX*MEDIA";
            }
            catch { return false; }
        }

        // The security data a burned Xbox 360 game needs, at fixed places in
        // the image ("at": PFI, DMI and SS, a sector each): present if none of
        // the three is all zeros.
        private static bool StealthPresent(string isoPath, long[] at)
        {
            try
            {
                using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var b = new byte[2048];
                foreach (long o in at)
                {
                    fs.Position = o;
                    if (fs.Read(b, 0, 2048) != 2048 || Array.TrueForAll(b, x => x == 0)) return false;
                }
                return true;
            }
            catch { return false; }   // beta 4: can't read it, so it can't be checked: don't burn it
        }

        // SEND DVD STRUCTURE, format 20h (layer boundary): layer 0 holds
        // "sectors" sectors, then the drive goes on in layer 1. Read back
        // to be sure the drive took it.
        private static bool SetLayerBreak(ScsiDrive drive, long sectors, Action<string>? log)
        {
            var d = new byte[12];
            d[1] = 10;                                   // data length (the rest)
            d[8] = (byte)(sectors >> 24); d[9] = (byte)(sectors >> 16); d[10] = (byte)(sectors >> 8); d[11] = (byte)sectors;
            var rep = drive.Write(new byte[] { 0xBF, 0, 0, 0, 0, 0, 0, 0x20, 0, 12, 0, 0 }, d, 10);
            if (!rep.Ok) { log?.Invoke("Direct write: the drive didn't accept the layer break: " + rep.Describe()); return false; }
            var back = new byte[12];
            rep = drive.Read(new byte[] { 0xAD, 0, 0, 0, 0, 0, 0, 0x20, 0, 12, 0, 0 }, back, 10);
            if (!rep.Ok) return true;   // set; the drive just doesn't say
            long got = BE32(back, 8);
            if (got != sectors) { log?.Invoke($"Direct write: the drive took the layer break as sector {got:N0} instead of {sectors:N0}"); return false; }
            return true;
        }

        // ---- DVD-R: disc at once ----
        // Write settings: disc at once, one data track, closed at the end,
        // buffer-underrun protection on. Then the track is reserved at the
        // exact size to be written: whole 16-sector (32 KB) blocks, the
        // smallest piece a DVD is written in, so the ISO is followed by up
        // to 15 sectors of zeros.
        private static bool PrepareDvdDashR(ScsiDrive drive, string discName, long isoSectors, long isoBytes, Result r, Action<string>? log,
            out long startLba, out long padSectors, out bool refused)
        {
            startLba = 0;
            padSectors = 0;
            refused = false;   // the drive said "not this way" (illegal request) before anything was reserved
            var di = new byte[34];
            var rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = $"The drive didn't say what's on the {discName}: " + rep.Describe(); return false; }
            if ((di[2] & 3) != 0) { r.Message = $"The {discName} isn't blank." + (discName == "DVD-RW" ? " Erase it first (🧽 Erase this disc… → Quick erase, about a minute)." : ""); return false; }
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
            if (!rep.Ok) { refused = rep.SenseKey == 5; r.Message = $"The drive didn't accept the {discName} write settings (disc at once): " + rep.Describe(); return false; }

            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);

            // RESERVE TRACK: the size (in sectors) goes in bytes 5-8.
            r.DiscUntouched = false;
            rep = drive.Command(new byte[] { 0x53, 0, 0, 0, 0, (byte)(reserve >> 24), (byte)(reserve >> 16), (byte)(reserve >> 8), (byte)reserve, 0 }, 60);
            if (!rep.Ok) { refused = rep.SenseKey == 5; r.Message = $"The drive wouldn't set aside the {discName}'s track (disc at once): " + rep.Describe(); return false; }
            // The drive may record the reservation now; wait until it's ready for data.
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(3));

            padSectors = reserve - isoSectors;
            log?.Invoke($"Direct write: blank {discName}, {free:N0} sectors free; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes), track reserved at {reserve:N0} sectors{(padSectors > 0 ? $" ({padSectors} sectors of zeros after the ISO)" : "")}; disc at once");
            return true;
        }

        // ---- DVD-RW (sequential mode): incremental, in 32 KB packets ----
        // The way growisofs writes a DVD-RW. Write settings: incremental,
        // fixed 16-sector (32 KB) packets, one data track, the disc closed at
        // the end, buffer-underrun protection on. Unlike disc at once, a
        // quick-erased DVD-RW takes this, so no full erase is needed.
        private static bool PrepareDvdRwIncremental(ScsiDrive drive, long isoSectors, long isoBytes, Result r, Action<string>? log,
            out long startLba, out long padSectors, out int track)
        {
            startLba = 0;
            padSectors = 0;
            track = 1;
            var di = new byte[34];
            var rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say what's on the DVD-RW: " + rep.Describe(); return false; }
            if ((di[2] & 3) != 0) { r.Message = "The DVD-RW isn't blank. Erase it first (🧽 Erase this disc… → Quick erase, about a minute)."; return false; }
            track = Math.Max(1, di[6] | (di[11] << 8));

            // Write settings (mode page 05). Nothing is written to the disc yet.
            int variant = 0;
            if (!SetDvdRwWriteMode(drive, ref variant, r, log, out string how)) return false;

            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);

            // Where to start (the drive's "next writable address"), and how much fits.
            var ti = new byte[48];
            rep = drive.Read(new byte[] { 0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, 0, 48, 0 }, ti, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say where the DVD-RW's track starts: " + rep.Describe(); return false; }
            bool nwaValid = (ti[7] & 0x01) != 0;
            startLba = nwaValid ? BE32(ti, 12) : 0;
            long free = BE32(ti, 16);
            long blocks = (isoSectors + 15) / 16 * 16;
            if (blocks > free)
            {
                r.Message = $"The ISO ({isoSectors:N0} sectors) is bigger than this DVD-RW ({free:N0} sectors free).";
                return false;
            }
            padSectors = blocks - isoSectors;
            log?.Invoke($"Direct write: blank DVD-RW (sequential), track {track} starts at sector {startLba:N0}{(nwaValid ? "" : " (the drive gave no start, so 0)")}, {free:N0} sectors free; track mode {ti[5] & 0x0F:X}, data mode {ti[6] & 0x0F:X}, packet bits {ti[6] >> 4:X}; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes){(padSectors > 0 ? $", {padSectors} sectors of zeros after it" : "")}; incremental ({how})");
            return true;
        }

        // The DVD-RW incremental write settings to try, in order. Drives
        // differ in what they take: some want fixed 32 KB packets, some
        // variable ones, some a "link size". A drive can accept the settings
        // and still refuse the first write, so the burn tries the next one
        // then too (nothing has been written at that point).
        private const int DvdRwVariants = 4;
        private static string DvdRwVariantName(int v) => v switch
        {
            0 => "fixed 32 KB packets, link size 16",
            1 => "variable packets",
            2 => "fixed 32 KB packets",
            _ => "variable packets, link size 16",
        };

        // Sends the first variant from `variant` on that the drive accepts.
        private static bool SetDvdRwWriteMode(ScsiDrive drive, ref int variant, Result r, Action<string>? log, out string how)
        {
            how = "";
            var mode = new byte[256];
            var rep = drive.Read(new byte[] { 0x5A, 0x08, 0x05, 0, 0, 0, 0, 0x01, 0x00, 0 }, mode, 10);
            if (!rep.Ok) { r.Message = "The drive didn't share its write settings: " + rep.Describe(); return false; }
            int p = 8 + ((mode[6] << 8) | mode[7]);
            if ((mode[p] & 0x3F) != 0x05) { r.Message = "The drive's write settings page wasn't where expected."; return false; }
            int pageLen = mode[p + 1] + 2;
            if (pageLen < 14) { r.Message = "The drive's write settings page is too short for DVD-RW packets."; return false; }
            for (; variant < DvdRwVariants; variant++)
            {
                bool fixedPackets = variant is 0 or 2;
                bool linkSize = variant is 0 or 3;
                var param = new byte[8 + pageLen];
                Array.Copy(mode, p, param, 8, pageLen);
                param[8] &= 0x3F;
                param[8 + 2] = (byte)(0x40 | (linkSize ? 0x20 : 0));   // buffer-underrun protection on, (link size valid), write type 0 = incremental, no test
                param[8 + 3] = (byte)((fixedPackets ? 0x20 : 0) | 0x05);   // no more sessions (closed disc), (fixed packets), track mode 5 (DVD data)
                param[8 + 4] = 0x08;                   // data block type 8 = 2048-byte sectors
                param[8 + 5] = (byte)(linkSize ? 0x10 : 0);
                param[8 + 8] = 0;                      // session format
                param[8 + 10] = 0; param[8 + 11] = 0; param[8 + 12] = 0; param[8 + 13] = (byte)(fixedPackets ? 0x10 : 0);   // packet size: 16 sectors (32 KB), or none
                rep = drive.Write(new byte[] { 0x55, 0x10, 0, 0, 0, 0, 0, (byte)(param.Length >> 8), (byte)param.Length, 0 }, param, 10);
                if (rep.Ok) { how = DvdRwVariantName(variant); return true; }
                log?.Invoke($"Direct write: the drive didn't take the DVD-RW write settings ({DvdRwVariantName(variant)}): {rep.Describe()}");
            }
            r.Message = "The drive didn't accept any DVD-RW write settings (incremental): " + rep.Describe();
            return false;
        }

        // ---- DVD-RW in overwrite mode: written over in place from sector 0 ----
        // Another program formatted it ("restricted overwrite"). Like a
        // DVD+RW it's simply written over, but only in whole 32 KB blocks.
        private static bool PrepareDvdRwOverwrite(ScsiDrive drive, long isoSectors, long isoBytes, Result r, Action<string>? log, out long padSectors)
        {
            padSectors = 0;
            var fc = new byte[252];
            var rep = drive.Read(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0, 252, 0 }, fc, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say how big the DVD-RW is: " + rep.Describe(); return false; }
            long capacity = BE32(fc, 4);
            int descType = fc[8] & 3;   // 1 = unformatted, 2 = formatted, 3 = no disc
            bool formatted = descType == 2;
            log?.Invoke($"Direct write: DVD-RW in overwrite mode: format capacity {capacity:N0} sectors, descriptor type {descType} ({(descType == 2 ? "formatted" : descType == 1 ? "not fully formatted" : "unknown")})");
            if (!formatted || capacity <= 0)
            {
                // Beta 4: Windows' "Like a USB flash drive" quick format leaves
                // a DVD-RW like this. The fix is an erase, which ImgBurn can't
                // do for us either, so it doesn't take over.
                r.Message = "This DVD-RW is in \"overwrite\" mode but isn't fully formatted (Windows' quick format leaves it like that). Erase it (🧽 Erase this disc… → Quick erase, about a minute) and burn again.";
                r.NoFallback = true;
                return false;
            }
            long blocks = (isoSectors + 15) / 16 * 16;
            if (blocks > capacity) { r.Message = $"The ISO ({Fmt(isoBytes)}) is bigger than this DVD-RW ({Fmt(capacity * Sector)})."; return false; }
            padSectors = blocks - isoSectors;
            log?.Invoke($"Direct write: DVD-RW in overwrite mode, {capacity:N0} sectors; ISO {isoSectors:N0} sectors ({isoBytes:N0} bytes); written from sector 0, no erase needed");
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);
            return true;
        }

        // Close the track, then the session; with "no more sessions" set in
        // the write settings, that finalizes the disc so players and other
        // drives can read it (DVD-RW written incrementally).
        private static bool CloseTrackAndDisc(ScsiDrive drive, string discName, int track, IProgress<Progress>? progress, Result r, Action<string>? log)
        {
            progress?.Report(new Progress { Phase = "Closing the disc", Percent = -1 });
            var rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x01, 0, (byte)(track >> 8), (byte)track, 0, 0, 0, 0 }, 120), 120, 1000);
            if (!rep.Ok) { r.Message = $"The drive wouldn't close the {discName}'s track: " + rep.Describe() + "." + DiscProblemHint(rep); log?.Invoke("Direct write: " + r.Message); return false; }
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(5));

            rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);
            if (!rep.Ok) { r.Message = $"The drive wouldn't close the {discName}: " + rep.Describe() + "." + DiscProblemHint(rep); log?.Invoke("Direct write: " + r.Message); return false; }
            if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(15)))
            {
                r.Message = $"The drive didn't finish closing the {discName} in time.";
                log?.Invoke("Direct write: " + r.Message);
                return false;
            }
            log?.Invoke($"Direct write: {discName} track closed and disc finalized");
            return true;
        }

        // Close the track, then finalize the disc (so DVD players and other
        // drives can read it). A drive that doesn't know "finalize" for this
        // disc gets "close session", which also leaves it readable.
        private static bool CloseDvdPlusR(ScsiDrive drive, string discName, int track, IProgress<Progress>? progress, Result r, Action<string>? log)
        {
            progress?.Report(new Progress { Phase = "Closing the disc", Percent = -1 });
            var rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x01, 0, (byte)(track >> 8), (byte)track, 0, 0, 0, 0 }, 120), 120, 1000);
            if (!rep.Ok) { r.Message = $"The drive wouldn't close the {discName}'s track: " + rep.Describe() + "." + DiscProblemHint(rep); log?.Invoke("Direct write: " + r.Message); return false; }
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(5));

            rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x06, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);   // finalize disc
            if (!rep.Ok && rep.SenseKey == 5)
            {
                log?.Invoke("Direct write: the drive didn't accept \"finalize\"; closing the session instead");
                rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);
            }
            if (!rep.Ok) { r.Message = $"The drive wouldn't close the {discName}: " + rep.Describe() + "." + DiscProblemHint(rep); log?.Invoke("Direct write: " + r.Message); return false; }
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
            if (!rep.Ok) { r.Message = "The drive wouldn't close the CD's track: " + rep.Describe() + "." + DiscProblemHint(rep); log?.Invoke("Direct write: " + r.Message); return false; }
            WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(5));

            rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x5B, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0 }, 300), 120, 1000);
            if (!rep.Ok) { r.Message = "The drive wouldn't close the CD: " + rep.Describe() + "." + DiscProblemHint(rep); log?.Invoke("Direct write: " + r.Message); return false; }
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
        // Returns how many sectors were written (fewer if cancelled), -1 if
        // a write failed (r.Message then says where and why), or
        // WriteRefusedAtStart if the drive refused the very first write
        // ("illegal request"), so nothing was written.
        private const long WriteRefusedAtStart = -2;
        // SEND OPC INFORMATION with DoOpc: the drive calibrates its laser for
        // this disc now. Some drives say no (they calibrate on the first
        // write instead), which is fine. Up to 5 minutes.
        // False: the drive didn't answer at all (Windows gave up on it), so
        // it's stuck and writing would hang the same way.
        private static bool CalibrateLaser(ScsiDrive drive, Action<string>? log)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var rep = drive.Command(new byte[] { 0x54, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 }, 300);
                if (rep.Ok) log?.Invoke($"Direct write: laser calibrated for this disc (OPC) in {sw.Elapsed.TotalSeconds:0.0} s");
                else if (rep.Win32Error != 0)
                {
                    log?.Invoke($"Direct write: the drive didn't answer the laser calibration ({rep.Describe()} after {sw.Elapsed.TotalSeconds:0} s)");
                    return false;
                }
                else log?.Invoke("Direct write: the drive calibrates its laser by itself on the first write (" + rep.Describe() + ")");
            }
            catch { /* calibration is optional */ }
            return true;
        }

        private static long WriteSectors(ScsiDrive drive, string isoPath, long startLba, long isoSectors, long padSectors, IProgress<Progress>? progress,
            CancellationToken ct, Result r, Action<string>? log, string afterFailure)
        {
            long total = isoSectors + Math.Max(0, padSectors);
            int chunkBytes = DirectChunkSectors * Sector;
            var clock = Stopwatch.StartNew();
            long done = 0;
            double speed = 0;
            var speedClock = Stopwatch.StartNew();
            long speedFrom = 0;
            var lastReport = TimeSpan.Zero;

            // Beta 4: the ISO is read ahead into an 80 MiB buffer by its own
            // thread, as ImgBurn does, and the buffer is filled before the
            // first write. Reading and writing one after the other left the
            // drive waiting whenever the hard drive was slow for a moment (a
            // cold start, another drive reading the same disk); a DVD burner
            // with a small buffer (512 KB = a tenth of a second at 4x) then
            // has to stop and restart the laser, and on some discs those
            // restarts can't be read back.
            int ring = (int)Math.Min(WriteBufferBytes / chunkBytes, (total + DirectChunkSectors - 1) / DirectChunkSectors);
            ring = Math.Max(ring, 1);
            using var free = new System.Collections.Concurrent.BlockingCollection<byte[]>(ring);
            using var filled = new System.Collections.Concurrent.BlockingCollection<(byte[] Buf, int Bytes)>(ring);
            for (int i = 0; i < ring; i++) free.Add(new byte[chunkBytes]);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Exception? readError = null;
            var reader = Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    long queued = 0;
                    while (queued < total)
                    {
                        var buf = free.Take(stop.Token);
                        // The ISO, then (DVD-R/-RW) zeros up to a whole 32 KB block.
                        // The end of the ISO and the first zeros share a chunk, so
                        // every write stays a whole number of 32 KB packets (a DVD-RW
                        // written incrementally or in overwrite mode needs that).
                        int n = (int)Math.Min(DirectChunkSectors, total - queued);
                        int fromIso = (int)Math.Clamp(isoSectors - queued, 0, n);
                        if (fromIso < n) Array.Clear(buf, fromIso * Sector, (n - fromIso) * Sector);
                        if (fromIso > 0) ReadFully(fs, buf, fromIso * Sector);
                        filled.Add((buf, n * Sector), stop.Token);
                        queued += n;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { readError = ex; }
                finally { filled.CompleteAdding(); }
            });

            int waits = 0;
            long waitedMs = 0, longestWaitMs = 0;
            try
            {
                // Fill the buffer before the first write (or until the reader is done).
                progress?.Report(new Progress { Phase = "Filling the buffer", Percent = -1 });
                var fillClock = Stopwatch.StartNew();
                while (filled.Count < ring && !filled.IsAddingCompleted && !ct.IsCancellationRequested && fillClock.Elapsed < TimeSpan.FromSeconds(60))
                    Thread.Sleep(20);
                log?.Invoke($"Direct write: buffer filled ({filled.Count * (long)chunkBytes / (1024 * 1024)} MiB of {ring * (long)chunkBytes / (1024 * 1024)} MiB) in {fillClock.Elapsed.TotalSeconds:0.0} s; writing");

                while (done < total)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                    (byte[] Buf, int Bytes) item;
                    if (!filled.TryTake(out item))
                    {
                        // The buffer ran dry: the drive waits for the hard drive.
                        var w = Stopwatch.StartNew();
                        try { item = filled.Take(ct); }
                        catch (OperationCanceledException) { r.Cancelled = true; break; }
                        catch (InvalidOperationException) { item = default; }
                        if (item.Buf is null)
                        {
                            r.Message = "The ISO couldn't be read: " + (readError?.Message ?? "it ended early") + " " + afterFailure;
                            log?.Invoke("Direct write: " + r.Message);
                            return -1;
                        }
                        waits++;
                        long ms = w.ElapsedMilliseconds;
                        waitedMs += ms;
                        if (ms > longestWaitMs) longestWaitMs = ms;
                    }
                    int n = item.Bytes / Sector;
                    var data = item.Bytes == item.Buf.Length ? item.Buf : item.Buf.AsSpan(0, item.Bytes).ToArray();
                    long lba = startLba + done;
                    var cdb = new byte[] { 0x2A, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };

                    // A drive that's busy (buffer full, or still calibrating the laser /
                    // recording a DVD-R's reservation before the first data, which
                    // can take a minute or more) is asked again for up to 3 minutes.
                    // Beta 5: the first write gets 5 minutes (the drive may still be
                    // calibrating, a dual-layer BD-R especially); the rest 60 seconds.
                    int writeTimeout = done == 0 ? 300 : 60;
                    var rep = RetryWhileBusyFor(() => drive.Write(cdb, data, writeTimeout), TimeSpan.FromMinutes(3), 50);
                    if (!free.IsAddingCompleted) free.Add(item.Buf);
                    if (!rep.Ok)
                    {
                        if (done == 0 && rep.SenseKey == 5)
                        {
                            // Refused outright before anything was written.
                            r.Message = $"The drive refused the first write: {rep.Describe()}";
                            log?.Invoke("Direct write: " + r.Message);
                            return WriteRefusedAtStart;
                        }
                        r.Message = $"The write failed at sector {lba:N0} ({100.0 * done / total:0.0}%): {rep.Describe()}.{DiscProblemHint(rep)} {afterFailure}";
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
            }
            finally
            {
                stop.Cancel();
                try { reader.Wait(TimeSpan.FromSeconds(10)); } catch { }
            }
            log?.Invoke(waits == 0
                ? "Direct write: the buffer never ran dry (the drive never waited for data)"
                : $"Direct write: the buffer ran dry {waits:N0} time(s), the drive waited {waitedMs / 1000.0:0.0} s in all (longest {longestWaitMs:N0} ms): the hard drive the ISO is on couldn't keep up for a moment");
            return Math.Min(done, isoSectors);
        }

        // How much of the ISO is read ahead of the drive (as ImgBurn's 80 MiB buffer).
        private const long WriteBufferBytes = 80L * 1024 * 1024;

        // A "medium error" (sense key 3) is the disc itself failing, not our
        // writer or the drive: say what to try next.
        private static string DiscProblemHint(ScsiDrive.Reply rep) =>
            rep.SenseKey == 3
                ? " This usually means a faulty disc, or one this drive doesn't get along with: try another disc, a slower speed, or another drive."
                : "";

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

        // The verify after burning. Beta 5: the same sector check as Verify
        // disc (CheckChunk), so both count unreadable and different sectors
        // the same way and list them, and it stops early on a hopeless disc.
        private static void DirectVerify(ScsiDrive drive, string isoPath, long startLba, long isoSectors, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log = null)
        {
            var disc = new byte[DirectChunkSectors * Sector];
            var file = new byte[DirectChunkSectors * Sector];
            long lba = 0;
            var check = new IsoCheck();
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var window = new Queue<(TimeSpan T, long Lba)>();
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            bool goOn = true;
            while (lba < isoSectors && goOn)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; r.Verified = null; return; }
                int n = (int)Math.Min(DirectChunkSectors, isoSectors - lba);
                ReadFully(fs, file, n * Sector);
                goOn = CheckChunk(drive, startLba + lba, lba, n, file, disc, check, ct);
                lba += n;
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250) || lba >= isoSectors)
                {
                    lastReport = clock.Elapsed;
                    // The speed over the last 5 seconds (not the average since
                    // the start), so it shows what the drive is doing now.
                    var now = clock.Elapsed;
                    window.Enqueue((now, lba));
                    while (window.Count > 1 && now - window.Peek().T > TimeSpan.FromSeconds(5)) window.Dequeue();
                    var first = window.Peek();
                    double secs = (now - first.T).TotalSeconds;
                    double bps = secs > 0.2 ? (lba - first.Lba) * (double)Sector / secs : lba * (double)Sector / Math.Max(0.001, now.TotalSeconds);
                    progress?.Report(new Progress
                    {
                        Phase = "Verifying",
                        Percent = 100.0 * lba / isoSectors,
                        BytesPerSec = bps,
                        Remaining = bps > 0 ? TimeSpan.FromSeconds((isoSectors - lba) * (double)Sector / bps) : TimeSpan.Zero,
                    });
                }
            }
            check.Finish();
            r.MismatchedSectors = check.Bad;
            r.DifferentSectors = check.Different;
            r.UnreadableSectors = check.Unreadable;
            r.BadRanges = check.Ranges;
            r.Verified = check.Bad == 0 && check.StoppedEarly is null;
            if (r.Verified == false)
            {
                r.Message = check.StoppedEarly is not null
                    ? $"Burned, but the check stopped early at {100.0 * lba / isoSectors:0.0}%: {check.StoppedEarly}."
                    : $"Burned, but {check.Bad:N0} sector(s) on the disc don't match the ISO ({check.Different:N0} differ, {check.Unreadable:N0} can't be read). Don't rely on this disc.";
                if (BdLayerChangeHint(check.FirstBad) is { } layerHint) r.Message += layerHint;
                log?.Invoke($"Direct write: {check.Different:N0} sector(s) differ and {check.Unreadable:N0} can't be read" + (check.StoppedEarly is not null ? " (the check stopped early)" : ""));
                if (check.Ranges.Count > 0) log?.Invoke("Direct write: problem sectors at " + check.RangesText());
            }
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
                // The progress comes with TEST UNIT READY's own answer (as
                // ImgBurn reads it). A separate REQUEST SENSE here could stall
                // the drive for minutes and break off a DVD-RW erase (beta 3).
                progress?.Report(new Progress { Phase = "Formatting", Percent = tur.Progress });
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
