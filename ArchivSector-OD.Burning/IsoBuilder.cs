using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;

namespace ArchivSector_OD.Burning
{
    // Build mode (v4.0 week 3): makes an ISO image from a folder, like
    // ImgBurn's "Create image file from files/folders". Uses Windows' own
    // disc image builder (IMAPI2FS), so nothing extra needs installing.
    //
    // File systems:
    //   * ISO9660 + Joliet + UDF: readable everywhere (old players, every
    //     Windows, Mac, Linux). Windows' image builder can't put a file of
    //     2 GB or more in it (it refuses with 0xC0AAB132).
    //   * UDF only: needed for files of 2 GB or more; what movie discs use.
    //   "Automatic" picks the first unless a file is 2 GB or bigger, and
    //   switches to UDF only if Windows still refuses a file.
    //
    // IMAPI2 must run on an STA thread, so the build runs on its own thread
    // and reports progress back. Nothing here touches a drive.
    public static class IsoBuilder
    {
        public const long Sector = 2048;

        public enum FileSystemChoice { Automatic, IsoJolietUdf, UdfOnly }

        // What the image is meant for: sets the UDF version and the size
        // check. "Any" means no size limit.
        public enum MediaTarget { Any, Cd, Dvd, DvdDualLayer, Bd25, Bd50 }

        // The smallest common size of each disc type, so an ISO that fits
        // here fits every brand (DVD-R and DVD+R differ by a few MB).
        public static long CapacityOf(MediaTarget t) => t switch
        {
            MediaTarget.Cd => 359_844L * Sector,              // 80 min CD-R as drives report it: 736,960,512 bytes
            MediaTarget.Dvd => 2_295_104L * Sector,           // DVD+R: 4,700,372,992 bytes
            MediaTarget.DvdDualLayer => 4_171_712L * Sector,  // DVD-R DL: 8,543,666,176 bytes
            MediaTarget.Bd25 => 12_219_392L * Sector,         // BD-R 25 GB: 25,025,314,816 bytes
            MediaTarget.Bd50 => 24_438_784L * Sector,         // BD-R 50 GB: 50,050,629,632 bytes
            _ => 0,
        };

        public static string NameOf(MediaTarget t) => t switch
        {
            MediaTarget.Cd => "CD (700 MB)",
            MediaTarget.Dvd => "DVD (4.7 GB)",
            MediaTarget.DvdDualLayer => "DVD DL (8.5 GB)",
            MediaTarget.Bd25 => "BD-25",
            MediaTarget.Bd50 => "BD-50",
            _ => "Any size",
        };

        // The biggest file Windows' image builder accepts in ISO9660/Joliet.
        public const long LargeFileBytes = 2L * 1024 * 1024 * 1024 - 1;

        public sealed class Options
        {
            public string SourceFolder = "";
            public string OutputPath = "";
            public string VolumeLabel = "";
            public FileSystemChoice FileSystem = FileSystemChoice.Automatic;
            public MediaTarget Target = MediaTarget.Any;
            public bool IncludeFolderItself;      // put the folder on the disc, not just what's inside it
            // Pad the finished ISO with zeros up to this size (0 = no padding),
            // like TargetFill's "Pad existing ISOs". The real size is noted on
            // the file so "Remove padding" can undo it exactly.
            public long PadToBytes;
        }

        // BD-25 as Windows' burning engine formats it (768 MB kept spare).
        public const long Bd25WindowsBytes = 24_220_008_448;

        // The note TargetFill's padding uses, so "Remove padding" knows the
        // ISO's real size. An NTFS alternate data stream: hidden, not part
        // of the ISO's contents.
        public const string OriginalSizeStream = ":ArchivSectorOD.OriginalSize";

        public sealed class Progress
        {
            public string Phase = "";             // "Reading the folder", "Writing the ISO"
            public double Percent = -1;           // -1 = unknown
            public long BytesDone;
            public long TotalBytes;
            public double BytesPerSec;
            public TimeSpan Remaining;
        }

        public sealed class Result
        {
            public bool Ok;
            public bool Cancelled;
            public string Message = "";
            public string? IsoPath;
            public long Bytes;
            public string FileSystems = "";
            public TimeSpan Time;
        }

