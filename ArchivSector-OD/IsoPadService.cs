using System.Diagnostics;
using System.IO;

namespace ArchivSector_OD
{
    // v3.3: TargetFill padding for an ISO that's already made.
    //
    // Padding is added to the END of the image: empty (zero) sectors up to
    // the exact size of the disc. Nothing inside the ISO moves, and its file
    // system doesn't list the padding, so players and drives ignore it --
    // the disc just gets written all the way out to the edge. (Padding at
    // the START would mean moving everything in the image and rebuilding
    // it, which isn't safe for movie and game discs, so that isn't offered.)
    //
    //   * Pad: makes a padded copy ("<name>_padded.iso", the original is
    //     left alone) or pads the original file in place (instant).
    //   * RemovePadding: takes zero padding back off the end, down to the
    //     size the ISO's own file system records -- only if every byte
    //     being removed is zero, so real data is never cut off.
    public static class IsoPadService
    {
        private const int Sector = 2048;

        public sealed class Result
        {
            public bool Ok;
            public string Message = "";
            public string? OutputPath;
            public long Added;
        }

        public sealed class Progress
        {
            public string Phase = "";
            public double Percent;
        }

        // The ISO's size before padding, or -1 if it can't be told. Checked
        // in this order:
        //   1. a note the app leaves on the file when it pads it (an NTFS
        //      "alternate data stream" -- hidden, not part of the ISO's
        //      contents, so the image itself is unchanged);
        //   2. the size the ISO's file systems record: ISO 9660 and UDF
        //      (Blu-ray images are often UDF only). The larger is used, so
        //      less is ever removed.
        // RemovePadding still checks that every byte it removes is zero.
        public static long RecordedSize(string isoPath)
        {
            long noted = ReadNote(isoPath);
            if (noted > 0) return noted;
            return Math.Max(Iso9660Size(isoPath), UdfSize(isoPath));
        }

        private const string NoteStream = ":ArchivSectorOD.OriginalSize";

        private static long ReadNote(string isoPath)
        {
            try
            {
                var text = File.ReadAllText(isoPath + NoteStream).Trim();
                return long.TryParse(text, out var v) && v > 0 ? v : -1;
            }
            catch { return -1; }
        }

        private static void WriteNote(string isoPath, long originalSize)
        {
            try { File.WriteAllText(isoPath + NoteStream, originalSize.ToString()); } catch { /* FAT/exFAT drives have no streams */ }
        }

        private static void DeleteNote(string isoPath)
        {
            try { File.Delete(isoPath + NoteStream); } catch { }
        }

        // UDF: the anchor at sector 256 points to the volume descriptors;
        // the partition descriptor says where the data ends. A UDF image
        // usually ends with one more anchor (and a backup copy of the
        // descriptors) just after that, so the last anchor found shortly
        // after the partition marks the real end of the image.
        private static long UdfSize(string isoPath)
        {
            try
            {
                using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                long sectors = fs.Length / Sector;
                var buf = new byte[Sector];
                bool ReadSector(long n)
                {
                    if (n < 0 || n >= sectors) return false;
                    fs.Position = n * Sector;
                    return fs.Read(buf, 0, Sector) == Sector;
                }
                ushort TagId() => BitConverter.ToUInt16(buf, 0);
                uint TagLocation() => BitConverter.ToUInt32(buf, 12);

                if (!ReadSector(256) || TagId() != 2 || TagLocation() != 256) return -1;
                uint vdsLength = BitConverter.ToUInt32(buf, 16);
                uint vdsStart = BitConverter.ToUInt32(buf, 20);

                long partEnd = -1;
                for (long n = vdsStart; n < vdsStart + Math.Min(64, vdsLength / Sector); n++)
                {
                    if (!ReadSector(n)) break;
                    if (TagId() == 8) break;                       // terminating descriptor
                    if (TagId() != 5) continue;                    // partition descriptor
                    long start = BitConverter.ToUInt32(buf, 188);
                    long length = BitConverter.ToUInt32(buf, 192);
                    partEnd = Math.Max(partEnd, start + length);
                }
                if (partEnd <= 0) return -1;

                long lastAnchor = -1;
                for (long n = partEnd; n < Math.Min(sectors, partEnd + 4096); n++)
                    if (ReadSector(n) && TagId() == 2 && TagLocation() == n) lastAnchor = n;
                return (lastAnchor >= 0 ? lastAnchor + 1 : partEnd) * Sector;
            }
            catch { return -1; }
        }

