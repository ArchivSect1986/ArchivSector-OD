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
    // Windows' engine can't erase every kind: DVD+RW and BD-RE usually
    // aren't supported (they can be written over without erasing). The
    // direct drive connection in week 4 adds that. QueryErase says which.
    // An erase can't be stopped once it starts.
    public static partial class BurnEngine
    {
        public sealed class EraseInfo
        {
            public bool CanErase;
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
                        info.MediaType = WritableLabel(type) ?? "";

                        eraseObj = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2.MsftDiscFormat2Erase")!)!;
                        dynamic erase = eraseObj;
                        bool recorderOk = false, mediaOk = false;
                        try { recorderOk = (bool)erase.IsRecorderSupported(recObj); } catch { }
                        if (recorderOk) { try { mediaOk = (bool)erase.IsCurrentMediaSupported(recObj); } catch { } }

                        if (mediaOk) { info.CanErase = true; return; }
                        info.Problem = type switch
                        {
                            7 or 13 or 19 => $"Windows' burning engine can't erase a {info.MediaType}. This kind of disc can be written over without erasing; that comes with the direct drive connection.",
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
        public static Task<EraseResult> EraseAsync(string driveLetter, bool full, IProgress<EraseProgress>? progress, Action<string>? log = null)
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
                    cp.Advise(sink, out cookie);
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