        public sealed class FolderInfo
        {
            public long Files;
            public long Folders;
            public long Bytes;
            public long LargestFile;
            public string? LargestFileName;
            public bool HasLargeFile => LargestFile > LargeFileBytes;
            public string? Error;
        }

        // Counts the folder's files and size (quick; no image is made).
        public static FolderInfo ScanFolder(string folder, CancellationToken ct = default)
        {
            var info = new FolderInfo();
            try
            {
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(folder));
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var dir = stack.Pop();
                    FileSystemInfo[] entries;
                    try { entries = dir.GetFileSystemInfos(); }
                    catch (UnauthorizedAccessException) { continue; }
                    foreach (var e in entries)
                    {
                        if (e is DirectoryInfo d)
                        {
                            // Skip junction points/links: IMAPI2 would follow them and could loop.
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            info.Folders++;
                            stack.Push(d);
                        }
                        else if (e is FileInfo f)
                        {
                            info.Files++;
                            info.Bytes += f.Length;
                            if (f.Length > info.LargestFile) { info.LargestFile = f.Length; info.LargestFileName = f.FullName; }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { info.Error = ex.Message; }
            return info;
        }

        // A disc label IMAPI2 accepts: letters, digits and underscores, up to 32.
        public static string CleanLabel(string label)
        {
            var clean = Regex.Replace(label.Trim(), "[^a-zA-Z0-9_]", "_");
            clean = Regex.Replace(clean, "_{2,}", "_").Trim('_');
            if (clean.Length > 32) clean = clean[..32];
            return clean.Length == 0 ? "DISC" : clean.ToUpperInvariant();
        }

        // The file systems a build will use, for showing before it starts.
        public static FileSystemChoice Resolve(FileSystemChoice choice, bool hasLargeFile) =>
            choice != FileSystemChoice.Automatic ? choice
            : hasLargeFile ? FileSystemChoice.UdfOnly
            : FileSystemChoice.IsoJolietUdf;

        public static string Describe(FileSystemChoice resolved, MediaTarget target) =>
            resolved == FileSystemChoice.UdfOnly
                ? $"UDF {UdfVersionText(target)} only"
                : $"ISO9660 + Joliet + UDF {UdfVersionText(target)}";

        private static int UdfRevision(MediaTarget t) => t is MediaTarget.Bd25 or MediaTarget.Bd50 ? 0x250 : 0x102;
        private static string UdfVersionText(MediaTarget t) => t is MediaTarget.Bd25 or MediaTarget.Bd50 ? "2.50" : "1.02";

        public static Task<Result> BuildAsync(Options o, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log = null)
        {
            var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
            var th = new Thread(() =>
            {
                try { tcs.SetResult(Build(o, progress, ct, log)); }
                catch (Exception ex) { tcs.SetResult(new Result { Message = "The ISO couldn't be built: " + ex.Message }); }
            });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            return tcs.Task;
        }

        private const int ErrFileTooBigForIso9660 = unchecked((int)0xC0AAB132);

        private static Result Build(Options o, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log)
        {
            var r = BuildOnce(o, progress, ct, log, forceUdf: false, out int hr);
            // Automatic: Windows refused a file for ISO9660 -- build it as UDF only.
            if (!r.Ok && hr == ErrFileTooBigForIso9660 && o.FileSystem == FileSystemChoice.Automatic)
            {
                log?.Invoke("Build: a file is too big for ISO9660/Joliet, building as UDF only instead.");
                r = BuildOnce(o, progress, ct, log, forceUdf: true, out _);
            }
            return r;
        }

        private static Result BuildOnce(Options o, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log, bool forceUdf, out int hresult)
        {
            hresult = 0;
            var sw = Stopwatch.StartNew();
            if (!Directory.Exists(o.SourceFolder))
                return new Result { Message = "The folder to build from doesn't exist any more." };
            if (string.IsNullOrWhiteSpace(o.OutputPath))
                return new Result { Message = "Choose where to save the ISO." };

            var outFull = Path.GetFullPath(o.OutputPath);
            var srcFull = Path.GetFullPath(o.SourceFolder).TrimEnd('\\') + "\\";
            if (outFull.StartsWith(srcFull, StringComparison.OrdinalIgnoreCase))
                return new Result { Message = "Save the ISO outside the folder you're building from, or it would try to include itself." };

            progress?.Report(new Progress { Phase = "Reading the folder" });
            var info = ScanFolder(o.SourceFolder, ct);
            if (info.Error is not null) return new Result { Message = "The folder couldn't be read: " + info.Error };
            if (info.Files == 0 && info.Folders == 0) return new Result { Message = "The folder is empty." };

            var fs = forceUdf ? FileSystemChoice.UdfOnly : Resolve(o.FileSystem, info.HasLargeFile);
            if (fs == FileSystemChoice.IsoJolietUdf && info.HasLargeFile)
                return new Result { Message = $"{Path.GetFileName(info.LargestFileName)} is 2 GB or bigger, which ISO9660 can't hold here. Choose \"UDF only\" or \"Automatic\"." };
            string fsText = Describe(fs, o.Target);
            string label = CleanLabel(string.IsNullOrWhiteSpace(o.VolumeLabel) ? Path.GetFileName(o.SourceFolder.TrimEnd('\\')) : o.VolumeLabel);
            log?.Invoke($"Build: {o.SourceFolder} -> {outFull} ({info.Files:N0} files, {info.Bytes:N0} bytes, {fsText}, label {label}, for {NameOf(o.Target)})");

            object? fsiObj = null, resultObj = null, streamObj = null;
            var temp = outFull + ".partial";
            try
            {
                ct.ThrowIfCancellationRequested();
                var t = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")
                    ?? throw new InvalidOperationException("Windows' disc image builder (IMAPI2) isn't available on this PC.");
                fsiObj = Activator.CreateInstance(t)!;
                dynamic fsi = fsiObj;

                int physical = o.Target switch
                {
                    MediaTarget.Cd => 2,              // CD-R
                    MediaTarget.Dvd => 6,             // DVD+R
                    MediaTarget.DvdDualLayer => 8,    // DVD+R DL
                    MediaTarget.Bd25 or MediaTarget.Bd50 => 18,   // BD-R
                    _ => info.Bytes > CapacityOf(MediaTarget.Dvd) ? 18 : 6,
                };
                TryCom(() => fsi.ChooseImageDefaultsForMediaType(physical));
                fsi.FileSystemsToCreate = fs == FileSystemChoice.UdfOnly ? 4 : 7;   // 1 ISO9660, 2 Joliet, 4 UDF
                TryCom(() => fsi.UDFRevision = UdfRevision(o.Target));
                // No size limit from IMAPI2 itself; the size is checked below.
                TryCom(() => fsi.FreeMediaBlocks = 0);
                fsi.VolumeName = label;

                progress?.Report(new Progress { Phase = "Reading the folder", TotalBytes = info.Bytes });
                object rootObj = fsi.Root;
                try { ((dynamic)rootObj).AddTree(o.SourceFolder, o.IncludeFolderItself); }
                finally { Release(rootObj); }
                ct.ThrowIfCancellationRequested();

                resultObj = fsi.CreateResultImage();
                dynamic result = resultObj;
                long total = Convert.ToInt64(result.TotalBlocks) * Sector;

                long cap = CapacityOf(o.Target);
                if (cap > 0 && total > cap)
                    return new Result
                    {
                        Message = $"The ISO would be {Fmt(total)}, which is {Fmt(total - cap)} too big for a {NameOf(o.Target)}. Remove some files or pick a bigger disc.",
                        FileSystems = fsText,
                    };

                if (o.PadToBytes > 0 && total > o.PadToBytes)
                    return new Result
                    {
                        Message = $"The ISO would be {Fmt(total)}, which is {Fmt(total - o.PadToBytes)} more than the {Fmt(o.PadToBytes)} it's meant to be padded to. Remove some files, or pad to the whole disc.",
                        FileSystems = fsText,
                    };
                long finalSize = Math.Max(total, o.PadToBytes);

                // Room on the drive for the ISO?
                try
                {
                    var root = Path.GetPathRoot(outFull);
                    if (!string.IsNullOrEmpty(root))
                    {
                        var di = new DriveInfo(root);
                        if (di.IsReady && di.AvailableFreeSpace < finalSize + 64L * 1024 * 1024)
                            return new Result { Message = $"There isn't enough free space on {root} for the ISO ({Fmt(finalSize)} needed, {Fmt(di.AvailableFreeSpace)} free)." };
                    }
                }
                catch { /* network paths etc.: just try */ }

                Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
                streamObj = result.ImageStream;
                var stream = (IStream)streamObj;
                long done = 0;
                var speedClock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    var readPtr = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            stream.Read(buf, buf.Length, readPtr);
                            int read = Marshal.ReadInt32(readPtr);
                            if (read <= 0) break;
                            file.Write(buf, 0, read);
                            done += read;
                            if (speedClock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                            {
                                lastReport = speedClock.Elapsed;
                                double bps = done / Math.Max(0.001, speedClock.Elapsed.TotalSeconds);
                                progress?.Report(new Progress
                                {
                                    Phase = "Writing the ISO",
                                    Percent = total > 0 ? Math.Min(100, 100.0 * done / total) : -1,
                                    BytesDone = done,
                                    TotalBytes = total,
                                    BytesPerSec = bps,
                                    Remaining = bps > 0 ? TimeSpan.FromSeconds((total - done) / bps) : TimeSpan.Zero,
                                });
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(readPtr); }

                    // Padding: zeros on the end, up to the disc size.
                    if (o.PadToBytes > done)
                    {
                        progress?.Report(new Progress { Phase = "Padding to the disc size", Percent = -1, BytesDone = done, TotalBytes = o.PadToBytes });
                        file.SetLength(o.PadToBytes);
                    }
                }
                Release(streamObj); streamObj = null;
                File.Move(temp, outFull, overwrite: true);
                bool padded = o.PadToBytes > done;
                try
                {
                    if (padded) File.WriteAllText(outFull + OriginalSizeStream, done.ToString());
                    else if (File.Exists(outFull + OriginalSizeStream)) File.Delete(outFull + OriginalSizeStream);
                }
                catch { /* FAT/exFAT drives have no streams; Remove padding falls back to reading the ISO itself */ }
                sw.Stop();
                long size = new FileInfo(outFull).Length;
                string padNote = padded ? ", padded from " + done.ToString("N0") : "";
                log?.Invoke($"Build finished: {outFull} ({size:N0} bytes{padNote}) in {sw.Elapsed:hh\\:mm\\:ss}");
                return new Result
                {
                    Ok = true,
                    IsoPath = outFull,
                    Bytes = size,
                    FileSystems = fsText,
                    Time = sw.Elapsed,
                    Message = padded
                        ? $"ISO built and padded: {Path.GetFileName(outFull)}, {Fmt(done)} of files padded to {Fmt(size)} ({fsText})."
                        : $"ISO built: {Path.GetFileName(outFull)}, {Fmt(size)} ({fsText}).",
                };
            }
            catch (OperationCanceledException)
            {
                log?.Invoke("Build cancelled.");
                return new Result { Cancelled = true, Message = "Cancelled. No ISO was saved." };
            }
            catch (COMException ex)
            {
                hresult = ex.HResult;
                log?.Invoke($"Build failed: {ex.Message.Trim()} (0x{ex.HResult:X8})");
                return new Result { Message = ComMessage(ex) };
            }
            catch (Exception ex)
            {
                log?.Invoke("Build failed: " + ex.Message);
                return new Result { Message = "The ISO couldn't be built: " + ex.Message };
            }
            finally
            {
                Release(streamObj);
                Release(resultObj);
                Release(fsiObj);
                // IMAPI2 keeps the source files open until every wrapper is gone.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        // IMAPI2's own error text is usually clear (a name too long, a file
        // too big for ISO9660, the image too large), so it's shown as is.
        private static string ComMessage(COMException ex) =>
            $"The ISO couldn't be built: {ex.Message.Trim()} (0x{unchecked((uint)ex.HResult):X8})";

        private static void TryCom(Action a)
        {
            try { a(); } catch { /* setting not supported on this Windows version */ }
        }

        private static void Release(object? com)
        {
            if (com is null) return;
            try { if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); } catch { }
        }

        private static string Fmt(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB"
            : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB"
            : $"{b / 1024.0:0} KB";
    }
}
