using System.Diagnostics;

namespace ArchivSector_OD.Burning
{
    // BIN/CUE burning (v4.0 beta 2): a CD image made of .bin file(s) and a
    // .cue, burned by ArchivSector-OD's own writer "session at once" (the
    // way ImgBurn and cdrdao burn them):
    //   1. the drive gets the whole layout first (SEND CUE SHEET): every
    //      track, its type, its gaps and where the lead-out goes;
    //   2. every sector is then sent in order from LBA -150 (the gap before
    //      track 1): music as 2352-byte sectors, data as the part the disc
    //      really stores (2048 bytes for mode 1, 2336 for mode 2), and the
    //      drive adds the sync, headers and error correction itself;
    //   3. the drive writes the lead-in and lead-out and closes the CD.
    // Verify reads every sector back: data must match exactly; music is
    // compared after finding how far the drive's read/write offset shifts it
    // (a few samples, normal for every drive), so a shift alone isn't a fault.
    public static partial class BurnEngine
    {
        public static Task<Result> BurnCueAsync(string driveLetter, string cuePath, bool verify, IProgress<Progress>? progress, CancellationToken ct,
            Action<string>? log = null, double speedX = 0)
        {
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new Result { Message = "This drive is already burning." });
            return Task.Factory.StartNew(() =>
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                var r = new Result();
                try { DirectBurnCue(letter, cuePath, verify, progress, ct, r, log, speedX); }
                catch (Exception ex)
                {
                    r.Message = (r.Burned ? "Burned, but checking the disc failed: " : "The burn failed: ") + ex.Message;
                    log?.Invoke("BIN/CUE: " + r.Message);
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

        private static void DirectBurnCue(string letter, string cuePath, bool verify, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log, double speedX)
        {
            r.DiscUntouched = true;
            progress?.Report(new Progress { Phase = "Preparing", Percent = -1 });
            var img = CueImage.Load(cuePath);
            if (img.Problem is not null) { r.Message = img.Problem; log?.Invoke("BIN/CUE: " + r.Message); return; }
            log?.Invoke($"BIN/CUE: {Path.GetFileName(cuePath)}: {img.Summary}");

            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }
            if (!WaitReady(drive, ct, TimeSpan.FromSeconds(30))) { r.Message = "The drive didn't get ready (is a blank CD in it?)."; return; }

            var cfg = new byte[8];
            var rep = drive.Read(new byte[] { 0x46, 0x02, 0, 0, 0, 0, 0, 0, 8, 0 }, cfg, 10);
            int profile = rep.Ok ? (cfg[6] << 8) | cfg[7] : 0;
            if (profile is not (ProfileCdR or ProfileCdRw)) { r.Message = "A BIN/CUE image is a CD image: put a blank CD-R or CD-RW in the drive."; return; }
            string discName = profile == ProfileCdR ? "CD-R" : "CD-RW";

            var di = new byte[34];
            rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            if (!rep.Ok) { r.Message = "The drive didn't say what's on the CD: " + rep.Describe(); return; }
            if ((di[2] & 3) != 0) { r.Message = $"The {discName} isn't blank." + (profile == ProfileCdRw ? " Erase it first (🧽 Erase this disc…)." : ""); return; }

            // Room? The disc's lead-out start (ATIP) is the most it can hold.
            long capacity = 0;
            var atip = new byte[28];
            rep = drive.Read(new byte[] { 0x43, 0x02, 0x04, 0, 0, 0, 0, 0, 28, 0 }, atip, 10);
            if (rep.Ok && ((atip[0] << 8) | atip[1]) >= 22) capacity = (atip[12] * 60L + atip[13]) * 75 + atip[14] - 150;
            // Where the lead-in starts (ATIP), for CD-Text: a time from 90:00:00 on
            // is before the disc's zero point (97:34:23 → about -11,800).
            long leadInLba = 0;
            if (rep.Ok && ((atip[0] << 8) | atip[1]) >= 22)
            {
                long msf = (atip[8] * 60L + atip[9]) * 75 + atip[10];
                leadInLba = atip[8] >= 90 ? msf - 450150 : msf - 150;
            }
            if (capacity <= 0)
            {
                var ti = new byte[48];
                rep = drive.Read(new byte[] { 0x52, 0x01, 0, 0, 0, 1, 0, 0, 48, 0 }, ti, 10);
                if (rep.Ok) capacity = BE32(ti, 16);
            }
            if (capacity > 0 && img.LeadOutLba > capacity)
            {
                r.Message = $"The image needs {img.LeadOutLba:N0} sectors but this {discName} holds {capacity:N0}.";
                return;
            }
            log?.Invoke($"BIN/CUE: blank {discName}, room for {capacity:N0} sectors; image {img.LeadOutLba + 150:N0} sectors incl. the 2-second gap; session at once" +
                        (img.HasMode2 ? " (CD-ROM XA)" : ""));

            drive.LockAndDismount();
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);
            try
            {
                drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);
                SetDirectWriteSpeed(drive, profile, speedX, img.LeadOutLba, log);

                string afterFailure = profile == ProfileCdRw ? "The CD-RW can be erased and used again." : "A CD-R written part-way can't be used again.";
                var clock = Stopwatch.StartNew();
                long done = 0, total = img.BurnSectors;

                // Each session in turn (an Enhanced CD has two: music, then data).
                // A session before the last is left open, so the drive notes
                // where the next one may start; the last one closes the disc.
                for (int session = 1; session <= img.Sessions; session++)
                {
                    var tracks = img.SessionTracks(session);
                    bool last = session == img.Sessions;
                    bool xa = tracks.Any(t => t.Mode is CueImage.TrackMode.Mode2Raw or CueImage.TrackMode.Mode2Cooked);
                    string which = img.Sessions > 1 ? $"session {session} of {img.Sessions}: " : "";

                    // A later session: the drive must be ready to add it exactly
                    // where the image puts it (after the earlier lead-out and
                    // this lead-in), or the data in it wouldn't be found.
                    if (session > 1)
                    {
                        var di2 = new byte[34];
                        rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di2, 10);
                        var ti = new byte[48];
                        var trep = drive.Read(new byte[] { 0x52, 0x01, 0, 0, 0, 0xFF, 0, 0, 48, 0 }, ti, 10);   // the next (not yet written) track
                        long nwa = trep.Ok && (ti[7] & 0x01) != 0 ? BE32(ti, 12) : -1;
                        log?.Invoke($"BIN/CUE: {which}disc {(rep.Ok ? (di2[2] & 3) switch { 0 => "blank", 1 => "open for another session", 2 => "closed", _ => "?" } : "unknown")}, " +
                                    $"next writable sector {(nwa >= 0 ? nwa.ToString("N0") : "not given")}; the image's session starts at {tracks[0].Index1Lba:N0} (gap from {tracks[0].PregapLba:N0})");
                        if (rep.Ok && (di2[2] & 3) != 1)
                        {
                            r.Message = $"The drive didn't leave the {discName} open for session {session}, so it can't be added. " + afterFailure;
                            log?.Invoke("BIN/CUE: " + r.Message);
                            return;
                        }
                        if (nwa >= 0 && nwa != tracks[0].Index1Lba && nwa != tracks[0].PregapLba)
                        {
                            r.Message = $"The drive would start session {session} at sector {nwa:N0}, but the image needs it at {tracks[0].Index1Lba:N0}. " +
                                        "The data in that session wouldn't be found there, so it wasn't written. " + afterFailure;
                            log?.Invoke("BIN/CUE: " + r.Message);
                            return;
                        }
                    }

                    // Write parameters: session at once, buffer-underrun protection,
                    // another session to follow or the disc closed, CD-ROM or CD-ROM XA.
                    var mode = new byte[256];
                    rep = drive.Read(new byte[] { 0x5A, 0x08, 0x05, 0, 0, 0, 0, 0x01, 0x00, 0 }, mode, 10);
                    if (!rep.Ok) { r.Message = "The drive didn't share its write settings: " + rep.Describe(); return; }
                    int p = 8 + ((mode[6] << 8) | mode[7]);
                    if ((mode[p] & 0x3F) != 0x05) { r.Message = "The drive's write settings page wasn't where expected."; return; }
                    int pageLen = mode[p + 1] + 2;
                    var param = new byte[8 + pageLen];
                    Array.Copy(mode, p, param, 8, pageLen);
                    param[8] &= 0x3F;
                    param[8 + 2] = 0x40 | 0x02;            // buffer-underrun protection on, write type 2 = session at once
                    param[8 + 3] = last ? (byte)0x00 : (byte)0xC0;   // last session: closed disc; otherwise "next session allowed"
                    param[8 + 4] = 0x00;                   // data block type: raw (not used for session at once)
                    param[8 + 8] = xa ? (byte)0x20 : (byte)0x00;     // session format: CD-ROM XA or CD-DA/CD-ROM
                    rep = drive.Write(new byte[] { 0x55, 0x10, 0, 0, 0, 0, 0, (byte)(param.Length >> 8), (byte)param.Length, 0 }, param, 10);
                    if (!rep.Ok)
                    {
                        r.Message = (img.Sessions > 1 && !last ? "The drive didn't accept session-at-once writing with another session to follow: " : "The drive didn't accept session-at-once writing: ") + rep.Describe() + (session > 1 ? " " + afterFailure : "");
                        log?.Invoke("BIN/CUE: " + r.Message);
                        return;
                    }

                    // CD-Text (beta 4): album and track names, in the first session's lead-in.
                    List<byte[]>? textPacks = null;
                    if (session == 1 && img.CdText is { HasAny: true } && leadInLba < -150)
                    {
                        textPacks = img.CdText.ToPacks(img.Tracks.Where(t => t.Session == 1).Max(t => t.Number));
                        if (textPacks.Count == 0) textPacks = null;
                    }

                    // 1. The layout.
                    bool SendSheet(bool withText)
                    {
                        var sheet = img.BuildCueSheet(session, withText);
                        log?.Invoke($"BIN/CUE: {which}cue sheet " + string.Join(" | ", Enumerable.Range(0, sheet.Length / 8)
                            .Select(i => string.Join(" ", sheet.Skip(i * 8).Take(8).Select(b => b.ToString("X2"))))));
                        rep = drive.Write(new byte[] { 0x5D, 0, 0, 0, 0, 0, (byte)(sheet.Length >> 16), (byte)(sheet.Length >> 8), (byte)sheet.Length, 0 }, sheet, 60);
                        return rep.Ok;
                    }
                    bool sheetOk = SendSheet(textPacks is not null);
                    if (!sheetOk && textPacks is not null)
                    {
                        log?.Invoke($"BIN/CUE: the drive didn't take the layout with CD-Text ({rep.Describe()}); burning without CD-Text");
                        textPacks = null;
                        r.CdTextNote = " The drive wouldn't write CD-Text, so the disc has no album or track names.";
                        sheetOk = SendSheet(false);
                    }
                    if (!sheetOk) { r.Message = $"The drive didn't accept the {which}disc layout (cue sheet): " + rep.Describe() + (session > 1 ? " " + afterFailure : ""); log?.Invoke("BIN/CUE: " + r.Message); return; }

                    // 1b. CD-Text: the lead-in, written by us from its start up to
                    // LBA -150, carrying the packs over and over. If the drive
                    // refuses the very first block, nothing is written yet: the
                    // layout goes again without CD-Text.
                    if (textPacks is not null)
                    {
                        var lw = WriteCdTextLeadIn(drive, textPacks, leadInLba, progress, ct, log);
                        if (lw == LeadInResult.RefusedAtStart)
                        {
                            log?.Invoke("BIN/CUE: the drive refused the CD-Text lead-in before anything was written; burning without CD-Text");
                            r.CdTextNote = " The drive wouldn't write CD-Text, so the disc has no album or track names.";
                            if (!SendSheet(false)) { r.Message = "The drive didn't accept the disc layout (cue sheet): " + rep.Describe(); log?.Invoke("BIN/CUE: " + r.Message); return; }
                        }
                        else if (lw == LeadInResult.Failed)
                        {
                            r.DiscUntouched = false;
                            r.Message = "Writing the CD-Text (in the lead-in) failed (the log says where). " + afterFailure;
                            return;
                        }
                        else if (lw == LeadInResult.Cancelled) { r.Cancelled = true; r.DiscUntouched = false; r.Message = "Cancelled. " + afterFailure; return; }
                        else r.CdTextWritten = true;
                    }

                    // 2. Every sector of the session, from its first gap.
                    r.DiscUntouched = false;
                    if (!WriteCuePieces(drive, img.SessionPieces(session), total, ref done, progress, ct, r, log, afterFailure)) return;

                    // 3. The session's lead-out (and lead-in), and close.
                    progress?.Report(new Progress { Phase = last ? "Finalizing" : $"Closing session {session}", Percent = -1 });
                    rep = RetryWhileBusy(() => drive.Command(new byte[] { 0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 600), 300, 1000);
                    if (r.Cancelled)
                    {
                        r.BurnTime = clock.Elapsed;
                        r.Message = "Cancelled. " + afterFailure;
                        log?.Invoke("BIN/CUE: " + r.Message);
                        return;
                    }
                    if (!rep.Ok) { r.Message = "The drive didn't confirm the last of the data was written: " + rep.Describe() + "." + DiscProblemHint(rep) + " " + afterFailure; log?.Invoke("BIN/CUE: " + r.Message); return; }
                    progress?.Report(new Progress { Phase = last ? "Closing the disc" : $"Closing session {session}", Percent = -1 });
                    if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(15)))
                    {
                        r.Message = $"The drive didn't finish closing the {discName} in time.";
                        log?.Invoke("BIN/CUE: " + r.Message);
                        return;
                    }
                    if (!last) log?.Invoke($"BIN/CUE: session {session} written and closed, the {discName} left open for session {session + 1}");
                }
                r.BurnTime = clock.Elapsed;
                r.Burned = true;
                RestoreDriveSpeed(drive, profile, speedX);
                log?.Invoke($"BIN/CUE: {img.BurnSectors:N0} sectors written{(img.Sessions > 1 ? $" in {img.Sessions} sessions" : "")}{(r.CdTextWritten ? " with CD-Text" : "")} and the {discName} closed in {r.BurnTime:hh\\:mm\\:ss}");

