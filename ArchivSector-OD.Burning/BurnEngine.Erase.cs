using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ArchivSector_OD.Burning
{
    // Erase (v4.0 week 3): wipes a rewritable disc (CD-RW, DVD-RW) with
    // Windows' burning engine (IMAPI2's MsftDiscFormat2Erase), so it can be
    // burned again.
    //   * Quick erase: clears the disc's table of contents. About a minute.
    //   * Full erase: writes over the whole disc. Slower (CD-RW about
    //     10 minutes, DVD-RW up to an hour), for a disc that misbehaves
    //     or that's being passed on.
    // Windows' engine can't erase DVD+RW or BD-RE (they're written over in
    // place), so those are erased by ArchivSector-OD itself (v4.0 beta 2,
    // EraseOverwritable below):
    //   * Quick erase: zeros over the start and the end of the disc, where
    //     its file system lives (ISO 9660 and UDF), so the disc reads as
    //     blank. A few seconds.
    //   * Full erase: the same, then zeros over every sector. BD-RE about
    //     45 minutes at 2x, DVD+RW about 15 minutes at 4x. It can be stopped
    //     safely: the disc is already blank after the first step.
    // DVD-RW (v4.0 beta 3) is erased by ArchivSector-OD too (EraseDvdRw),
    // with the drive's own BLANK command, so it shows real progress:
    //   * Quick erase: "minimal blank", about a minute. The own writer
    //     writes a quick-erased DVD-RW incrementally, so that's enough.
    //   * Full erase: blanks every sector, about 15 minutes at 4x.
    //   A DVD-RW in "overwrite" mode comes back in the normal (sequential) mode.
    // A CD-RW (Windows' engine) or DVD-RW erase can't be stopped once it starts.
    public static partial class BurnEngine
    {
        public sealed class EraseInfo
        {
            public bool CanErase;
            public bool Direct;              // erased by ArchivSector-OD itself (DVD+RW, BD-RE): can be stopped
            public bool DvdRw;               // a DVD-RW, blanked by ArchivSector-OD with the drive's BLANK command (can't be stopped)
            public string MediaType = "";
            public string? Problem;          // why it can't be erased here
        }

        public sealed class EraseProgress
        {
            public double Percent = -1;      // -1 = unknown (the drive gives no estimate)
            public TimeSpan Elapsed;
            public TimeSpan Remaining;
        }

        public sealed class EraseResult
        {
            public bool Ok;
            public string Message = "";
            public TimeSpan Time;
        }

        // IMAPI2's erase-progress event (DDiscFormat2EraseEvents.Update).
        [ComImport, Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface DDiscFormat2EraseEvents
        {
            [DispId(0x200)]
            void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, int elapsedSeconds, int estimatedTotalSeconds);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class EraseSink : DDiscFormat2EraseEvents
        {
            public Action<int, int>? OnUpdate;
            public void Update(object sender, int elapsedSeconds, int estimatedTotalSeconds)
            {
                try { OnUpdate?.Invoke(elapsedSeconds, estimatedTotalSeconds); } catch { }
            }
        }

        /// <summary>Can the disc in this drive be erased with Windows' engine?</summary>
        public static EraseInfo QueryErase(string driveLetter)
        {
            var info = new EraseInfo();
            var letter = Norm(driveLetter);
            if (IsBusy(letter)) { info.Problem = "The drive is busy."; return info; }
            try
            {
                RunSta(() =>
                {
                    var id = RecorderIdFor(letter);
                    if (id is null) { info.Problem = "This drive can't write discs, or it couldn't be found."; return; }
                    object? recObj = null, dataObj = null, eraseObj = null;
                    try
                    {
                        recObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!)!;
                        dynamic rec = recObj;
                        rec.InitializeDiscRecorder(id);

                        // The disc's type, from the data-writing side.
                        dataObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")!)!;
                        dynamic data = dataObj;
                        int type = 0;
                        try { data.Recorder = recObj; type = (int)data.CurrentPhysicalMediaType; } catch { }
                        // A closed CD-RW/DVD-RW isn't "writable" to the data side,
                        // so ask the drive's current profiles instead.
                        if (type == 0)
                        {
                            try
                            {
                                var profiles = new HashSet<int>();
                                foreach (var o in (object[])rec.CurrentProfiles) profiles.Add(Convert.ToInt32(o));
                                type = profiles.Contains(0x43) ? 19 : profiles.Contains(0x0A) ? 3
                                    : profiles.Contains(0x13) || profiles.Contains(0x14) ? 10
                                    : profiles.Contains(0x1A) ? 7 : profiles.Contains(0x2A) ? 13 : 0;
                            }
                            catch { }
                        }
                        info.MediaType = WritableLabel(type) ?? "";

                        // DVD+RW and BD-RE: erased by ArchivSector-OD itself.
                        if (type is 7 or 19) { info.CanErase = true; info.Direct = true; return; }
                        // DVD-RW: blanked by ArchivSector-OD (Windows' engine is the fallback).
                        if (type == 10) { info.CanErase = true; info.DvdRw = true; return; }

                        eraseObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Erase")!)!;
                        dynamic erase = eraseObj;
                        bool recorderOk = false, mediaOk = false;
                        try { recorderOk = (bool)erase.IsRecorderSupported(recObj); } catch { }
                        if (recorderOk) { try { mediaOk = (bool)erase.IsCurrentMediaSupported(recObj); } catch { } }

                        if (mediaOk) { info.CanErase = true; return; }
                        info.Problem = type switch
                        {
                            13 => $"A {info.MediaType} can't be erased here yet. It can be written over without erasing.",
                            2 or 6 or 8 or 9 or 11 or 18 => $"A {info.MediaType} is write-once, so it can't be erased.",
                            0 => "There's no disc in the drive (or the drive can't read it).",
                            _ => "This disc can't be erased with Windows' burning engine.",
                        };
                    }
                    finally
                    {
                        Release(eraseObj);
                        Release(dataObj);
                        Release(recObj);
                    }
                });
            }
            catch (Exception ex) { info.Problem = "The drive couldn't be asked: " + ex.Message; }
            return info;
        }

        /// <summary>
        /// Erases the rewritable disc in a drive (quick or full). Marks the
        /// drive busy so nothing else asks it anything meanwhile. Can't be
        /// cancelled once it starts.
        /// </summary>
        public static Task<EraseResult> EraseAsync(string driveLetter, bool full, IProgress<EraseProgress>? progress, Action<string>? log = null,
            CancellationToken ct = default)
        {
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new EraseResult { Message = "This drive is busy burning or erasing." });
            return Task.Factory.StartNew(() =>
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                var r = new EraseResult();
                var clock = Stopwatch.StartNew();
                try
                {
                    log?.Invoke($"Erase: {(full ? "full" : "quick")} erase of the disc in {letter}: started");
                    if (!EraseOverwritable(letter, full, progress, clock, ct, r, log) && !EraseDvdRw(letter, full, progress, clock, r, log))
                        RunSta(() => Erase(letter, full, progress, clock, r));
                }
                catch (Exception ex)
                {
                    r.Ok = false;
                    r.Message = "The erase failed: " + DescribeImapiError(ex);
                }
                finally
                {
                    clock.Stop();
                    r.Time = clock.Elapsed;
                    SetThreadExecutionState(ES_CONTINUOUS);
                    Busy.TryRemove(letter, out _);
                    // Give the drive a moment to re-read the now blank disc
                    // before the bays ask it anything.
                    PausePolling(letter, TimeSpan.FromSeconds(5));
                }
                log?.Invoke($"Erase: {r.Message} ({r.Time:hh\\:mm\\:ss})");
                return r;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        // ---- DVD+RW and BD-RE: erased by ArchivSector-OD itself ----
        // Returns false when the disc isn't one of these (Windows' engine then
        // erases it); true when it handled the disc (r says how it went).
        private const int QuickEraseSectors = 1024;   // 2 MB at each end: every ISO 9660 / UDF structure that says "files are here"

        private static bool EraseOverwritable(string letter, bool full, IProgress<EraseProgress>? progress, Stopwatch clock, CancellationToken ct, EraseResult r, Action<string>? log)
        {
            using var drive = ScsiDrive.Open(letter, out _);
            if (drive is null) return false;
            if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromSeconds(30))) return false;
            var cfg = new byte[8];
            var rep = drive.Read(new byte[] { 0x46, 0x02, 0, 0, 0, 0, 0, 0, 8, 0 }, cfg, 10);
            int profile = rep.Ok ? (cfg[6] << 8) | cfg[7] : 0;
            if (profile is not (ProfileBdRe or ProfileDvdPlusRw)) return false;
            string name = profile == ProfileBdRe ? "BD-RE" : "DVD+RW";

            // Never formatted: nothing has ever been written, so it's blank already.
            bool stopBackgroundFormat = false;
            if (profile == ProfileBdRe)
            {
                var fc = new byte[12];
                rep = drive.Read(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0, 12, 0 }, fc, 10);
                if (rep.Ok && (fc[8] & 3) != 2)
                {
                    r.Ok = true;
                    r.Message = "This BD-RE isn't formatted yet, so it's already blank and ready to burn.";
                    return true;
                }
            }
            else
            {
                var di = new byte[34];
                rep = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
                int bg = rep.Ok ? di[7] & 3 : 3;
                if (bg == 0)
                {
                    r.Ok = true;
                    r.Message = "This DVD+RW has never been written, so it's already blank and ready to burn.";
                    return true;
                }
                stopBackgroundFormat = bg is 1 or 2;
            }

            var cap = new byte[8];
            rep = drive.Read(new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, cap, 10);
            long sectors = rep.Ok ? BE32(cap, 0) + 1 : 0;
            if (sectors < QuickEraseSectors * 2) { r.Message = $"The drive didn't say how big the {name} is" + (rep.Ok ? "." : ": " + rep.Describe()); return true; }
            log?.Invoke($"Erase: {name}, {sectors:N0} sectors; {(full ? "zeros over the whole disc" : "zeros over the first and last 2 MB")}");

            drive.LockAndDismount();
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);                                   // keep the tray shut
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);       // fastest speed
            try
            {
                // 1. The start and the end: after this the disc reads as blank.
                long total = full ? sectors + QuickEraseSectors * 2 : QuickEraseSectors * 2;
                long done = 0;
                if (!ZeroSectors(drive, 0, QuickEraseSectors, ref done, total, progress, clock, CancellationToken.None, r, name)) return true;
                if (!ZeroSectors(drive, sectors - QuickEraseSectors, QuickEraseSectors, ref done, total, progress, clock, CancellationToken.None, r, name)) return true;

                // 2. Full erase: every sector. Stopping here is harmless.
                bool stopped = false;
                if (full && !ZeroSectors(drive, 0, sectors, ref done, total, progress, clock, ct, r, name))
                {
                    if (!ct.IsCancellationRequested) return true;   // a write failed: r says where
                    stopped = true;
                }

                drive.Command(new byte[] { 0x35, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 600);                  // SYNCHRONIZE CACHE
                if (stopBackgroundFormat) StopDvdPlusRwFormat(drive, null, log);
                WaitReady(drive, CancellationToken.None, TimeSpan.FromMinutes(5));
                r.Ok = true;
                r.Message = stopped
                    ? $"Full erase stopped at {100.0 * done / total:0}%. The {name} is blank and ready to burn (its file list was wiped first)."
                    : full ? $"Full erase finished: every sector of the {name} was overwritten. It's blank and ready to burn."
                    : $"Quick erase finished. The {name} is blank and ready to burn.";
                return true;
            }
            finally
            {
                drive.Command(new byte[] { 0x1E, 0, 0, 0, 0, 0 }, 10);
                drive.Unlock();
            }
        }

        // ---- DVD-RW: the drive's BLANK command ----
        // Returns false when the disc isn't a DVD-RW, or the drive refused
        // BLANK before starting (Windows' engine then erases it); true when it
        // handled the disc (r says how it went).
        private static bool EraseDvdRw(string letter, bool full, IProgress<EraseProgress>? progress, Stopwatch clock, EraseResult r, Action<string>? log)
        {
            using var drive = ScsiDrive.Open(letter, out _);
            if (drive is null) return false;
            if (!WaitReady(drive, CancellationToken.None, TimeSpan.FromSeconds(30))) return false;
            var cfg = new byte[8];
            var rep = drive.Read(new byte[] { 0x46, 0x02, 0, 0, 0, 0, 0, 0, 8, 0 }, cfg, 10);
            int profile = rep.Ok ? (cfg[6] << 8) | cfg[7] : 0;
            if (profile is not (ProfileDvdRwSequential or ProfileDvdRwOverwrite)) return false;
            string mode = profile == ProfileDvdRwOverwrite ? "overwrite mode" : "sequential mode";

            drive.LockAndDismount();
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);                                   // keep the tray shut
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);       // fastest speed
            try
            {
                // BLANK, answering at once (IMMED); type 0 = the whole disc, 1 = minimal.
                byte type = (byte)(full ? 0x00 : 0x01);
                rep = drive.Command(new byte[] { 0xA1, (byte)(0x10 | type), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 120);
                if (!rep.Ok && !rep.IsTryAgain)
                {
                    log?.Invoke($"Erase: the drive refused BLANK on the DVD-RW ({mode}): {rep.Describe()}; trying Windows' engine");
                    return false;
                }
                log?.Invoke($"Erase: DVD-RW ({mode}), {(full ? "full blank (every sector)" : "minimal blank")} with the drive's BLANK command");
                progress?.Report(new EraseProgress { Elapsed = clock.Elapsed });

                // Wait, with the drive's own progress (REQUEST SENSE). Up to 90 minutes.
                // The log gets a line every 10%, and one if the drive stops
                // answering for a while, so a stalled erase shows where.
                var start = clock.Elapsed;
                double lastPct = -1;
                int lastTenth = -1;
                while (clock.Elapsed - start < TimeSpan.FromMinutes(90))
                {
                    Thread.Sleep(1000);
                    var asked = clock.Elapsed;
                    var tur = drive.Command(new byte[] { 0x00, 0, 0, 0, 0, 0 }, 10);
                    var took = clock.Elapsed - asked;
                    if (took > TimeSpan.FromSeconds(5))
                        log?.Invoke($"Erase: the drive took {took.TotalSeconds:0} s to answer (at {Math.Max(0, lastPct):0}%, {clock.Elapsed - start:mm\\:ss} in)");
                    if (tur.Ok)
                    {
                        // The drive says it's done: check the disc really is blank
                        // now. An erase the drive gave up on part-way (interrupted)
                        // can also end like this, with the old contents still there.
                        var di = new byte[34];
                        var dr = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
                        bool blank = dr.Ok && (di[2] & 3) == 0;
                        log?.Invoke($"Erase: the drive says it's done after {clock.Elapsed - start:mm\\:ss} (last progress {Math.Max(0, lastPct):0}%); the disc reads as {(!dr.Ok ? "unknown (" + dr.Describe() + ")" : blank ? "blank" : "NOT blank")}");
                        if (dr.Ok && !blank)
                        {
                            r.Message = "The drive stopped before the DVD-RW was blank, so the old contents are still on it. Erase it again,"
                                      + " and don't open Explorer or a file window (like choosing an ISO) while it erases: Windows checking the drive can stop the erase.";
                            return true;
                        }
                        r.Ok = true;
                        r.Message = full ? "Full erase finished: every sector of the DVD-RW was blanked. It's blank and ready to burn."
                                         : "Quick erase finished. The DVD-RW is blank and ready to burn.";
                        return true;
                    }
                    if (!tur.IsTryAgain && tur.SenseKey != 6)
                    {
                        r.Message = "The erase failed: " + tur.Describe() + ". Try a full erase, or another drive.";
                        return true;
                    }
                    var sense = new byte[18];
                    var rs = drive.Read(new byte[] { 0x03, 0, 0, 0, 18, 0 }, sense, 10);
                    var elapsed = clock.Elapsed - start;
                    if (rs.Ok && (sense[15] & 0x80) != 0)                            // progress is valid
                    {
                        double pct = ((sense[16] << 8) | sense[17]) * 100.0 / 65536;
                        lastPct = pct;
                        if ((int)(pct / 10) > lastTenth)
                        {
                            lastTenth = (int)(pct / 10);
                            if (lastTenth > 0) log?.Invoke($"Erase: {lastTenth * 10}% after {clock.Elapsed - start:mm\\:ss}");
                        }
                        progress?.Report(new EraseProgress
                        {
                            Elapsed = clock.Elapsed,
                            Percent = Math.Min(99, pct),
                            Remaining = pct > 0.5 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - pct) / pct) : TimeSpan.Zero,
                        });
                    }
                    else progress?.Report(new EraseProgress { Elapsed = clock.Elapsed });
                }
                r.Message = "The erase didn't finish within 90 minutes.";
                return true;
            }
            finally
            {
                drive.Command(new byte[] { 0x1E, 0, 0, 0, 0, 0 }, 10);
                drive.Unlock();
            }
        }

        // Zeros over count sectors from lba, 64 KB at a time. False if a write
        // failed (r.Message says where) or ct asked to stop.
        private static bool ZeroSectors(ScsiDrive drive, long lba, long count, ref long done, long total, IProgress<EraseProgress>? progress,
            Stopwatch clock, CancellationToken ct, EraseResult r, string name)
        {
            var zeros = new byte[DirectChunkSectors * Sector];
            var lastReport = TimeSpan.Zero;
            var start = clock.Elapsed;
            long startDone = done;
            for (long at = lba, end = lba + count; at < end;)
            {
                if (ct.IsCancellationRequested) return false;
                int n = (int)Math.Min(DirectChunkSectors, end - at);
                var data = n == DirectChunkSectors ? zeros : new byte[n * Sector];
                long a = at;
                var cdb = new byte[] { 0x2A, 0, (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a, 0, (byte)(n >> 8), (byte)n, 0 };
                var rep = RetryWhileBusyFor(() => drive.Write(cdb, data, 60), TimeSpan.FromMinutes(3), 50);
                if (!rep.Ok)
                {
                    r.Message = $"The erase failed at sector {a:N0}: {rep.Describe()}. The {name} can be erased again.";
                    return false;
                }
                at += n;
                done += n;
                if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(500))
                {
                    lastReport = clock.Elapsed;
                    double secs = (clock.Elapsed - start).TotalSeconds;
                    double rate = secs > 1 ? (done - startDone) / secs : 0;            // sectors per second
                    progress?.Report(new EraseProgress
                    {
                        Elapsed = clock.Elapsed,
                        Percent = Math.Min(99, 100.0 * done / total),
                        Remaining = rate > 0 ? TimeSpan.FromSeconds((total - done) / rate) : TimeSpan.Zero,
                    });
                }
            }
            return true;
        }

        private static void Erase(string letter, bool full, IProgress<EraseProgress>? progress, Stopwatch clock, EraseResult r)
        {
            var id = RecorderIdFor(letter);
            if (id is null) { r.Message = "This drive can't write discs, or it couldn't be found."; return; }
            object? recObj = null, eraseObj = null;
            IConnectionPoint? cp = null;
            int cookie = 0;
            try
            {
                recObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!)!;
                dynamic rec = recObj;
                rec.InitializeDiscRecorder(id);
                eraseObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Erase")!)!;
                dynamic erase = eraseObj;
                if (!(bool)erase.IsRecorderSupported(recObj)) { r.Message = "This drive can't erase discs."; return; }
                if (!(bool)erase.IsCurrentMediaSupported(recObj)) { r.Message = "The disc in the drive can't be erased with Windows' burning engine."; return; }
                erase.Recorder = recObj;
                erase.ClientName = "ArchivSector-OD";
                erase.FullErase = full;

                // Progress: IMAPI2 says how long it has been and how long it
                // expects in total. If hooking the event fails, the erase
                // still runs, just without a percentage.
                var sink = new EraseSink
                {
                    OnUpdate = (elapsed, total) =>
                    {
                        progress?.Report(new EraseProgress
                        {
                            Elapsed = TimeSpan.FromSeconds(elapsed),
                            Percent = total > 0 ? Math.Clamp(100.0 * elapsed / total, 0, 99) : -1,
                            Remaining = total > elapsed ? TimeSpan.FromSeconds(total - elapsed) : TimeSpan.Zero,
                        });
                    },
                };
                try
                {
                    var cpc = (IConnectionPointContainer)eraseObj;
                    Guid iid = typeof(DDiscFormat2EraseEvents).GUID;
                    cpc.FindConnectionPoint(ref iid, out cp);
                    // Windows may hand back no connection point: then there's
                    // just no progress percentage.
                    if (cp is not null) cp.Advise(sink, out cookie);
                }
                catch { cp = null; }

                progress?.Report(new EraseProgress { Elapsed = clock.Elapsed });
                erase.EraseMedia();
                r.Ok = true;
                r.Message = full ? "Full erase finished. The disc is blank and ready to burn." : "Quick erase finished. The disc is blank and ready to burn.";
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Message = "The erase failed: " + DescribeImapiError(ex);
            }
            finally
            {
                if (cp is not null) { try { cp.Unadvise(cookie); } catch { } Release(cp); }
                Release(eraseObj);
                Release(recObj);
            }
        }
    }
}