        // ISO 9660: the primary volume descriptor (sector 16 onwards)
        // records the "volume space size".
        private static long Iso9660Size(string isoPath)
        {
            try
            {
                using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[Sector];
                // Volume descriptors start at sector 16; look through a few for the primary one.
                for (int s = 16; s < 32; s++)
                {
                    fs.Position = (long)s * Sector;
                    if (fs.Read(buf, 0, Sector) != Sector) return -1;
                    if (buf[1] != 'C' || buf[2] != 'D' || buf[3] != '0' || buf[4] != '0' || buf[5] != '1') return -1;
                    if (buf[0] == 255) return -1;                 // terminator: no primary descriptor
                    if (buf[0] != 1) continue;                    // not the primary one
                    long blocks = BitConverter.ToUInt32(buf, 80); // little-endian copy
                    int blockSize = BitConverter.ToUInt16(buf, 128);
                    if (blockSize == 0) blockSize = Sector;
                    return blocks * blockSize;
                }
            }
            catch { }
            return -1;
        }

        public static Task<Result> PadAsync(string isoPath, TargetFillService.MediaPreset media, bool makeCopy,
            IProgress<Progress>? progress, CancellationToken ct) =>
            Task.Run(() => Pad(isoPath, media, makeCopy, progress, ct), CancellationToken.None);

        public static Task<Result> RemovePaddingAsync(string isoPath, IProgress<Progress>? progress, CancellationToken ct) =>
            Task.Run(() => RemovePadding(isoPath, progress, ct), CancellationToken.None);