                // CD-Text: read it back and compare with what was meant.
                if (r.CdTextWritten && img.CdText is not null)
                {
                    var back = CdText.Read(drive);
                    var want = img.CdText.Lines().ToList();
                    var got = back?.Lines().ToList() ?? new();
                    bool same = back is not null && want.SequenceEqual(got);
                    log?.Invoke(same ? $"BIN/CUE: CD-Text read back and matches ({want.Count} line(s))"
                        : back is null ? "BIN/CUE: CD-TEXT CHECK: the drive reads no CD-Text on the disc"
                        : "BIN/CUE: CD-TEXT CHECK: read back differently: " + string.Join(" | ", got.Select(l => $"{l.Label}: {l.Value}")));
                    if (!same) r.CdTextNote = back is null ? " The CD-Text didn't read back from the disc." : " The CD-Text reads back differently from the .cue.";
                }

                string audioNote = "";
                if (verify && !ct.IsCancellationRequested)
                {
                    var vclock = Stopwatch.StartNew();
                    audioNote = VerifyCue(drive, img, progress, ct, r, log);
                    r.VerifyTime = vclock.Elapsed;
                }
                string withText = r.CdTextWritten && r.CdTextNote.Length == 0 ? ", with CD-Text (album and track names)" : "";
                r.Message = r.Verified == true ? $"Burned the BIN/CUE with ArchivSector-OD's own writer{withText} and verified it{audioNote}."
                    : r.Verified == false ? (r.Message.Length > 0 ? r.Message : $"Burned, but {r.MismatchedSectors:N0} sector(s) on the disc don't match the image. Don't rely on this disc.")
                    : r.Cancelled ? "Burned the BIN/CUE with ArchivSector-OD's own writer. The check was stopped, so the disc wasn't fully verified."
                    : "Burned the BIN/CUE with ArchivSector-OD's own writer (not verified).";
                if (r.CdTextNote.Length > 0) r.Message += r.CdTextNote;
            }
            finally
            {
                RestoreDriveSpeed(drive, profile, speedX);
                drive.Command(new byte[] { 0x1E, 0, 0, 0, 0, 0 }, 10);
                drive.Unlock();
            }
        }

        private enum LeadInResult { Written, RefusedAtStart, Failed, Cancelled }

        // CD-Text in the lead-in (beta 4). The drive makes the lead-in's main
        // data and its TOC itself; we give it the R-W sub-channel of every
        // lead-in sector: 96 bytes, each holding 6 bits. One 18-byte pack is
        // 24 of those 6-bit pieces, so a sector carries 4 packs. The packs go
        // round and round from the lead-in's start to LBA -150, so a player
        // can pick them up anywhere.
        private static LeadInResult WriteCdTextLeadIn(ScsiDrive drive, List<byte[]> packs, long leadInLba, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log)
        {
            long sectors = -150 - leadInLba;
            // The whole cycle as 6-bit pieces.
            var symbols = new List<byte>(packs.Count * 24);
            foreach (var p in packs)
                for (int i = 0; i < 18; i += 3)
                {
                    symbols.Add((byte)(p[i] >> 2));
                    symbols.Add((byte)(((p[i] & 0x03) << 4) | (p[i + 1] >> 4)));
                    symbols.Add((byte)(((p[i + 1] & 0x0F) << 2) | (p[i + 2] >> 6)));
                    symbols.Add((byte)(p[i + 2] & 0x3F));
                }
            log?.Invoke($"BIN/CUE: CD-Text: {packs.Count} packs, written into the lead-in from sector {leadInLba:N0} to -151 ({sectors:N0} sectors)");
            progress?.Report(new Progress { Phase = "Writing CD-Text (lead-in)", Percent = -1 });

            const int PerWrite = 32;
            var buf = new byte[PerWrite * 96];
            int at = 0;   // place in the cycle
            for (long done = 0; done < sectors;)
            {
                if (ct.IsCancellationRequested) return LeadInResult.Cancelled;
                int n = (int)Math.Min(PerWrite, sectors - done);
                for (int b = 0; b < n * 96; b++) { buf[b] = symbols[at]; at = (at + 1) % symbols.Count; }
                long lba = leadInLba + done;
                var data = n == PerWrite ? buf : buf.AsSpan(0, n * 96).ToArray();
                var cdb = new byte[] { 0x2A, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };
                var rep = RetryWhileBusyFor(() => drive.Write(cdb, data, 60), TimeSpan.FromMinutes(3), 50);
                if (!rep.Ok)
                {
                    log?.Invoke($"BIN/CUE: CD-Text lead-in write at sector {lba:N0} failed: {rep.Describe()}");
                    return done == 0 && rep.SenseKey == 5 ? LeadInResult.RefusedAtStart : LeadInResult.Failed;
                }
                done += n;
            }
            return LeadInResult.Written;
        }

        // The sectors as the drive wants them, piece by piece, in disc order.
        // done/total count across all sessions, for one progress bar.
        private static bool WriteCuePieces(ScsiDrive drive, List<CueImage.Piece> pieces, long total, ref long done, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log, string afterFailure)
        {
            double speed = 0;
            var speedClock = Stopwatch.StartNew();
            long speedFrom = done;
            var lastReport = TimeSpan.Zero;
            var clock = Stopwatch.StartNew();
            var readBuf = new byte[64 * 2352];

            foreach (var piece in pieces)
            {
                int block = CueImage.BlockSize(piece.Mode);
                int perWrite = 65536 / block;
                var outBuf = new byte[perWrite * block];
                FileStream? fs = null;
                try
                {
                    if (piece.File is not null)
                    {
                        fs = new FileStream(piece.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                        fs.Position = piece.FileOffset;
                    }
                    for (long i = 0; i < piece.Count;)
                    {
                        if (ct.IsCancellationRequested) { r.Cancelled = true; return true; }
                        int n = (int)Math.Min(perWrite, piece.Count - i);
                        int outLen = n * block;
                        if (fs is null) Array.Clear(outBuf, 0, outLen);
                        else
                        {
                            int inLen = n * piece.FileSectorSize;
                            ReadPadded(fs, readBuf, inLen, piece.FileDataEnd);
                            for (int s = 0; s < n; s++)
                            {
                                int src = s * piece.FileSectorSize, dst = s * block;
                                switch (piece.Mode)
                                {
                                    case CueImage.TrackMode.Audio:
                                        Buffer.BlockCopy(readBuf, src, outBuf, dst, 2352);
                                        if (piece.SwapAudio)
                                            for (int b = dst; b < dst + 2352; b += 2) (outBuf[b], outBuf[b + 1]) = (outBuf[b + 1], outBuf[b]);
                                        break;
                                    case CueImage.TrackMode.Mode1Raw:  Buffer.BlockCopy(readBuf, src + 16, outBuf, dst, 2048); break;   // user data
                                    case CueImage.TrackMode.Mode2Raw:  Buffer.BlockCopy(readBuf, src + 16, outBuf, dst, 2336); break;   // sub-header onwards
                                    default:                           Buffer.BlockCopy(readBuf, src, outBuf, dst, block); break;       // already 2048 / 2336
                                }
                            }
                        }
                        long lba = piece.StartLba + i;
                        var data = outLen == outBuf.Length ? outBuf : outBuf.AsSpan(0, outLen).ToArray();
                        var cdb = new byte[] { 0x2A, 0, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0 };
                        var rep = RetryWhileBusyFor(() => drive.Write(cdb, data, 60), TimeSpan.FromMinutes(3), 50);
                        if (!rep.Ok)
                        {
                            r.Message = $"The write failed at sector {lba:N0} ({100.0 * done / total:0.0}%): {rep.Describe()}.{DiscProblemHint(rep)} {afterFailure}";
                            log?.Invoke("BIN/CUE: " + r.Message);
                            return false;
                        }
                        i += n;
                        done += n;

                        if (speedClock.Elapsed.TotalSeconds >= 1)
                        {
                            double now = (done - speedFrom) * 2352.0 / speedClock.Elapsed.TotalSeconds;
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
                                Remaining = speed > 0 ? TimeSpan.FromSeconds((total - done) * 2352.0 / speed) : TimeSpan.Zero,
                            });
                        }
                    }
                }
                finally { fs?.Dispose(); }
            }
            return true;
        }

        // Like ReadFully, but past "end" (the end of a .wav's audio) gives silence.
        private static void ReadPadded(FileStream fs, byte[] buf, int count, long end)
        {
            long left = Math.Max(0, end - fs.Position);
            int real = (int)Math.Min(count, left);
            if (real > 0) ReadFully(fs, buf, real);
            if (real < count) { Array.Clear(buf, real, count - real); fs.Position += count - real; }
        }

        // Reads every sector back. Returns a note about the music tracks for
        // the result message ("" when there are none).
        private static string VerifyCue(ScsiDrive drive, CueImage img, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? log)
        {
            long total = img.Pieces.Where(x => x.File is not null).Sum(x => x.Count), done = 0, bad = 0;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var window = new Queue<(TimeSpan T, long Done)>();
            void Report()
            {
                var now = clock.Elapsed;
                if (now - lastReport < TimeSpan.FromMilliseconds(250) && done < total) return;
                lastReport = now;
                window.Enqueue((now, done));
                while (window.Count > 1 && now - window.Peek().T > TimeSpan.FromSeconds(5)) window.Dequeue();
                var first = window.Peek();
                double secs = (now - first.T).TotalSeconds;
                double bps = secs > 0.2 ? (done - first.Done) * 2352.0 / secs : 0;
                progress?.Report(new Progress
                {
                    Phase = "Verifying",
                    Percent = total > 0 ? 100.0 * done / total : 100,
                    BytesPerSec = bps,
                    Remaining = bps > 0 ? TimeSpan.FromSeconds((total - done) * 2352.0 / bps) : TimeSpan.Zero,
                });
            }

            // ---- data tracks: exact ----
            const int N = 26;
            var disc = new byte[N * 2352];
            var file = new byte[N * 2352];
            foreach (var piece in img.Pieces.Where(x => x.File is not null && x.Mode != CueImage.TrackMode.Audio))
            {
                using var fs = new FileStream(piece.File!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                fs.Position = piece.FileOffset;
                for (long i = 0; i < piece.Count;)
                {
                    if (ct.IsCancellationRequested) { r.Cancelled = true; r.Verified = null; return ""; }
                    int n = (int)Math.Min(N, piece.Count - i);
                    long lba = piece.StartLba + i;
                    ReadFully(fs, file, n * piece.FileSectorSize);
                    var cdb = new byte[] { 0xBE, 0x00, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0xF8, 0, 0 };
                    var target = n == N ? disc : new byte[n * 2352];
                    var rep = RetryWhileBusy(() => drive.Read(cdb, target, 60), 100, 50);
                    for (int s = 0; s < n; s++)
                    {
                        if (!rep.Ok) { bad++; continue; }
                        int d = s * 2352, f = s * piece.FileSectorSize;
                        int fileData = piece.Mode is CueImage.TrackMode.Mode1Raw or CueImage.TrackMode.Mode2Raw ? f + 16 : f;   // where the stored part starts in the file
                        int len = piece.Mode switch
                        {
                            CueImage.TrackMode.Mode1Raw or CueImage.TrackMode.Mode1Cooked => 2048,
                            _ => (file[fileData + 2] & 0x20) != 0 ? 2332 : 2056,    // mode 2: form 2 (sub-header + 2324) or form 1 (sub-header + 2048)
                        };
                        if (!target.AsSpan(d + 16, len).SequenceEqual(file.AsSpan(fileData, len))) bad++;
                    }
                    i += n;
                    done += n;
                    Report();
                }
            }

            // ---- music tracks: find the offset, then compare ----
            string audioNote = "";
            var audio = img.Pieces.Where(x => x.File is not null && x.Mode == CueImage.TrackMode.Audio).ToList();
            if (audio.Count > 0)
            {
                long? shift = FindAudioShift(drive, img, audio, log);
                if (shift is null)
                {
                    audioNote = "; the music tracks couldn't be lined up for checking (too quiet), so only the data was compared";
                    done += audio.Sum(x => x.Count);
                    Report();
                }
                else
                {
                    long samples = shift.Value / 4;
                    log?.Invoke($"BIN/CUE: music reads back shifted by {samples:+0;-0;0} samples (the drive's offset)");
                    var expected = new byte[2352];
                    var open = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                    foreach (var piece in audio)
                    {
                        // The first and last sector of each stretch are skipped:
                        // with the shift, part of them comes from outside it.
                        for (long i = 0; i < piece.Count;)
                        {
                            if (ct.IsCancellationRequested) { r.Cancelled = true; r.Verified = null; return ""; }
                            int n = (int)Math.Min(N, piece.Count - i);
                            long lba = piece.StartLba + i;
                            var cdb = new byte[] { 0xBE, 0x04, (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba, 0, (byte)(n >> 8), (byte)n, 0x10, 0, 0 };
                            var target = n == N ? disc : new byte[n * 2352];
                            var rep = RetryWhileBusy(() => drive.Read(cdb, target, 60), 100, 50);
                            for (int s = 0; s < n; s++)
                            {
                                long at = lba + s;
                                if (at <= piece.StartLba || at >= piece.StartLba + piece.Count - 1) continue;
                                if (!rep.Ok) { bad++; continue; }
                                if (!ExpectedAudio(img, at * 2352 - shift.Value, expected, open)) continue;
                                if (!target.AsSpan(s * 2352, 2352).SequenceEqual(expected)) bad++;
                            }
                            i += n;
                            done += n;
                            Report();
                        }
                    }
                    }
                    finally { foreach (var f in open.Values) f.Dispose(); }
                    audioNote = samples == 0 ? "; music tracks match exactly" : $"; music tracks match (read back {Math.Abs(samples)} samples {(samples > 0 ? "later" : "earlier")}, the drive's normal offset)";
                }
            }

            r.MismatchedSectors = bad;
            r.Verified = bad == 0;
            log?.Invoke(bad == 0 ? $"BIN/CUE: verified ({clock.Elapsed:hh\\:mm\\:ss}){audioNote}" : $"BIN/CUE: VERIFY FAILED, {bad:N0} sector(s) differ or couldn't be read");
            return audioNote;
        }

        // How many bytes the disc's music is shifted by compared with the
        // .bin: a non-silent sector from the middle of a music track is looked
        // for in the disc around the same place, ±6 sectors.
        private static long? FindAudioShift(ScsiDrive drive, CueImage img, List<CueImage.Piece> audio, Action<string>? log)
        {
            const int Around = 6;
            var sector = new byte[2352];
            foreach (var piece in audio.Where(x => x.Count > Around * 4 + 50))
            {
                using var fs = new FileStream(piece.File!, FileMode.Open, FileAccess.Read, FileShare.Read);
                for (long tryAt = piece.Count / 2; tryAt < piece.Count - Around * 2; tryAt += 75)   // a second apart
                {
                    fs.Position = piece.FileOffset + tryAt * piece.FileSectorSize;
                    ReadFully(fs, sector, 2352);
                    if (piece.SwapAudio) for (int b = 0; b < 2352; b += 2) (sector[b], sector[b + 1]) = (sector[b + 1], sector[b]);
                    // Needs real sound, not silence, to line up on.
                    int loud = 0;
                    for (int b = 0; b < 2352; b += 2) if (Math.Abs((short)(sector[b] | (sector[b + 1] << 8))) > 64) loud++;
                    if (loud < 200) continue;

                    long lba = piece.StartLba + tryAt;
                    int n = Around * 2 + 1;
                    var around = new byte[n * 2352];
                    long from = lba - Around;
                    var cdb = new byte[] { 0xBE, 0x04, (byte)(from >> 24), (byte)(from >> 16), (byte)(from >> 8), (byte)from, 0, 0, (byte)n, 0x10, 0, 0 };
                    var rep = RetryWhileBusy(() => drive.Read(cdb, around, 60), 100, 50);
                    if (!rep.Ok) continue;
                    // Outward from "no shift": the smallest shift that fits wins.
                    // Music that repeats exactly (a test tone) also fits at
                    // whole numbers of its period; a drive's real offset is
                    // the small one (beta 3: a 440 Hz tone matched at 2205
                    // samples, 1/20 s, before the true 0).
                    int centre = Around * 2352;
                    for (int d = 0; d <= centre; d += 4)
                        foreach (int k in d == 0 ? new[] { centre } : new[] { centre + d, centre - d })
                            if (k >= 0 && k + 2352 <= around.Length && around.AsSpan(k, 2352).SequenceEqual(sector))
                                return k - centre;   // disc byte = file byte + shift
                    log?.Invoke($"BIN/CUE: couldn't line up the music at sector {lba:N0}");
                    return null;
                }
            }
            return null;
        }

        // The bytes the .bin has at a disc byte address (LBA × 2352), across
        // pieces. False if any of it isn't from a music file (gaps, data).
        // Files stay open in "open" while checking.
        private static bool ExpectedAudio(CueImage img, long address, byte[] dst, Dictionary<string, FileStream> open)
        {
            int got = 0;
            while (got < dst.Length)
            {
                long a = address + got;
                long lba = (long)Math.Floor(a / 2352.0);
                int within = (int)(a - lba * 2352);
                var piece = img.Pieces.FirstOrDefault(x => lba >= x.StartLba && lba < x.StartLba + x.Count);
                if (piece is null || piece.File is null || piece.Mode != CueImage.TrackMode.Audio) return false;
                int take = Math.Min(dst.Length - got, 2352 - within);
                if (!open.TryGetValue(piece.File, out var fs))
                {
                    fs = new FileStream(piece.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                    open[piece.File] = fs;
                }
                fs.Position = piece.FileOffset + (lba - piece.StartLba) * 2352 + within;
                int real = (int)Math.Max(0, Math.Min(take, piece.FileDataEnd - fs.Position));
                int read = 0;
                while (read < real)
                {
                    int k = fs.Read(dst, got + read, real - read);
                    if (k <= 0) return false;
                    read += k;
                }
                if (real < take) Array.Clear(dst, got + real, take - real);   // the silence after a .wav's audio
                // The shift is whole samples, so "within" is even: swap byte pairs.
                if (piece.SwapAudio)
                    for (int b = got; b + 1 < got + take; b += 2) (dst[b], dst[b + 1]) = (dst[b + 1], dst[b]);
                got += take;
            }
            return true;
        }
    }
}
