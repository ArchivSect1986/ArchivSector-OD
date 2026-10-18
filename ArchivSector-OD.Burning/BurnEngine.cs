using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace ArchivSector_OD.Burning
{
    // The burning engine (moved here from the app's BurnService in v4.0
    // week 1, so the drive bays, the Burn tab and a future standalone
    // burner all share one engine).
    //
    // Burns ISO images to CD/DVD/Blu-ray with Windows' built-in disc
    // burning engine (IMAPI2) -- nothing extra to install -- or with
    // ImgBurn when the caller passes its path. No screens and nothing from
    // the app here: the app's BurnService wraps this and adds History,
    // logging, "drive busy" tracking and the Recycle Bin option.
    //
    //   * QueryBlank: is there a blank, writable disc in this drive, what
    //     kind and how much space? (Used by the drive bays.)
    //   * BurnAsync: writes an .iso to the blank disc with live progress,
    //     closes the disc, then (optionally) reads the whole disc back and
    //     compares it with the .iso byte for byte, so you know the burn is
    //     perfect before it goes on the shelf.
    //
    // IMAPI2 objects are used on their own STA thread. Cancelling during
    // the write leaves a partly written disc that can't be reused (that's
    // how write-once discs work); the screen warns about this first.
    public static class BurnEngine
    {
        private const int Sector = 2048;

        public sealed class BlankInfo
        {
            public string MediaType = "";   // "BD-R", "DVD+R DL", ...
            public long FreeBytes;          // what Windows' burning engine says it can write
            public long TotalBytes;         // the disc's full size (can be more than FreeBytes on BD-R)
        }

        public sealed class Progress
        {
            public string Phase = "";       // "Preparing", "Writing", "Finalizing", "Verifying"
            public double Percent;          // 0-100 within the phase, -1 = unknown
            public double BytesPerSec;
            public TimeSpan Remaining;
        }

        public sealed class Result
        {
            public bool Burned;
            public bool Cancelled;
            public bool? Verified;          // null = not verified
            public long MismatchedSectors;
            public bool VerifiedByImgBurn;   // ImgBurn did the check (its "Exact Match")
            public bool IsoDeleted;          // the ISO went to the Recycle Bin afterwards
            public string Message = "";
            public TimeSpan BurnTime;
            public TimeSpan VerifyTime;
        }

        // Drives currently burning or verifying (the bay polling leaves them alone).
        private static readonly ConcurrentDictionary<string, bool> Busy = new(StringComparer.OrdinalIgnoreCase);
        public static bool IsBusy(string driveLetter) => Busy.ContainsKey(Norm(driveLetter));
        private static string Norm(string l) => l.TrimEnd(':', '\\').ToUpperInvariant();

        // Drives the bay polling should leave alone for a moment (the tray
        // is being opened -- a blank-disc check landing at the same instant
        // can make the drive ignore the eject).
        private static readonly ConcurrentDictionary<string, DateTime> PausedUntil = new(StringComparer.OrdinalIgnoreCase);
        public static void PausePolling(string driveLetter, TimeSpan howLong) => PausedUntil[Norm(driveLetter)] = DateTime.Now + howLong;
        public static bool IsPaused(string driveLetter) =>
            PausedUntil.TryGetValue(Norm(driveLetter), out var until) && DateTime.Now < until;

        // Sizes in the same units as Windows Explorer (1 GB = 1024^3 bytes).
        private static string Fmt(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" : $"{b / 1024.0:0} KB";

        // IMAPI_MEDIA_PHYSICAL_TYPE -> label, for writable types only.
        private static string? WritableLabel(int t) => t switch
        {
            2 => "CD-R", 3 => "CD-RW",
            6 => "DVD+R", 7 => "DVD+RW", 8 => "DVD+R DL",
            9 => "DVD-R", 10 => "DVD-RW", 11 => "DVD-R DL", 13 => "DVD+RW DL",
            18 => "BD-R", 19 => "BD-RE",
            _ => null,
        };

        // ------------------------------------------------------------------
        // Blank disc detection
        // ------------------------------------------------------------------
        // Returns details if the drive holds a blank, writable disc; null
        // otherwise (no disc, a disc with data, not a burner, or busy).
        public static BlankInfo? QueryBlank(string driveLetter)
        {
            if (IsBusy(driveLetter) || IsPaused(driveLetter)) return null;
            BlankInfo? info = null;
            try
            {
                RunSta(() =>
                {
                    WithRecorder(driveLetter, (rec, fmt) =>
                    {
                        if (!(bool)fmt.IsCurrentMediaSupported(rec)) return;
                        int type = (int)fmt.CurrentPhysicalMediaType;
                        var label = WritableLabel(type);
                        if (label is null) return;
                        if (!(bool)fmt.MediaHeuristicallyBlank) return;
                        long free = Convert.ToInt64(fmt.FreeSectorsOnMedia) * Sector;
                        if (free <= 0) return;
                        long total = free;
                        try { total = Math.Max(free, Convert.ToInt64(fmt.TotalSectorsOnMedia) * Sector); } catch { }
                        info = new BlankInfo { MediaType = label, FreeBytes = free, TotalBytes = total };
                    });
                });
            }
            catch { /* not a burner, or the drive is busy */ }
            return info;
        }

        // Finds the IMAPI2 recorder for a drive letter and gives it, with a
        // data-format object attached, to work(). Releases everything after.
        // Drive letter -> IMAPI2 recorder ID. Worked out once by asking every
        // burner for its drive letters, then reused, so checking one drive
        // never sends commands to another drive that might be busy ripping.
        private static readonly ConcurrentDictionary<string, string> RecorderIds = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime lastEnumeration = DateTime.MinValue;

        private static string? RecorderIdFor(string letter)
        {
            if (RecorderIds.TryGetValue(letter, out var known)) return known;
            // Re-scan at most once a minute (a drive may have been plugged in).
            if (DateTime.Now - lastEnumeration < TimeSpan.FromMinutes(1)) return null;
            lastEnumeration = DateTime.Now;

            object? masterObj = null;
            try
            {
                masterObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscMaster2")
                    ?? throw new InvalidOperationException("Windows' disc burning engine (IMAPI2) isn't available."))!;
                dynamic master = masterObj;
                int count = (int)master.Count;
                for (int i = 0; i < count; i++)
                {
                    string id = (string)master[i];
                    if (RecorderIds.Values.Contains(id)) continue;
                    object? recObj = null;
                    try
                    {
                        recObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!)!;
                        dynamic rec = recObj;
                        rec.InitializeDiscRecorder(id);
                        foreach (object p in (object[])rec.VolumePathNames)
                            if (p is string s && s.Length >= 1) RecorderIds[s.TrimEnd('\\', ':').ToUpperInvariant()] = id;
                    }
                    catch { }
                    finally { Release(recObj); }
                }
            }
            catch { }
            finally { Release(masterObj); }
            return RecorderIds.TryGetValue(letter, out var found) ? found : null;
        }

        // Gives work() the IMAPI2 recorder for a drive letter, with a
        // data-format object attached. Releases everything after.
        private static void WithRecorder(string driveLetter, Action<dynamic, dynamic> work)
        {
            var letter = Norm(driveLetter);
            var id = RecorderIdFor(letter);
            if (id is null) return;
            object? recObj = null, fmtObj = null;
            try
            {
                recObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!)!;
                dynamic rec = recObj;
                try { rec.InitializeDiscRecorder(id); }
                catch { RecorderIds.TryRemove(letter, out _); return; }   // drive gone: forget it

                fmtObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Data")!)!;
                dynamic fmt = fmtObj;
                if (!(bool)fmt.IsRecorderSupported(recObj)) return;
                fmt.Recorder = recObj;
                work(recObj, fmt);
            }
            finally
            {
                Release(fmtObj);
                Release(recObj);
            }
        }

        // ------------------------------------------------------------------
        // Burning
        // ------------------------------------------------------------------
        // Keeps the PC awake while burning (sleep in the middle of a burn ruins the disc).
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IStream? template, out IStream stream);

        // IMAPI2's write-progress event (DDiscFormat2DataEvents.Update).
        // Both must be public: .NET only exposes public types to COM, and
        // IMAPI2 has to see the sink as this event interface to connect it.
        [ComImport, Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        public interface DDiscFormat2DataEvents
        {
            [DispId(0x200)]
            void Update([In, MarshalAs(UnmanagedType.IDispatch)] object sender, [In, MarshalAs(UnmanagedType.IDispatch)] object progress);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class WriteSink : DDiscFormat2DataEvents
        {
            public Action<object, object>? OnUpdate;
            public void Update(object sender, object progress)
            {
                try { OnUpdate?.Invoke(sender, progress); } catch { }
            }
        }

        /// <summary>
        /// Burns an ISO to the blank disc in a drive. <paramref name="imgBurnPath"/>
        /// = burn with ImgBurn (its own verify is used); null = Windows' engine.
        /// <paramref name="log"/> receives lines worth keeping in the app's log.
        /// A drive that's already burning is refused.
        /// </summary>
        public static Task<Result> BurnAsync(string driveLetter, string isoPath, bool verify,
            IProgress<Progress>? progress, CancellationToken ct, string? imgBurnPath = null, Action<string>? log = null)
        {
            // Marked busy right away (before the task starts), so the bay
            // polling stops asking the drive anything before the burn begins.
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new Result { Message = "This drive is already burning." });
            bool useImgBurn = imgBurnPath is not null;
            return Task.Factory.StartNew(() =>
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                var r = new Result();
                try
                {
                    var burnClock = Stopwatch.StartNew();
                    if (useImgBurn) BurnWithImgBurn(imgBurnPath!, letter, isoPath, verify, progress, ct, r, log);
                    else RunSta(() => Burn(letter, isoPath, progress, ct, r));
                    r.BurnTime = burnClock.Elapsed;

                    if (useImgBurn)
                    {
                        // ImgBurn did its own verify (see BurnWithImgBurn).
                    }
                    else if (r.Burned && verify && !ct.IsCancellationRequested && !File.Exists(isoPath))
                    {
                        // Something removed the ISO during the burn (ImgBurn
                        // can be set to delete an image once it's burned).
                        r.Message = $"Burned, but the ISO is no longer at {isoPath}, so the disc couldn't be compared with it.";
                    }
                    else if (r.Burned && verify && !ct.IsCancellationRequested)
                    {
                        var verifyClock = Stopwatch.StartNew();
                        VerifyDisc(letter, isoPath, progress, ct, r);
                        r.VerifyTime = verifyClock.Elapsed;
                    }

                    r.Message = !r.Burned ? (r.Message.Length > 0 ? r.Message : "The burn didn't complete.")
                        : r.Verified == true ? "Burned and verified: every byte on the disc matches the ISO" + (r.VerifiedByImgBurn ? " (checked by ImgBurn)." : ".")
                        : r.Verified == false ? (r.Message.Length > 0 ? r.Message : $"Burned, but {r.MismatchedSectors:N0} sector(s) on the disc don't match the ISO. Don't rely on this disc.")
                        : r.Cancelled ? "Burned. The check was stopped, so the disc wasn't fully verified."
                        : r.Message.Length > 0 ? r.Message
                        : "Burned (not verified).";
                }
                catch (Exception ex)
                {
                    // Only call it a failed burn if the disc wasn't written.
                    r.Message = r.Burned
                        ? $"Burned, but checking the disc failed: {ex.Message}"
                        : $"The burn failed: {ex.Message}";
                    if (r.Burned) r.Verified = null;
                }
                finally
                {
                    Busy.TryRemove(letter, out _);
                    SetThreadExecutionState(ES_CONTINUOUS);
                }
                return r;
            }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void Burn(string letter, string isoPath, IProgress<Progress>? progress, CancellationToken ct, Result r)
        {
            long isoBytes = new FileInfo(isoPath).Length;
            if (isoBytes == 0 || isoBytes % Sector != 0)
            {
                r.Message = "This file isn't a disc image made of whole 2 KB sectors, so it can't be burned as-is.";
                return;
            }
            progress?.Report(new Progress { Phase = "Preparing", Percent = -1 });

            WithRecorder(letter, (rec, fmt) =>
            {
                if (!(bool)fmt.IsCurrentMediaSupported(rec)) { r.Message = "There's no blank disc in the drive that it can write to."; return; }
                if (!(bool)fmt.MediaHeuristicallyBlank) { r.Message = "The disc in the drive isn't blank."; return; }
                long free = Convert.ToInt64(fmt.FreeSectorsOnMedia) * Sector;
                if (isoBytes > free)
                {
                    r.Message = $"The ISO ({Fmt(isoBytes)}) is bigger than the free space on this disc ({Fmt(free)}).";
                    return;
                }

                fmt.ClientName = "ArchivSector-OD";
                fmt.ForceMediaToBeClosed = true;

                SHCreateStreamOnFileEx(isoPath, 0x00000000 /* STGM_READ */ | 0x00000020 /* STGM_SHARE_DENY_WRITE */, 0, false, null, out IStream stream);

                // Live progress through IMAPI2's Update event. If hooking
                // it fails, the burn still runs, just without a percentage.
                IConnectionPoint? cp = null;
                int cookie = 0;
                var sink = new WriteSink();
                var lastTime = Stopwatch.StartNew();
                long lastLba = -1;
                double speed = 0;
                sink.OnUpdate = (sender, args) =>
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { ((dynamic)sender).CancelWrite(); } catch { }
                        r.Cancelled = true;
                    }
                    dynamic a = args;
                    // IMAPI_FORMAT2_DATA_WRITE_ACTION is 0x0C000000 + step:
                    // 0 validating, 1 formatting, 2 initializing, 3 calibrating,
                    // 4 writing, 5 finalizing, 6 completed, 7 verifying
                    int action = (int)a.CurrentAction & 0xFFFF;
                    if (action == 4)
                    {
                        long start = Convert.ToInt64(a.StartLba);
                        long count = Math.Max(1, Convert.ToInt64(a.SectorCount));
                        long written = Convert.ToInt64(a.LastWrittenLba) - start;
                        double secs = lastTime.Elapsed.TotalSeconds;
                        if (lastLba >= 0 && secs >= 1)
                        {
                            double now = (written - lastLba) * (double)Sector / secs;
                            speed = speed <= 0 ? now : speed * 0.7 + now * 0.3;
                            lastTime.Restart();
                            lastLba = written;
                        }
                        else if (lastLba < 0) { lastLba = written; lastTime.Restart(); }
                        progress?.Report(new Progress
                        {
                            Phase = "Writing",
                            Percent = Math.Clamp(100.0 * written / count, 0, 100),
                            BytesPerSec = speed,
                            Remaining = TimeSpan.FromSeconds(Math.Max(0, Convert.ToInt64(a.RemainingTime))),
                        });
                    }
                    else if (action == 5)
                        progress?.Report(new Progress { Phase = "Finalizing", Percent = -1 });
                    else if (action < 4)
                        progress?.Report(new Progress { Phase = "Preparing", Percent = -1 });
                };
                try
                {
                    var cpc = (IConnectionPointContainer)(object)fmt;
                    Guid iid = typeof(DDiscFormat2DataEvents).GUID;
                    cpc.FindConnectionPoint(ref iid, out cp);
                    cp.Advise(sink, out cookie);
                }
                catch { cp = null; }

                try
                {
                    fmt.Write(stream);
                    r.Burned = !r.Cancelled;
                    if (r.Cancelled) r.Message = "The burn was cancelled. The disc is partly written and can't be used again.";
                }
                catch (Exception ex)
                {
                    r.Message = r.Cancelled
                        ? "The burn was cancelled. The disc is partly written and can't be used again."
                        : $"The burn failed: {DescribeImapiError(ex)}";
                }
                finally
                {
                    if (cp is not null) { try { cp.Unadvise(cookie); } catch { } Release(cp); }
                    Release(stream);
                }
            });
            if (!r.Burned && r.Message.Length == 0) r.Message = "This drive can't burn discs, or it couldn't be found.";
        }

        // ------------------------------------------------------------------
        // Burning with ImgBurn instead (v3.3, the user's choice)
        // ------------------------------------------------------------------
        // ImgBurn does the writing in its own window (it shows its own
        // progress there). When Verify is ticked, ImgBurn verifies too (it
        // also compares every sector with the ISO), and its result is read
        // from its log -- so the disc isn't read through twice.
        private static void BurnWithImgBurn(string exe, string letter, string isoPath, bool verify, IProgress<Progress>? progress, CancellationToken ct, Result r, Action<string>? logLine)
        {
            if (!File.Exists(exe))
            {
                r.Message = "ImgBurn wasn't found at " + exe + ". Install it, set its location in Settings, or burn with Windows instead.";
                return;
            }
            long isoBytes = new FileInfo(isoPath).Length;
            if (isoBytes == 0 || isoBytes % Sector != 0)
            {
                r.Message = "This file isn't a disc image made of whole 2 KB sectors, so it can't be burned as-is.";
                return;
            }
            progress?.Report(new Progress { Phase = "Preparing", Percent = -1 });

            // Same safety checks as the Windows engine, before ImgBurn opens.
            string? problem = null;
            try
            {
                RunSta(() => WithRecorder(letter, (rec, fmt) =>
                {
                    if (!(bool)fmt.IsCurrentMediaSupported(rec)) { problem = "There's no blank disc in the drive that it can write to."; return; }
                    if (!(bool)fmt.MediaHeuristicallyBlank) { problem = "The disc in the drive isn't blank."; return; }
                    // No size check here: Windows' engine can report less
                    // free space than the disc really has (BD-R), and
                    // ImgBurn checks the real size itself before writing.
                }));
            }
            catch { /* couldn't ask -- ImgBurn will check the disc itself */ }
            if (problem is not null) { r.Message = problem; return; }

            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchivSector-OD", "Logs");
            string logPath = Path.Combine(logDir, $"ImgBurn_{letter}_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");
            try { Directory.CreateDirectory(logDir); } catch { }

            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            foreach (var arg in new[]
            {
                "/MODE", "WRITE",
                "/SRC", isoPath,
                "/DEST", $"{letter}:",
                "/VERIFY", verify ? "YES" : "NO",
                "/EJECT", "NO",
                "/DELETEIMAGE", "NO",   // the app deletes it instead, and only after a verified burn
                "/LOG", logPath,
                "/START", "/CLOSESUCCESS", "/NOSAVESETTINGS", "/NOIMAGEDETAILS",
            })
                psi.ArgumentList.Add(arg);

            Process? proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex) { r.Message = $"ImgBurn couldn't be started: {ex.Message}"; return; }
            if (proc is null) { r.Message = "ImgBurn couldn't be started."; return; }

            using (proc)
            {
                progress?.Report(new Progress { Phase = "Burning with ImgBurn", Percent = -1 });
                bool askedToStop = false;
                while (!proc.WaitForExit(500))
                {
                    // Cancel = ask ImgBurn to close. ImgBurn asks its own
                    // "abort?" question, so nothing is stopped without the
                    // user confirming it in ImgBurn too.
                    if (ct.IsCancellationRequested && !askedToStop)
                    {
                        askedToStop = true;
                        try { proc.CloseMainWindow(); } catch { }
                    }
                }

                string imgBurnLog = "";
                try { if (File.Exists(logPath)) imgBurnLog = File.ReadAllText(logPath); } catch { }
                bool logSaysOk = imgBurnLog.Contains("Operation Successfully Completed", StringComparison.OrdinalIgnoreCase);
                bool logSaysAborted = imgBurnLog.Contains("Operation Aborted", StringComparison.OrdinalIgnoreCase);
                bool logSaysFailed = imgBurnLog.Contains("Operation Failed", StringComparison.OrdinalIgnoreCase);
                int code = proc.ExitCode;

                // ImgBurn logs "Operation Successfully Completed!" after the
                // write. Its verify starts with "Verifying Session..." and,
                // when every sector matched, ends with another "Operation
                // Successfully Completed!" (plus "Average Verify Rate"). A
                // mismatch logs "Miscompare" and the verify ends "Operation
                // Failed!". So the verify result is read from the part of
                // the log after the verify started.
                int verifyStart = Math.Max(
                    imgBurnLog.LastIndexOf("Verifying Session", StringComparison.OrdinalIgnoreCase),
                    imgBurnLog.LastIndexOf("Verifying Track", StringComparison.OrdinalIgnoreCase));
                string verifyPart = verifyStart >= 0 ? imgBurnLog[verifyStart..] : "";
                bool miscompare = verifyPart.Contains("Miscompare", StringComparison.OrdinalIgnoreCase);
                bool verifyFailed = verifyPart.Contains("Operation Failed", StringComparison.OrdinalIgnoreCase);
                bool verifyAborted = verifyPart.Contains("Operation Aborted", StringComparison.OrdinalIgnoreCase);
                bool exactMatch = verifyStart >= 0 && !miscompare && !verifyFailed && !verifyAborted &&
                                  (verifyPart.Contains("Operation Successfully Completed", StringComparison.OrdinalIgnoreCase) ||
                                   verifyPart.Contains("Exact Match", StringComparison.OrdinalIgnoreCase));

                if (logSaysOk || (imgBurnLog.Length == 0 && code == 0 && !askedToStop))
                {
                    r.Burned = true;
                    if (verify)
                    {
                        r.VerifiedByImgBurn = true;
                        if (exactMatch) r.Verified = true;
                        else if (miscompare || verifyFailed)
                        {
                            r.Verified = false;
                            r.Message = "Burned, but ImgBurn's verify found parts of the disc that don't match the ISO. Don't rely on this disc. ImgBurn's log: " + logPath;
                        }
                        else if (askedToStop || verifyAborted) r.Cancelled = true;
                        else r.Message = "Burned, but ImgBurn's verify result wasn't in its log, so the disc may not have been checked. ImgBurn's log: " + logPath;
                    }
                }
                else if (logSaysAborted || askedToStop)
                {
                    r.Cancelled = true;
                    r.Message = "The burn was cancelled in ImgBurn. If it had started writing, the disc can't be used again.";
                }
                else
                {
                    r.Message = "ImgBurn didn't finish the burn" + (logSaysFailed ? "" : $" (exit code {code})") +
                                ". See ImgBurn's log: " + (File.Exists(logPath) ? logPath : "(no log was written)");
                }
                logLine?.Invoke($"ImgBurn exited with code {code}; log: {logPath}");
            }
        }

        private static string DescribeImapiError(Exception ex)
        {
            int hr = ex is COMException c ? c.ErrorCode : ex.HResult;
            return hr switch
            {
                unchecked((int)0xC0AA0202) => "the disc was removed or changed during the burn.",
                unchecked((int)0xC0AA0205) => "the disc is write-protected or already used.",
                unchecked((int)0xC0AA0407) => "the drive is in use by another program.",
                unchecked((int)0xC0AA0300) => "the drive stopped responding (it may have overheated or the disc may be faulty).",
                _ => $"{ex.Message} (0x{hr:X8})",
            };
        }

        // ------------------------------------------------------------------
        // Verify: read the disc back and compare with the ISO
        // ------------------------------------------------------------------
        private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, NO_BUFFERING = 0x20000000;
        private const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint toRead, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr ov);

        private static void VerifyDisc(string letter, string isoPath, IProgress<Progress>? progress, CancellationToken ct, Result r)
        {
            progress?.Report(new Progress { Phase = "Verifying", Percent = -1 });
            const int chunk = 32 * Sector;                       // 64 KB
            IntPtr raw = Marshal.AllocHGlobal(chunk + 4096);
            IntPtr buf = new IntPtr((raw.ToInt64() + 4095) & ~4095L);
            var disc = new byte[chunk];
            var iso = new byte[chunk];
            try
            {
                // After a burn the drive re-reads the new disc; wait for it (up to 2 minutes).
                SafeFileHandle? h = null;
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromMinutes(2) && !ct.IsCancellationRequested)
                {
                    h = CreateFile($@"\\.\{letter}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, NO_BUFFERING, IntPtr.Zero);
                    if (!h.IsInvalid && SetFilePointerEx(h, 0, out _, 0) && ReadFile(h, buf, Sector, out uint got, IntPtr.Zero) && got == Sector) break;
                    h.Dispose(); h = null;
                    Thread.Sleep(2000);
                }
                if (h is null)
                {
                    r.Verified = null;
                    if (ct.IsCancellationRequested) r.Cancelled = true;
                    else r.Message = "Burned, but the disc couldn't be read back to verify it (the drive didn't become ready).";
                    return;
                }

                using (h)
                using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    DeviceIoControl(h, FSCTL_ALLOW_EXTENDED_DASD_IO, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    long total = fs.Length;
                    long done = 0;
                    var clock = Stopwatch.StartNew();
                    var lastReport = TimeSpan.Zero;
                    long mismatched = 0;
                    while (done < total)
                    {
                        if (ct.IsCancellationRequested) { r.Cancelled = true; r.Verified = null; return; }
                        int n = (int)Math.Min(chunk, total - done);
                        int nAligned = (n + Sector - 1) / Sector * Sector;
                        int isoRead = 0;
                        while (isoRead < n) { int k = fs.Read(iso, isoRead, n - isoRead); if (k <= 0) break; isoRead += k; }

                        bool ok = SetFilePointerEx(h, done, out _, 0) && ReadFile(h, buf, (uint)nAligned, out uint got, IntPtr.Zero) && got >= n;
                        if (ok)
                        {
                            Marshal.Copy(buf, disc, 0, n);
                            for (int off = 0; off < n; off += Sector)
                            {
                                int len = Math.Min(Sector, n - off);
                                if (!disc.AsSpan(off, len).SequenceEqual(iso.AsSpan(off, len))) mismatched++;
                            }
                        }
                        else mismatched += nAligned / Sector;

                        if (mismatched > 10_000) break;   // clearly a bad burn -- no need to read the rest
                        done += n;
                        if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(400) || done >= total)
                        {
                            lastReport = clock.Elapsed;
                            double bps = done / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                            progress?.Report(new Progress
                            {
                                Phase = "Verifying",
                                Percent = 100.0 * done / total,
                                BytesPerSec = bps,
                                Remaining = TimeSpan.FromSeconds((total - done) / Math.Max(1, bps)),
                            });
                        }
                    }
                    r.MismatchedSectors = mismatched;
                    r.Verified = mismatched == 0;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
            }
        }

        // ------------------------------------------------------------------
        // Ejecting, helpers
        // ------------------------------------------------------------------
        // Opens the tray through Windows' burning engine. This works with a
        // blank disc in the drive, where the usual "Eject" (which works on
        // the disc's file system) does nothing because there isn't one yet.
        // Returns false if the drive isn't a burner or the call failed.
        public static bool Eject(string driveLetter)
        {
            var letter = Norm(driveLetter);
            bool ok = false;
            try
            {
                RunSta(() =>
                {
                    var id = RecorderIdFor(letter);
                    if (id is null) return;
                    object? recObj = null;
                    try
                    {
                        recObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscRecorder2")!)!;
                        dynamic rec = recObj;
                        rec.InitializeDiscRecorder(id);
                        rec.EjectMedia();
                        ok = true;
                    }
                    finally { Release(recObj); }
                });
            }
            catch { ok = false; }
            return ok;
        }

        private static void RunSta(Action work)
        {
            Exception? error = null;
            var th = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            th.Join();
            if (error is not null) throw error;
        }

        private static void Release(object? com)
        {
            if (com is null || !Marshal.IsComObject(com)) return;
            try { Marshal.FinalReleaseComObject(com); } catch { }
        }
    }
}