        private static Result Pad(string isoPath, TargetFillService.MediaPreset media, bool makeCopy,
            IProgress<Progress>? progress, CancellationToken ct)
        {
            var r = new Result();
            long size;
            try { size = new FileInfo(isoPath).Length; }
            catch (Exception ex) { r.Message = $"Couldn't read the ISO: {ex.Message}"; return r; }

            long target = media.Bytes / Sector * Sector;
            if (size > target)
            {
                r.Message = $"This ISO ({TargetFillService.FormatBytes(size)}) is already bigger than a {media.Label} ({TargetFillService.FormatBytes(target)}), so it can't be padded to fit one.";
                return r;
            }
            if (size == target)
            {
                r.Message = $"This ISO is already exactly the size of a {media.Label}. Nothing to add.";
                return r;
            }
            long add = target - size;

            string dir = Path.GetDirectoryName(Path.GetFullPath(isoPath)) ?? ".";
            long free = FreeSpace(dir);
            long needed = makeCopy ? target : add;
            if (free >= 0 && free < needed + (64L << 20))
            {
                r.Message = $"Not enough free space on that drive: this needs {TargetFillService.FormatBytes(needed)}, and {TargetFillService.FormatBytes(free)} is free.";
                return r;
            }

            if (!makeCopy)
            {
                try
                {
                    progress?.Report(new Progress { Phase = "Padding", Percent = -1 });
                    using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    fs.SetLength(target);   // the new space reads back as zeros
                    fs.Flush(true);
                }
                catch (Exception ex)
                {
                    r.Message = $"Couldn't pad the ISO: {ex.Message}" + (ex is IOException ? " (Is it open in another program, like ImgBurn or a burn in progress?)" : "");
                    return r;
                }
                if (ReadNote(isoPath) <= 0) WriteNote(isoPath, size);
                r.Ok = true;
                r.Added = add;
                r.OutputPath = isoPath;
                r.Message = $"Padded to {TargetFillService.FormatBytes(target)} ({target:N0} bytes, 100% of a {media.Label}): added {TargetFillService.FormatBytes(add)} of empty space at the end.";
                return r;
            }

            string name = Path.GetFileNameWithoutExtension(isoPath);
            string ext = Path.GetExtension(isoPath);
            string outPath = Path.Combine(dir, name + "_padded" + ext);
            for (int i = 2; File.Exists(outPath); i++) outPath = Path.Combine(dir, $"{name}_padded ({i}){ext}");
            string partial = outPath + ".partial";

            try
            {
                const int chunk = 4 << 20;
                var buf = new byte[chunk];
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                using (var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.SequentialScan))
                using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, chunk))
                {
                    dst.SetLength(target);     // reserve the space up front (fails early if it won't fit)
                    long done = 0;
                    int n;
                    while ((n = src.Read(buf, 0, chunk)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buf, 0, n);
                        done += n;
                        if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(300))
                        {
                            lastReport = clock.Elapsed;
                            progress?.Report(new Progress { Phase = "Copying", Percent = 100.0 * done / size });
                        }
                    }
                    dst.Flush(true);
                }
                File.Move(partial, outPath);
                WriteNote(outPath, ReadNote(isoPath) > 0 ? ReadNote(isoPath) : size);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                r.Message = "Stopped. The padded copy wasn't made; the original ISO is unchanged.";
                return r;
            }
            catch (Exception ex)
            {
                TryDelete(partial);
                r.Message = $"Couldn't make the padded copy: {ex.Message}";
                return r;
            }

            r.Ok = true;
            r.Added = add;
            r.OutputPath = outPath;
            r.Message = $"Made a padded copy: {Path.GetFileName(outPath)}, {TargetFillService.FormatBytes(target)} ({target:N0} bytes, 100% of a {media.Label}). The original is unchanged.";
            return r;
        }

        private static Result RemovePadding(string isoPath, IProgress<Progress>? progress, CancellationToken ct)
        {
            var r = new Result();
            long size;
            try { size = new FileInfo(isoPath).Length; }
            catch (Exception ex) { r.Message = $"Couldn't read the ISO: {ex.Message}"; return r; }

            long recorded = RecordedSize(isoPath);
            if (recorded <= 0)
            {
                r.Message = "The app can't tell this ISO's size before padding (it wasn't padded by this app, and its file system doesn't record it), so nothing was removed.";
                return r;
            }
            if (recorded >= size)
            {
                r.Message = "This ISO has no padding at the end to remove.";
                return r;
            }

            // Every byte being removed must be zero.
            try
            {
                const int chunk = 4 << 20;
                var buf = new byte[chunk];
                var clock = Stopwatch.StartNew();
                var lastReport = TimeSpan.Zero;
                using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.SequentialScan))
                {
                    fs.Position = recorded;
                    long todo = size - recorded, done = 0;
                    int n;
                    while ((n = fs.Read(buf, 0, (int)Math.Min(chunk, todo - done))) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0)
                        {
                            r.Message = "The end of this ISO holds data, not just padding, so nothing was removed.";
                            return r;
                        }
                        done += n;
                        if (clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(300))
                        {
                            lastReport = clock.Elapsed;
                            progress?.Report(new Progress { Phase = "Checking", Percent = 100.0 * done / todo });
                        }
                    }
                }
                using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    fs.SetLength(recorded);
                DeleteNote(isoPath);
            }
            catch (OperationCanceledException)
            {
                r.Message = "Stopped. Nothing was removed.";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = $"Couldn't remove the padding: {ex.Message}";
                return r;
            }

            r.Ok = true;
            r.Added = recorded - size;
            r.OutputPath = isoPath;
            r.Message = $"Removed {TargetFillService.FormatBytes(size - recorded)} of padding. The ISO is back to {TargetFillService.FormatBytes(recorded)} ({recorded:N0} bytes).";
            return r;
        }

        private static long FreeSpace(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
            catch { return -1; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
