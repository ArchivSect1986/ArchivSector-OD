using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace ArchivSector_OD
{
    // TargetFill, ported from TargetFill Pro v3.0.4 (PowerShell) into
    // ArchivSector-OD, feature for feature. Pads a folder so the disc it's
    // burned to is filled to exactly 100%: filler goes at the start
    // (lead-in) and/or end (lead-out) of the disc, which puts the real data
    // in the middle tracks. Filler files are NTFS sparse files, so
    // gigabytes appear instantly without using disk space or wearing an SSD.
    //
    // Compatible with the PowerShell version on purpose: same filler names
    // (00_pad_start.dat / zz_pad_end.dat in a folder,
    // 00_lead_in_part1_filler.dat / zz_lead_out_part2_filler.dat inside
    // ISOs and Disc_NN folders, the stealth names), the same hidden
    // ".targetfill_generated" tracker ("kind|relative path" per line), the
    // same safety margins, manifest format, Verify_Disc scripts, PAR2 file
    // names, catalog files (including the shared master catalog in
    // %LOCALAPPDATA%\TargetFillPro) and label file names. A folder prepared
    // by either one can be cleaned or rebuilt by the other.
    //
    // Everything here is UI-free: questions (replace old disc folders?
    // build ISOs or folders?) are asked by TargetFillPanel, which then
    // calls the matching method.
    public static class TargetFillService
    {
        public record MediaPreset(string Label, long Bytes, bool IsUsb, int Kind);

        // Kind: 1 = CD, 2 = DVD-5, 3 = DVD-9, 4 = Blu-ray, 0 = USB/drive.
        // Labels are the PowerShell version's exactly, so the shared
        // catalog's MediaType column matches between the two. Capacities
        // are the real recordable size in 2 KB sectors; for DVD it's the
        // smaller of the + and - formats, so an image fits both.
        public static readonly IReadOnlyList<MediaPreset> OpticalPresets = new[]
        {
            new MediaPreset("CD-R 700MB", 737280000L, false, 1),
            new MediaPreset("DVD-5 4.7GB", 4700372992L, false, 2),   // 2,295,104 sectors: DVD+R (DVD-R is a little bigger)
            new MediaPreset("DVD-9 8.5GB", 8543666176L, false, 3),   // 4,171,712 sectors: DVD-R DL (DVD+R DL is a little bigger)
            new MediaPreset("Blu-ray BD-25", 25025314816L, false, 4),
            new MediaPreset("Blu-ray BD-50", 50050629632L, false, 4),
            new MediaPreset("Blu-ray BD-100", 100103356416L, false, 4),
            new MediaPreset("Blu-ray BD-128", 128001769472L, false, 4),
        };

        public static readonly IReadOnlyList<MediaPreset> UsbPresets = new[]
        {
            new MediaPreset("USB 16GB", 16000000000L, true, 0),
            new MediaPreset("USB 32GB", 32000000000L, true, 0),
            new MediaPreset("USB 64GB", 64000000000L, true, 0),
            new MediaPreset("USB 128GB", 128000000000L, true, 0),
            new MediaPreset("USB 256GB", 256000000000L, true, 0),
            new MediaPreset("USB 512GB", 512000000000L, true, 0),
            new MediaPreset("USB 1TB", 1000000000000L, true, 0),
            new MediaPreset("USB 2TB", 2000000000000L, true, 0),
        };

        // Custom sizes, in GB of 1,000,000,000 bytes like the PowerShell version.
        public static MediaPreset CustomOptical(double gb)
        {
            long bytes = (long)(Math.Max(0.01, gb) * 1_000_000_000);
            int kind = bytes <= 900_000_000L ? 1 : bytes <= 4_700_000_000L ? 2 : bytes <= 8_600_000_000L ? 3 : 4;
            return new MediaPreset("Custom", bytes, false, kind);
        }

        public static MediaPreset CustomUsb(double gb) =>
            new("Custom USB", (long)(Math.Max(0.01, gb) * 1_000_000_000), true, 0);

        public enum Placement { DualSplit = 0, EndOnly = 1, StartOnly = 2 }

        public class Options
        {
            public MediaPreset Media = OpticalPresets[3];
            public Placement Placement = Placement.DualSplit;
            public int SplitPercent = 50;       // share of filler at the start (Dual-Part Split)
            public bool Physical = false;       // USB: write real zero blocks instead of sparse
            public bool Manifest = true;
            public bool Verifier = true;
            public bool Catalog = true;
            public bool Preflight = true;
            public bool LbaReport = true;       // zone layout lines in the manifest
            public bool Parity = false;         // PAR2 recovery files (needs par2.exe)
            public bool Noise = false;          // random-data filler instead of zeros
            public bool Stealth = false;        // filler named like cache files
            public bool Sound = true;
            public string VolumeLabel = "";
        }

        public class Result
        {
            public bool Ok;
            public bool TooBig;                 // payload doesn't fit on one disc
            public string Message = "";
            public List<string> Outputs = new();
            public List<string> Notes = new();
        }

        public class PayloadStats
        {
            public long Bytes;
            public int Count;
            public List<FileInfo> Files = new();
            public long Rounded;         // every file rounded up to whole 2 KB sectors
            public int Dirs;             // folders that hold files
            public long ManifestBytes;   // size of the manifest's hash lines
        }

        public const long Sector = 2048;
        public static long RoundUp(long n) => (n + Sector - 1) / Sector * Sector;

        // Fills in the disc-space details for a list of files (relative to root).
        public static PayloadStats StatsOf(IEnumerable<FileInfo> files, string root)
        {
            var s = new PayloadStats();
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                long len;
                try { len = f.Length; } catch { continue; }
                var rel = RelPath(f.FullName, root);
                s.Files.Add(f);
                s.Bytes += len;
                s.Rounded += RoundUp(len);
                s.Count++;
                s.ManifestBytes += 64 + 3 + Encoding.UTF8.GetByteCount(rel) + 2;
                var dir = Path.GetDirectoryName(rel);
                if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
            }
            s.Dirs = dirs.Count;
            return s;
        }

        // Space a disc's file system (ISO9660 / Joliet / UDF) needs besides
        // the files' own bytes: fixed structures, an entry per file and per
        // folder, every file rounded up to whole 2 KB sectors, and room for
        // the manifest, verifier, catalog, PAR2 and filler files TargetFill
        // adds. Tight but safe, so a padded folder burns to about 99.7%
        // (CD) / 99.9% (DVD, Blu-ray) with any burning software. ISO builds
        // are then trimmed to the exact sector: 100%.
        public static long Overhead(PayloadStats s, MediaPreset m)
        {
            const int extraFiles = 40;
            long fixedBytes = m.Kind >= 4 ? 4L << 20 : 2L << 20;
            long perFile = (s.Count + extraFiles) * 2 * Sector;
            long perDir = (s.Dirs + 4) * 2 * Sector;
            long rounding = s.Rounded - s.Bytes + extraFiles * Sector;
            long extras = RoundUp(s.ManifestBytes + 1024) + 16 * Sector;
            return fixedBytes + perFile + perDir + rounding + extras;
        }

        // ---------------------------------------------------------------
        // Names and patterns (identical to the PowerShell version)
        // ---------------------------------------------------------------
        public const string TrackerName = ".targetfill_generated";
        private const string ManifestName = "TargetFill_Archival_Manifest.txt";
        private const int Par2RedundancyPct = 10;

        private static readonly Regex LegacyFillerRx = new(@"^(00_pad_start\.dat|zz_pad_end\.dat|00_lead_in_part1_filler\.dat|zz_lead_out_part2_filler\.dat|usb_filler_\d+\.dat|zz_bluray_128gb_chunk\d+_filler\.dat)$", RegexOptions.IgnoreCase);
        private static readonly Regex LegacyArtifactRx = new(@"^(TargetFill_Archival_Manifest\.txt|TargetFill_Manifest\.json|Verify_Disc\.bat|Verify_Disc\.ps1|TargetFill_Recovery.*\.par2|.+_DiscLabel\.png|.+_CaseInsert\.png)$", RegexOptions.IgnoreCase);
        private static readonly Regex CatalogRx = new(@"^(TargetFill_DiscCatalog\.csv|_DiscCatalog\.json)$", RegexOptions.IgnoreCase);
        private static readonly Regex ToolRx = new(@"^(TargetFill\.ps1|Launch_TargetFill\.bat|Launch\.bat|Install\.bat|Uninstall\.bat|par2\.exe)$", RegexOptions.IgnoreCase);
        private static readonly Regex DiscIsoRx = new(@"(_DISC\d+\.iso|_Disc_\d+\.iso)$", RegexOptions.IgnoreCase);
        private static readonly Regex DiscDirRx = new(@"^Disc_\d+$", RegexOptions.IgnoreCase);
        private static readonly Regex LabelPngRx = new(@"_(DiscLabel|CaseInsert)\.png$", RegexOptions.IgnoreCase);
        private static readonly string[] LegacyStealthRel =
        {
            @"System_Diagnostics\diag_buffer_part1.dat", @"System_Diagnostics\diag_buffer_single.dat",
            @"Cache_Archive\stream_cache_part2.bin", @"Cache_Archive\stream_cache_single.bin",
        };

        // USB/drive fill keeps TargetFill Pro's margin (a drive's file
        // system is already there, so this only leaves a little headroom).
        public static long SafetyMargin(int fileCount) => fileCount * 16384L + 16777216L;

        // Fixed space kept free on each disc when deciding which files go on
        // which disc (per-file space is added by PackDiscs).
        public static long IsoMargin(MediaPreset m) => m.Kind >= 4 ? 8L << 20 : 4L << 20;

        public static (long Head, long Tail) SplitBytes(long needed, Placement placement, int splitPercent)
        {
            if (needed <= 0) return (0, 0);
            if (placement == Placement.EndOnly) return (0, needed);
            if (placement == Placement.StartOnly) return (needed, 0);
            long head = (long)Math.Floor(needed * (Math.Clamp(splitPercent, 1, 99) / 100.0));
            return (head, needed - head);
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):N2} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):N2} MB";
            if (bytes >= 1L << 10) return $"{bytes / 1024.0:N2} KB";
            return $"{bytes} Bytes";
        }

        private static string Version => UpdateCheckService.CurrentVersionDisplay;

        public static string MasterCatalogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TargetFillPro", "TargetFill_DiscCatalog.csv");

        // Full path, no trailing separator (except a drive root like E:\).
        public static string NormalizePath(string path)
        {
            var p = Path.GetFullPath(path.Trim().Trim('"'));
            var root = Path.GetPathRoot(p);
            if (!string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) p = p.TrimEnd('\\', '/');
            return p;
        }

        private static bool Inside(string root, string path)
        {
            try
            {
                var r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string VolumeFor(string targetDir, Options o) =>
            !string.IsNullOrWhiteSpace(o.VolumeLabel) ? o.VolumeLabel.Trim() : new DirectoryInfo(targetDir).Name;

        public static string PrefixFor(string targetDir, Options o) =>
            Regex.Replace(VolumeFor(targetDir, o), "[^a-zA-Z0-9_]", "_");

        // ---------------------------------------------------------------
        // Tracker: which files in a folder TargetFill made
        // ---------------------------------------------------------------
        private static Dictionary<string, string> LoadTracker(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var p = Path.Combine(root, TrackerName);
            try
            {
                if (File.Exists(p))
                    foreach (var line in File.ReadAllLines(p))
                    {
                        int i = line.IndexOf('|');
                        if (i <= 0) continue;
                        var rel = line.Substring(i + 1);
                        // Never trust an entry that points outside this folder.
                        if (rel.Length == 0 || Path.IsPathRooted(rel) || !Inside(root, Path.Combine(root, rel))) continue;
                        map[rel] = line.Substring(0, i);
                    }
            }
            catch { /* unreadable tracker -- treat as empty */ }
            return map;
        }

        private static void SaveTracker(string root, Dictionary<string, string> map)
        {
            var p = Path.Combine(root, TrackerName);
            try
            {
                if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
                if (map.Count == 0)
                {
                    if (File.Exists(p)) File.Delete(p);
                    return;
                }
                File.WriteAllLines(p, map.Select(kv => $"{kv.Value}|{kv.Key}"), new UTF8Encoding(false));
                File.SetAttributes(p, FileAttributes.Hidden);
            }
            catch { /* best-effort */ }
        }

        private static string RelPath(string fullPath, string root)
        {
            var r = root.TrimEnd('\\', '/');
            if (fullPath.Length <= r.Length || !fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return "";
            return fullPath.Substring(r.Length).TrimStart('\\', '/').Replace('/', '\\');
        }

        private static void Track(string root, string fullPath, string kind)
        {
            var rel = RelPath(fullPath, root);
            if (rel.Length == 0) return;
            var map = LoadTracker(root);
            map[rel] = kind;
            SaveTracker(root, map);
        }

        private static void Untrack(string root, string fullPath)
        {
            var rel = RelPath(fullPath, root);
            if (rel.Length == 0) return;
            var map = LoadTracker(root);
            if (map.Remove(rel)) SaveTracker(root, map);
        }

        private static bool IsLegacyDiscDir(string dir)
        {
            try
            {
                // TargetFill Pro also accepts any *_DiscLabel.png; only its
                // own Disc_NN_DiscLabel.png name counts here, so a user's
                // folder with some other label image is never taken.
                return File.Exists(Path.Combine(dir, "00_lead_in_part1_filler.dat"))
                    || File.Exists(Path.Combine(dir, "zz_lead_out_part2_filler.dat"))
                    || File.Exists(Path.Combine(dir, Path.GetFileName(dir) + "_DiscLabel.png"));
            }
            catch { return false; }
        }

        // null = the user's own file (payload); otherwise what kind of
        // TargetFill item it is. Same rules as Get-ArtifactKind.
        private static string? KindOf(FileSystemInfo item, string root, Dictionary<string, string> map, bool portableToolHere)
        {
            var rel = RelPath(item.FullName, root);
            if (rel.Length == 0) return "system";
            if (rel.Equals(TrackerName, StringComparison.OrdinalIgnoreCase)) return "tracker";
            if (map.TryGetValue(rel, out var k)) return k;

            var parts = rel.Split('\\');
            var top = parts[0];
            if (top.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                || top.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || top.StartsWith(".targetfill_stage_", StringComparison.OrdinalIgnoreCase)) return "system";

            if (parts.Length > 1)
            {
                if (map.TryGetValue(top, out var tk) && tk == "discdir") return "discdir";
                if (DiscDirRx.IsMatch(top) && IsLegacyDiscDir(Path.Combine(root, top))) return "discdir";
                if (LegacyStealthRel.Contains(rel, StringComparer.OrdinalIgnoreCase)) return "filler";
                return null;
            }

            var name = top;
            if (item is DirectoryInfo)
                return DiscDirRx.IsMatch(name) && IsLegacyDiscDir(item.FullName) ? "discdir" : null;
            if (LegacyFillerRx.IsMatch(name)) return "filler";
            if (LegacyArtifactRx.IsMatch(name)) return "artifact";
            if (CatalogRx.IsMatch(name)) return "catalog";
            if (DiscIsoRx.IsMatch(name)) return "iso";
            if (portableToolHere && ToolRx.IsMatch(name)) return "tool";
            if (portableToolHere && (name.Equals("README.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("License.txt", StringComparison.OrdinalIgnoreCase))
                && IsTargetFillDoc(item.FullName)) return "tool";
            return null;
        }

        // A portable TargetFill Pro's own README/License (Test-IsTargetFillDoc).
        private static bool IsTargetFillDoc(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[4096];
                int n = fs.Read(buf, 0, buf.Length);
                var text = Encoding.UTF8.GetString(buf, 0, n);
                return text.Contains("TargetFill", StringComparison.Ordinal) || text.Contains("ArchivSect1986", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        public static PayloadStats ScanPayload(string root)
        {
            if (!Directory.Exists(root)) return new PayloadStats();
            var map = LoadTracker(root);
            bool tool = File.Exists(Path.Combine(root, "TargetFill.ps1"));
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(root, "*", opts))
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (KindOf(fi, root, map, tool) is not null) continue;
                    _ = fi.Length; // throws now if it vanished mid-scan
                    files.Add(fi);
                }
                catch { /* vanished or unreadable mid-scan -- skip it */ }
            }
            return StatsOf(files, root);
        }

        // Pre-flight scanner: paths of 250+ characters can fail in
        // Joliet/UDF images. Returns the first offending path, or null.
        public static string? PreflightProblem(PayloadStats stats) =>
            stats.Files.FirstOrDefault(f => f.FullName.Length >= 250)?.FullName;

        // Deletes TargetFill-generated fillers and artifacts (manifest,
        // verifier, labels, recovery data). Never touches the user's files,
        // ISOs or disc folders.
        public static int CleanFillers(string root, params string[] kinds)
        {
            if (kinds.Length == 0) kinds = new[] { "filler", "artifact" };
            int removed = 0;
            if (!Directory.Exists(root)) return 0;

            var map = LoadTracker(root);
            foreach (var rel in map.Keys.ToList())
            {
                if (!kinds.Contains(map[rel])) continue;
                var p = Path.Combine(root, rel);
                if (!Inside(root, p)) { map.Remove(rel); continue; }
                if (File.Exists(p))
                {
                    try { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); removed++; }
                    catch { continue; }
                }
                map.Remove(rel);
            }
            foreach (var rel in map.Keys.ToList())
            {
                if (map[rel] != "dir") continue;
                var p = Path.Combine(root, rel);
                try
                {
                    if (!Directory.Exists(p)) { map.Remove(rel); continue; }
                    if (!Directory.EnumerateFileSystemEntries(p).Any()) { Directory.Delete(p); map.Remove(rel); }
                }
                catch { /* leave it */ }
            }
            SaveTracker(root, map);

            // Untracked leftovers from older TargetFill versions (exact names, top level).
            bool tool = File.Exists(Path.Combine(root, "TargetFill.ps1"));
            try
            {
                foreach (var path in Directory.EnumerateFiles(root))
                {
                    var k = KindOf(new FileInfo(path), root, map, tool);
                    if (k is not null && kinds.Contains(k))
                    {
                        try { File.Delete(path); removed++; } catch { }
                    }
                }
            }
            catch { /* folder unreadable -- nothing more to do */ }
            if (kinds.Contains("filler"))
                foreach (var rel in LegacyStealthRel)
                {
                    var p = Path.Combine(root, rel);
                    if (File.Exists(p)) { try { File.Delete(p); removed++; } catch { } }
                }
            foreach (var d in new[] { "System_Diagnostics", "Cache_Archive" })
            {
                var p = Path.Combine(root, d);
                try { if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p); } catch { }
            }
            return removed;
        }

        // ---------------------------------------------------------------
        // File writers
        // ---------------------------------------------------------------
        private const uint FSCTL_SET_SPARSE = 0x000900C4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        private static void CreateSparseFile(string path, long size)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            // Ignored on FAT32/exFAT, where the file is simply allocated.
            DeviceIoControl(fs.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            fs.SetLength(size);
        }

        private static void CreateZeroFile(string path, long size)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 22);
            var buf = new byte[4 * 1024 * 1024];
            long rem = size;
            while (rem > 0)
            {
                int n = (int)Math.Min(buf.Length, rem);
                fs.Write(buf, 0, n);
                rem -= n;
            }
        }

        private static void CreateNoiseFile(string path, long size)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buf = new byte[1024 * 1024];
            long rem = size;
            while (rem > 0)
            {
                int n = (int)Math.Min(buf.Length, rem);
                RandomNumberGenerator.Fill(buf.AsSpan(0, n));
                fs.Write(buf, 0, n);
                rem -= n;
            }
        }

        private static void WriteFiller(string root, string relName, long size, bool noise, bool physical, bool track)
        {
            var path = Path.Combine(root, relName);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                if (track) Track(root, parent, "dir");
            }
            if (noise) CreateNoiseFile(path, size);
            else if (physical) CreateZeroFile(path, size);
            else CreateSparseFile(path, size);
            if (track) Track(root, path, "filler");
        }

        // Same names as Get-FillerNames.
        private static (string Head, string Tail) FillerNames(Placement placement, bool stealth, bool onDisc)
        {
            if (stealth)
                return placement == Placement.DualSplit
                    ? (@"System_Diagnostics\diag_buffer_part1.dat", @"Cache_Archive\stream_cache_part2.bin")
                    : (@"System_Diagnostics\diag_buffer_single.dat", @"Cache_Archive\stream_cache_single.bin");
            return onDisc ? ("00_lead_in_part1_filler.dat", "zz_lead_out_part2_filler.dat")
                          : ("00_pad_start.dat", "zz_pad_end.dat");
        }

        private static void WriteSplitFiller(string root, long needed, Options o, bool onDisc, bool track, Result r)
        {
            var (head, tail) = SplitBytes(needed, o.Placement, o.SplitPercent);
            var (headName, tailName) = FillerNames(o.Placement, o.Stealth, onDisc);
            if (head > 0) { WriteFiller(root, headName, head, o.Noise, o.Physical, track); if (track) r.Outputs.Add($"{headName} ({FormatBytes(head)})"); }
            if (tail > 0) { WriteFiller(root, tailName, tail, o.Noise, o.Physical, track); if (track) r.Outputs.Add($"{tailName} ({FormatBytes(tail)})"); }
        }

        // ---------------------------------------------------------------
        // Manifest, verifier, catalog
        // ---------------------------------------------------------------
        private static void WriteManifest(string workDir, IEnumerable<FileInfo> files, string volume, string mediaLabel, bool lba, bool track, Action<string>? log)
        {
            var sb = new StringBuilder();
            sb.Append($"TARGETFILL (ARCHIVSECTOR-OD v{Version}) - ARCHIVAL CHECKSUM MANIFEST\r\n");
            sb.Append($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n");
            sb.Append($"Volume Label: {volume}\r\n");
            sb.Append($"Media Type: {mediaLabel}\r\n");
            if (lba)
            {
                sb.Append("Nominal zone layout (approximate, depends on burning software):\r\n");
                sb.Append(" - Inner region (start filler): 25mm to 35mm radius\r\n");
                sb.Append(" - Data payload: 35mm to 52mm radius\r\n");
                sb.Append(" - Outer region (end filler): 52mm to 58mm radius\r\n");
            }
            sb.Append("--------------------------------------------------------\r\n");
            int n = 0;
            foreach (var f in files)
            {
                var rel = RelPath(f.FullName, workDir);
                try
                {
                    using var s = File.OpenRead(f.FullName);
                    sb.Append(Convert.ToHexString(SHA256.HashData(s))).Append(" * ").Append(rel).Append("\r\n");
                }
                catch
                {
                    sb.Append("[ERROR] Could not hash: ").Append(rel).Append("\r\n");
                }
                if (++n % 25 == 0) log?.Invoke($"Hashing… {n} file(s)");
            }
            var path = Path.Combine(workDir, ManifestName);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            if (track) Track(workDir, path, "artifact");
        }

        private static void WriteVerifier(string workDir, bool track)
        {
            var title = $"TargetFill (ArchivSector-OD v{Version}) - Standalone On-Disc SHA-256 Verifier";
            var ps1 = new[]
            {
                "$discDir = $PSScriptRoot",
                "if (-not $discDir) { $discDir = \".\" }",
                "$manifestPath = Join-Path $discDir \"TargetFill_Archival_Manifest.txt\"",
                "",
                "Write-Host \"========================================================================\" -ForegroundColor Cyan",
                $"Write-Host \"  {title}\" -ForegroundColor Cyan",
                "Write-Host \"========================================================================\" -ForegroundColor Cyan",
                "Write-Host \"\"",
                "Write-Host \"[*] Verifying disc root: $discDir\" -ForegroundColor Gray",
                "",
                "if (-not (Test-Path -LiteralPath $manifestPath)) {",
                "    Write-Host \"[ERROR] TargetFill_Archival_Manifest.txt not found on this disc!\" -ForegroundColor Red",
                "    Write-Host \"Please ensure Verify_Disc.bat and TargetFill_Archival_Manifest.txt are in the disc root.\" -ForegroundColor Yellow",
                "    Write-Host \"\"",
                "    exit 1",
                "}",
                "",
                "Write-Host \"[*] Manifest found: TargetFill_Archival_Manifest.txt\" -ForegroundColor Green",
                "Write-Host \"[*] Checking every file against its SHA-256 hash...\" -ForegroundColor Cyan",
                "Write-Host \"\"",
                "",
                "$passed = 0",
                "$failed = 0",
                "$missing = 0",
                "",
                "$lines = Get-Content -LiteralPath $manifestPath",
                "foreach ($line in $lines) {",
                "    $trimmed = $line.Trim()",
                "    if ($trimmed -match '^([A-Fa-f0-9]{64})\\s+\\*?\\s*(.+)$') {",
                "        $expectedHash = $matches[1].ToUpper()",
                "        $relPath = $matches[2].Trim()",
                "        $fullPath = Join-Path $discDir $relPath",
                "        if (Test-Path -LiteralPath $fullPath) {",
                "            try {",
                "                $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToUpper()",
                "                if ($actualHash -eq $expectedHash) {",
                "                    Write-Host \"[PASS]    \" -ForegroundColor Green -NoNewline",
                "                    Write-Host $relPath",
                "                    $passed++",
                "                } else {",
                "                    Write-Host \"[CORRUPT] \" -ForegroundColor Red -NoNewline",
                "                    Write-Host $relPath",
                "                    $failed++",
                "                }",
                "            } catch {",
                "                Write-Host \"[ERROR]   \" -ForegroundColor Red -NoNewline",
                "                Write-Host \"$relPath ($($_.Exception.Message))\"",
                "                $failed++",
                "            }",
                "        } else {",
                "            Write-Host \"[MISSING] \" -ForegroundColor Yellow -NoNewline",
                "            Write-Host $relPath",
                "            $missing++",
                "        }",
                "    }",
                "}",
                "",
                "Write-Host \"\"",
                "Write-Host \"------------------------------------------------------------------------\" -ForegroundColor Gray",
                "Write-Host \"Total Verified: $($passed + $failed + $missing) | Passed: $passed | Corrupted: $failed | Missing: $missing\" -ForegroundColor White",
                "",
                "if ($failed -eq 0 -and $missing -eq 0 -and $passed -gt 0) {",
                "    Write-Host \"[SUCCESS] Every file matches its SHA-256 hash.\" -ForegroundColor Green",
                "} elseif ($failed -gt 0 -or $missing -gt 0) {",
                "    Write-Host \"[WARNING] Problems found on this disc!\" -ForegroundColor Red",
                "    Write-Host \"\"",
                "    Write-Host \"  * Discs and ISOs are read-only; this check never changes them.\" -ForegroundColor Gray",
                "    $parFile = Join-Path $discDir \"TargetFill_Recovery.par2\"",
                "    if (Test-Path -LiteralPath $parFile) {",
                "        Write-Host \"[*] PAR2 recovery data found: TargetFill_Recovery.par2\" -ForegroundColor Cyan",
                "        Write-Host \"    1. Copy all readable files from this disc to a folder on your PC.\" -ForegroundColor Gray",
                "        Write-Host \"    2. Open that folder in MultiPar (or run: par2.exe r TargetFill_Recovery.par2).\" -ForegroundColor Gray",
                "        Write-Host \"    3. The damaged or missing files are rebuilt from the recovery data.\" -ForegroundColor Gray",
                "    } else {",
                "        Write-Host \"    Restore damaged or missing files from your original backup.\" -ForegroundColor Gray",
                "    }",
                "} else {",
                "    Write-Host \"[NOTICE] No file entries were found in the manifest.\" -ForegroundColor Yellow",
                "}",
            };
            var ps1Path = Path.Combine(workDir, "Verify_Disc.ps1");
            File.WriteAllLines(ps1Path, ps1, new UTF8Encoding(true));

            var bat = new[]
            {
                "@echo off",
                "setlocal EnableDelayedExpansion",
                $"title TargetFill (ArchivSector-OD v{Version}) - Standalone Disc Verifier",
                "color 0B",
                "echo ========================================================================",
                $"echo   {title}",
                "echo ========================================================================",
                "echo.",
                "cd /d \"%~dp0\"",
                "if not exist \"%~dp0Verify_Disc.ps1\" (",
                "    echo [ERROR] Verify_Disc.ps1 was not found on this disc!",
                "    echo.",
                "    pause",
                "    exit /b 1",
                ")",
                "powershell.exe -NoProfile -Sta -ExecutionPolicy Bypass -File \"%~dp0Verify_Disc.ps1\"",
                "if %ERRORLEVEL% neq 0 (",
                "    echo.",
                "    echo [NOTICE] Verification exited with code %ERRORLEVEL%.",
                ")",
                "echo.",
                "pause",
            };
            var batPath = Path.Combine(workDir, "Verify_Disc.bat");
            File.WriteAllLines(batPath, bat, Encoding.ASCII);
            if (track)
            {
                Track(workDir, ps1Path, "artifact");
                Track(workDir, batPath, "artifact");
            }
        }

        // Same CSV layout as the PowerShell version, and the same master
        // catalog file in %LOCALAPPDATA%\TargetFillPro, so both share it.
        private static void UpdateCatalog(string targetDir, string volume, string mediaLabel, long capacity, long payload, long filler, bool noise, bool parity)
        {
            try
            {
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var fillerType = noise ? "RandomData" : "SparseZero";
                const string header = "VolumeName,Timestamp,MediaType,CapacityBytes,PayloadBytes,FillerBytes,FillerType,Parity,SourceFolder";
                var line = $"\"{volume}\",\"{now}\",\"{mediaLabel}\",{capacity},{payload},{filler},{fillerType},{(parity ? "True" : "False")},\"{targetDir}\"";

                Directory.CreateDirectory(Path.GetDirectoryName(MasterCatalogPath)!);
                AppendCsv(MasterCatalogPath, header, line);

                var localCsv = Path.Combine(targetDir, "TargetFill_DiscCatalog.csv");
                AppendCsv(localCsv, header, line);
                var localJson = Path.Combine(targetDir, "_DiscCatalog.json");
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    VolumeName = volume, Timestamp = now, MediaType = mediaLabel,
                    CapacityBytes = capacity, PayloadBytes = payload, FillerBytes = filler,
                    FillerType = fillerType, Parity = parity,
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(localJson, json, new UTF8Encoding(true));
                Track(targetDir, localCsv, "catalog");
                Track(targetDir, localJson, "catalog");
            }
            catch { /* best-effort */ }
        }

        private static void AppendCsv(string path, string header, string line)
        {
            if (!File.Exists(path)) File.WriteAllText(path, header + "\r\n", new UTF8Encoding(true));
            File.AppendAllText(path, line + "\r\n", new UTF8Encoding(false));
        }

        // The catalog to open with "View Disc Library Catalog": the master
        // one, else this folder's own. null when neither exists yet.
        public static string? CatalogToOpen(string? targetDir)
        {
            if (File.Exists(MasterCatalogPath)) return MasterCatalogPath;
            if (!string.IsNullOrEmpty(targetDir))
            {
                var local = Path.Combine(targetDir, "TargetFill_DiscCatalog.csv");
                if (File.Exists(local)) return local;
            }
            return null;
        }

        private static void Chime(Options o)
        {
            if (!o.Sound) return;
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }

        // ---------------------------------------------------------------
        // PAR2 recovery data (real par2cmdline)
        // ---------------------------------------------------------------
        public static string? FindPar2()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "par2.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TargetFillPro", "par2.exe"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "par2.exe");
                    if (File.Exists(p)) return p;
                }
                catch { /* malformed PATH entry */ }
            }
            return null;
        }

        // Creates TargetFill_Recovery*.par2 in root covering the given files.
        private static (bool Ok, long Bytes, string Message) CreatePar2(string root, List<FileInfo> files, bool track)
        {
            var exe = FindPar2();
            if (exe is null)
                return (false, 0, "par2.exe wasn't found. Download par2cmdline (github.com/Parchive/par2cmdline/releases) and put par2.exe next to ArchivSector-OD.exe.");
            RemovePar2(root, track);
            if (files.Count == 0) return (false, 0, "There are no files to protect.");

            var basePath = Path.GetFullPath(root);
            if (basePath.EndsWith('\\')) basePath += "\\";
            var sb = new StringBuilder($"c -q -r{Par2RedundancyPct} -B\"{basePath}\" \"TargetFill_Recovery.par2\"");
            foreach (var f in files) sb.Append(" \"").Append(RelPath(f.FullName, root)).Append('"');
            if (sb.Length > 30000)
                return (false, 0, $"There are too many files ({files.Count}) to pass to par2 in one run. Zip small files together, or make recovery data with MultiPar.");

            int exit;
            string err;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = sb.ToString(),
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi)!;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                _ = outTask.Result;
                exit = proc.ExitCode;
            }
            catch (Exception ex)
            {
                return (false, 0, $"Couldn't run par2.exe: {ex.Message}");
            }

            var created = Directory.GetFiles(root, "TargetFill_Recovery*.par2");
            if (exit != 0 || created.Length == 0)
            {
                foreach (var c in created) { try { File.Delete(c); } catch { } }
                return (false, 0, $"par2 failed (exit code {exit}). {err}".Trim());
            }
            long bytes = 0;
            foreach (var c in created)
            {
                bytes += new FileInfo(c).Length;
                if (track) Track(root, c, "artifact");
            }
            return (true, bytes, $"PAR2 recovery data: {FormatBytes(bytes)} ({Par2RedundancyPct}% redundancy, {created.Length} files)");
        }

        private static void RemovePar2(string root, bool track)
        {
            foreach (var c in Directory.GetFiles(root, "TargetFill_Recovery*.par2"))
            {
                try { File.Delete(c); } catch { }
                if (track) Untrack(root, c);
            }
        }

        // Adds parity if asked and it fits; returns the filler still needed.
        private static long ApplyParity(string root, List<FileInfo> files, long needed, Options o, bool track, Result r, string prefix)
        {
            if (!o.Parity) return needed;
            var (ok, bytes, msg) = CreatePar2(root, files, track);
            if (!ok) { r.Notes.Add($"{prefix}Parity skipped: {msg}"); return needed; }
            if (bytes > needed)
            {
                RemovePar2(root, track);
                r.Notes.Add($"{prefix}Parity skipped: the recovery files ({FormatBytes(bytes)}) don't fit in the free space on this disc.");
                return needed;
            }
            r.Notes.Add(prefix + msg);
            return needed - bytes;
        }

        // ---------------------------------------------------------------
        // Generate Split Filler (pads the folder in place)
        // ---------------------------------------------------------------
        public static Result GenerateFiller(string targetDir, Options o, Action<string>? log = null)
        {
            var r = new Result();
            try
            {
                targetDir = NormalizePath(targetDir);
                if (!Directory.Exists(targetDir)) { r.Message = "That folder doesn't exist."; return r; }
                CleanFillers(targetDir);
                var stats = ScanPayload(targetDir);
                if (o.Preflight && PreflightProblem(stats) is string longPath)
                {
                    r.Message = $"Pre-flight scanner: this path is 250+ characters and may fail on a disc:\n{longPath}\n\nShorten the folder or file names and try again.";
                    return r;
                }

                if (o.Media.IsUsb) return UsbFill(targetDir, o, stats, log);

                long needed = o.Media.Bytes - stats.Bytes - Overhead(stats, o.Media);
                if (needed < 0)
                {
                    r.TooBig = true;
                    r.Message = $"This folder is {FormatBytes(-needed)} too big for one {o.Media.Label}. " +
                                "Turn on Auto-Span in Expert Mode to split it across several discs, or pick bigger media.";
                    return r;
                }

                var (head, tail) = SplitBytes(needed, o.Placement, o.SplitPercent);
                if (FatProblem(targetDir, Math.Max(head, tail)) is string fat) { r.Message = fat; return r; }

                long beforeParity = needed;
                needed = ApplyParity(targetDir, stats.Files, needed, o, track: true, r, "");
                bool parityAdded = needed != beforeParity;

                log?.Invoke(o.Noise ? "Writing random-data filler (this takes a while)…" : "Creating sparse filler files…");
                // Physical (real zero blocks) is a USB setting, as in TargetFill Pro.
                WriteSplitFiller(targetDir, needed, CloneForIso(o), onDisc: false, track: true, r);

                var volume = VolumeFor(targetDir, o);
                if (o.Manifest || o.Verifier) { log?.Invoke("Writing SHA-256 manifest…"); WriteManifest(targetDir, stats.Files, volume, o.Media.Label, o.LbaReport, true, log); }
                if (o.Verifier) WriteVerifier(targetDir, true);
                if (o.Catalog) UpdateCatalog(targetDir, volume, o.Media.Label, o.Media.Bytes, stats.Bytes, needed, o.Noise, parityAdded);
                Chime(o);

                r.Ok = true;
                r.Message = needed <= 0
                    ? $"No filler needed: the folder already fills the {o.Media.Label}."
                    : $"Filler created for {o.Media.Label}: {FormatBytes(needed)} of padding. Burn this folder and the disc will be 100% full.";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = $"Something went wrong: {ex.Message}";
                return r;
            }
        }

        private static Result UsbFill(string targetDir, Options o, PayloadStats stats, Action<string>? log)
        {
            var r = new Result();
            var root = Path.GetPathRoot(targetDir)!;
            long free = new DriveInfo(root).AvailableFreeSpace - 5242880;
            long capTarget = o.Media.Bytes - stats.Bytes - SafetyMargin(stats.Count);
            long needed = Math.Min(free, capTarget);
            if (needed <= 0) { r.Message = $"Nothing to fill: the {o.Media.Label} target is already reached or the drive is full."; return r; }

            const long chunkMax = 4294900000L; // stays under the FAT32 4 GB file limit
            int i = 1;
            long remaining = needed;
            while (remaining > 0)
            {
                long chunk = Math.Min(remaining, chunkMax);
                log?.Invoke($"Writing usb_filler_{i}.dat ({FormatBytes(chunk)})…");
                WriteFiller(targetDir, $"usb_filler_{i}.dat", chunk, noise: false, o.Physical, track: true);
                remaining -= chunk;
                i++;
            }
            Chime(o);
            r.Ok = true;
            r.Outputs.Add($"{i - 1} filler file(s), {FormatBytes(needed)}");
            r.Message = $"Created {i - 1} filler file(s), {FormatBytes(needed)} total. " +
                        (free < capTarget ? $"Limited by free space on {root}." : $"Filled up to the {o.Media.Label} preset.");
            return r;
        }

        // ---------------------------------------------------------------
        // Disc packing and Disc_NN folders
        // ---------------------------------------------------------------
        // Splits the user's files into disc-sized groups, in folder order
        // (whole files only). The screen uses this too, so the disc count
        // it shows is the number that gets built.
        public static List<(List<FileInfo> Files, long Size)> PackDiscs(PayloadStats stats, long usablePerDisc)
        {
            var bins = new List<(List<FileInfo> Files, long Size)>();
            var cur = new List<FileInfo>(); long curSize = 0;
            long curCost = 0;
            foreach (var f in stats.Files.OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                // What the file really takes on a disc: whole sectors plus
                // its file-system entry and manifest line.
                long cost = RoundUp(f.Length) + 3 * Sector;
                if (curCost + cost > usablePerDisc && cur.Count > 0) { bins.Add((cur, curSize)); cur = new List<FileInfo>(); curSize = 0; curCost = 0; }
                cur.Add(f); curSize += f.Length; curCost += cost;
            }
            if (cur.Count > 0 || bins.Count == 0) bins.Add((cur, curSize));
            return bins;
        }

        // Disc_NN folders TargetFill made in an earlier Auto-Span run.
        public static List<string> PreviousDiscFolders(string targetDir)
        {
            var list = new List<string>();
            var map = LoadTracker(targetDir);
            foreach (var kv in map) if (kv.Value == "discdir") list.Add(kv.Key);
            try
            {
                foreach (var d in Directory.GetDirectories(targetDir))
                {
                    var name = Path.GetFileName(d);
                    if (DiscDirRx.IsMatch(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase) && IsLegacyDiscDir(d)) list.Add(name);
                }
            }
            catch { }
            return list;
        }

        // All Disc_NN folders in the folder (for "one ISO per disc folder").
        public static List<string> DiscFolders(string targetDir)
        {
            try
            {
                return Directory.GetDirectories(targetDir)
                    .Where(d => DiscDirRx.IsMatch(Path.GetFileName(d)))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }

        // Auto-Span, folder mode: copies the files into Disc_01, Disc_02…
        // each padded to full, with manifest, verifier and label. Disc
        // folders from a previous run are replaced (the panel asks first).
        public static Result AutoSpanFolders(string targetDir, Options o, Action<string>? log = null)
        {
            var r = new Result();
            try
            {
                targetDir = NormalizePath(targetDir);
                var stats = ScanPayload(targetDir);
                long max = o.Media.Bytes;
                long usable = max - IsoMargin(o.Media);
                var oversized = stats.Files.FirstOrDefault(f => f.Length > usable);
                if (oversized is not null)
                {
                    r.Message = $"Can't span: '{oversized.Name}' ({FormatBytes(oversized.Length)}) is bigger than one whole {o.Media.Label}. Pick bigger media.";
                    return r;
                }
                if (FatProblem(targetDir, max) is string fat) { r.Message = fat; return r; }

                foreach (var old in PreviousDiscFolders(targetDir))
                {
                    var p = Path.Combine(targetDir, old);
                    if (!Inside(targetDir, p) || !DiscDirRx.IsMatch(Path.GetFileName(p))) continue;
                    try { if (Directory.Exists(p)) Directory.Delete(p, recursive: true); } catch { }
                    Untrack(targetDir, p);
                }

                var bins = PackDiscs(stats, usable);
                int discs = bins.Count;
                var prefix = PrefixFor(targetDir, o);
                for (int d = 0; d < discs; d++)
                {
                    int num = d + 1;
                    var sub = $"Disc_{num:D2}";
                    var subPath = Path.Combine(targetDir, sub);
                    if (Directory.Exists(subPath))
                    {
                        r.Message = $"A folder named {sub} already exists and wasn't made by TargetFill. Rename or move it, then try again.";
                        return r;
                    }
                    log?.Invoke($"Disc {num} of {discs}: copying files…");
                    Directory.CreateDirectory(subPath);
                    Track(targetDir, subPath, "discdir");
                    foreach (var f in bins[d].Files)
                    {
                        var dest = Path.Combine(subPath, RelPath(f.FullName, targetDir));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(f.FullName, dest, overwrite: true);
                    }
                    var copied = bins[d].Files.Select(f => new FileInfo(Path.Combine(subPath, RelPath(f.FullName, targetDir)))).ToList();

                    // Like TargetFill Pro: disc folders get plain sparse
                    // filler (no parity, random data or stealth names).
                    long needed = Math.Max(0, max - bins[d].Size - Overhead(StatsOf(bins[d].Files, targetDir), o.Media));
                    if (o.Manifest || o.Verifier) WriteManifest(subPath, copied, $"{prefix}_D{num}", o.Media.Label, o.LbaReport, false, log);
                    var plain = new Options { Media = o.Media, Placement = o.Placement, SplitPercent = o.SplitPercent };
                    WriteSplitFiller(subPath, needed, plain, onDisc: true, track: false, r);
                    if (o.Verifier) WriteVerifier(subPath, false);
                    var (head, _) = SplitBytes(needed, o.Placement, o.SplitPercent);
                    ExportDiscLabel(Path.Combine(subPath, $"{sub}_DiscLabel.png"), o.Media, head, bins[d].Size, num, discs);
                    r.Outputs.Add($"{sub} ({bins[d].Files.Count} file(s), {FormatBytes(bins[d].Size)})");
                }
                Chime(o);
                r.Ok = true;
                r.Message = $"Copied your files into {discs} padded disc folders (Disc_01…). Your original files weren't changed. " +
                            "Click Generate .ISO Image to turn each folder into an ISO.";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = $"Auto-span failed: {ex.Message}";
                return r;
            }
        }

        // ---------------------------------------------------------------
        // Generate .ISO Image
        // ---------------------------------------------------------------
        // Each disc is staged in a hidden folder next to the source, with
        // the user's files hard-linked in (instant, no extra space) or
        // copied when hard links aren't possible (FAT/exFAT, network
        // drives). Parity, manifest, filler and verifier are added to the
        // stage, then Windows' built-in IMAPI2 builds the .iso into the
        // source folder, plus a disc label PNG. The user's files are never
        // changed.
        public static Result BuildIso(string targetDir, Options o, Action<string>? log = null)
        {
            var r = new Result();
            try
            {
                targetDir = NormalizePath(targetDir);
                if (!Directory.Exists(targetDir)) { r.Message = "That folder doesn't exist."; return r; }
                if (o.Media.IsUsb) { r.Message = "ISO images are for discs -- pick a CD, DVD or Blu-ray size."; return r; }

                CleanFillers(targetDir);
                var stats = ScanPayload(targetDir);
                if (o.Preflight && PreflightProblem(stats) is string longPath)
                {
                    r.Message = $"Pre-flight scanner: this path is 250+ characters and may fail on a disc:\n{longPath}\n\nShorten the folder or file names and try again.";
                    return r;
                }

                long max = o.Media.Bytes;
                long fsMargin = IsoMargin(o.Media);
                long usable = Math.Max(1, max - fsMargin);
                if (FatProblem(targetDir, max) is string fat) { r.Message = fat; return r; }

                var oversized = stats.Files.FirstOrDefault(f => f.Length > usable);
                if (oversized is not null)
                {
                    r.Message = $"Can't build: '{oversized.Name}' ({FormatBytes(oversized.Length)}) is bigger than one whole {o.Media.Label}. Pick bigger media.";
                    return r;
                }

                var bins = PackDiscs(stats, usable);
                int discs = bins.Count;
                if (discs > 1) log?.Invoke($"This needs {discs} × {o.Media.Label} -- building {discs} ISO images.");

                var prefix = PrefixFor(targetDir, o);
                var parent = Directory.GetParent(targetDir)?.FullName ?? targetDir;
                // The stage is created in a sibling folder so hard links
                // work (same drive). Untracked files named like the stage
                // are skipped by ScanPayload, so a leftover never counts.

                for (int d = 0; d < discs; d++)
                {
                    int num = d + 1;
                    var suffix = discs > 1 ? $"_DISC{num}" : "";
                    var discVol = discs > 1 ? $"{prefix}_D{num}" : prefix;
                    var isoPath = SafeIsoPath(targetDir, Path.Combine(targetDir, $"{prefix}{suffix}.iso"));
                    var pngPath = Path.Combine(targetDir, $"{prefix}{suffix}_DiscLabel.png");
                    var readOnlyLinks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var stage = CreateStage(parent, targetDir);
                    try
                    {
                        log?.Invoke(discs > 1 ? $"Disc {num}: gathering files…" : "Gathering files…");
                        StageFiles(bins[d].Files, targetDir, stage, readOnlyLinks, log);
                        var staged = bins[d].Files.Select(f => new FileInfo(Path.Combine(stage, RelPath(f.FullName, targetDir)))).ToList();

                        long needed = Math.Max(0, max - bins[d].Size - Overhead(StatsOf(bins[d].Files, targetDir), o.Media));
                        long beforeParity = needed;
                        needed = ApplyParity(stage, staged, needed, o, track: false, r, discs > 1 ? $"Disc {num}: " : "");
                        bool parityAdded = needed != beforeParity;
                        if (o.Manifest || o.Verifier) { log?.Invoke("Writing SHA-256 manifest…"); WriteManifest(stage, staged, discVol, o.Media.Label, o.LbaReport, false, log); }
                        var isoOptions = CloneForIso(o);
                        WriteSplitFiller(stage, needed, isoOptions, onDisc: true, track: false, r);
                        if (o.Verifier) WriteVerifier(stage, false);
                        if (o.Catalog) UpdateCatalog(targetDir, discVol, o.Media.Label, max, bins[d].Size, Math.Max(0, needed), o.Noise, parityAdded);

                        log?.Invoke(discs > 1 ? $"Disc {num}: building {Path.GetFileName(isoPath)}…" : $"Building {Path.GetFileName(isoPath)}…");
                        // IMAPI2 measures the real image; the filler is then
                        // grown or trimmed so the disc is full to the sector.
                        var (fillHead, fillTail) = FillerNames(isoOptions.Placement, isoOptions.Stealth, onDisc: true);
                        bool big = bins[d].Files.Any(f => f.Length >= 2147483648L);
                        RunSta(() => WriteIso(stage, isoPath, o.Media, discVol, max, big,
                            delta => FitFiller(stage, fillHead, fillTail, delta, isoOptions.Noise)));
                        Track(targetDir, isoPath, "iso");

                        var (head, _) = SplitBytes(needed, o.Placement, o.SplitPercent);
                        ExportDiscLabel(pngPath, o.Media, head, bins[d].Size, num, discs);
                        Track(targetDir, pngPath, "artifact");
                        r.Outputs.Add($"{Path.GetFileName(isoPath)} ({FormatBytes(new FileInfo(isoPath).Length)})");
                    }
                    finally
                    {
                        DeleteStage(stage, readOnlyLinks, log);
                    }
                }

                Chime(o);
                r.Ok = true;
                r.Message = discs > 1
                    ? $"Built {discs} ISO images and disc labels for {o.Media.Label}, each filled to 100%. Your original files weren't changed."
                    : $"Built the ISO and disc label for {o.Media.Label}, filled to 100% with your data in the middle tracks. Your original files weren't changed.";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = $"ISO build failed: {ex.Message}";
                return r;
            }
        }

        // Inside an ISO the filler is always sparse or random (Physical is a
        // USB-only setting).
        private static Options CloneForIso(Options o) => new()
        {
            Media = o.Media, Placement = o.Placement, SplitPercent = o.SplitPercent,
            Physical = false, Noise = o.Noise, Stealth = o.Stealth,
        };

        // "One ISO per folder" for Disc_01, Disc_02… made by Auto-Span.
        public static Result BuildIsoFromDiscFolders(string targetDir, Options o, Action<string>? log = null)
        {
            var r = new Result();
            try
            {
                targetDir = NormalizePath(targetDir);
                var folders = DiscFolders(targetDir);
                if (folders.Count == 0) { r.Message = "There are no Disc_NN folders here."; return r; }
                if (FatProblem(targetDir, o.Media.Bytes) is string fat) { r.Message = fat; return r; }
                var prefix = PrefixFor(targetDir, o);
                for (int d = 0; d < folders.Count; d++)
                {
                    var name = Path.GetFileName(folders[d]);
                    var iso = SafeIsoPath(targetDir, Path.Combine(targetDir, $"{prefix}_{name}.iso"));
                    log?.Invoke($"Building {Path.GetFileName(iso)} ({d + 1} of {folders.Count})…");
                    bool big = Directory.EnumerateFiles(folders[d], "*", SearchOption.AllDirectories).Any(f => new FileInfo(f).Length >= 2147483648L);
                    var discDir = folders[d];
                    // Disc folders made by Auto-Span have TargetFill's own
                    // filler; it's grown or trimmed to fill the disc exactly.
                    bool hasFiller = File.Exists(Path.Combine(discDir, "00_lead_in_part1_filler.dat")) || File.Exists(Path.Combine(discDir, "zz_lead_out_part2_filler.dat"));
                    Func<long, bool>? fit = hasFiller
                        ? delta => FitFiller(discDir, "00_lead_in_part1_filler.dat", "zz_lead_out_part2_filler.dat", delta, noise: false)
                        : null;
                    RunSta(() => WriteIso(discDir, iso, o.Media, $"{prefix}_D{d + 1}", o.Media.Bytes, big, fit));
                    Track(targetDir, iso, "iso");
                    var png = Path.Combine(targetDir, $"{prefix}_{name}_DiscLabel.png");
                    long payload = ScanPayload(folders[d]).Bytes;
                    ExportDiscLabel(png, o.Media, 0, payload, d + 1, folders.Count);
                    Track(targetDir, png, "artifact");
                    r.Outputs.Add($"{Path.GetFileName(iso)} ({FormatBytes(new FileInfo(iso).Length)})");
                }
                Chime(o);
                r.Ok = true;
                r.Message = $"Created {folders.Count} ISO image(s) and disc label(s), one per disc folder.";
                return r;
            }
            catch (Exception ex)
            {
                r.Message = $"ISO build failed: {ex.Message}";
                return r;
            }
        }

        // Never overwrite an .iso the user put in the folder (a rip with the
        // same name, say): only one TargetFill made itself is replaced.
        private static string SafeIsoPath(string targetDir, string isoPath)
        {
            if (!File.Exists(isoPath)) return isoPath;
            var map = LoadTracker(targetDir);
            if (map.TryGetValue(RelPath(isoPath, targetDir), out var k) && k == "iso") return isoPath;
            var alt = Path.Combine(Path.GetDirectoryName(isoPath)!, Path.GetFileNameWithoutExtension(isoPath) + "_TargetFill.iso");
            int n = 2;
            while (File.Exists(alt) && !(map.TryGetValue(RelPath(alt, targetDir), out var k2) && k2 == "iso"))
                alt = Path.Combine(Path.GetDirectoryName(isoPath)!, Path.GetFileNameWithoutExtension(isoPath) + $"_TargetFill_{n++}.iso");
            return alt;
        }

        // Hidden staging folder next to the source (same drive, so hard
        // links work), or inside it when the parent can't be written to.
        private static string CreateStage(string parent, string targetDir)
        {
            foreach (var dir in new[] { parent, targetDir })
            {
                var stage = Path.Combine(dir, $".targetfill_stage_{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(stage);
                    try { File.SetAttributes(stage, FileAttributes.Directory | FileAttributes.Hidden); } catch { }
                    return stage;
                }
                catch { /* try the next place */ }
            }
            throw new IOException("Couldn't create a temporary folder next to (or inside) the source folder.");
        }

        // FAT32 can't hold a file of 4 GB or more, so a DVD/Blu-ray ISO (or
        // its filler) can't be made on a FAT32 drive. Says so up front.
        private static string? FatProblem(string folder, long bytesNeeded)
        {
            try
            {
                var fmt = new DriveInfo(Path.GetPathRoot(folder)!).DriveFormat;
                if (fmt.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && bytesNeeded >= 4294967296L)
                    return "This folder is on a FAT32 drive, which can't hold files of 4 GB or more, so a disc this size can't be built here. " +
                           "Move the folder to an NTFS drive (most internal drives are) and try again.";
            }
            catch { /* unknown format -- let the build try */ }
            return null;
        }

        private static void StageFiles(List<FileInfo> files, string sourceRoot, string stage, Dictionary<string, string> readOnlyLinks, Action<string>? log)
        {
            bool linkOk = true;
            int n = 0;
            foreach (var f in files)
            {
                var dest = Path.Combine(stage, RelPath(f.FullName, sourceRoot));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (linkOk && CreateHardLink(dest, f.FullName, IntPtr.Zero))
                {
                    if (f.IsReadOnly) readOnlyLinks[dest] = f.FullName;
                    n++;
                    continue;
                }
                if (linkOk) { linkOk = false; log?.Invoke("Hard links aren't available here -- copying files instead (slower)…"); }
                File.Copy(f.FullName, dest, overwrite: true);
                if (new FileInfo(dest).IsReadOnly) File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly); // a copy, safe to change
                if (++n % 25 == 0) log?.Invoke($"Copied {n} of {files.Count} file(s)…");
            }
        }

        // Removes a staging folder. A hard link shares its attributes with
        // the user's original file, and .NET won't delete a read-only file,
        // so for each read-only staged link: clear the flag (this clears it
        // on the original too), delete the link, then put the flag back on
        // the original. readOnlyLinks maps staged path -> original path.
        private static void DeleteStage(string stage, Dictionary<string, string> readOnlyLinks, Action<string>? log)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(stage)) return;
                    foreach (var (staged, original) in readOnlyLinks.ToList())
                    {
                        if (!File.Exists(staged)) { readOnlyLinks.Remove(staged); continue; }
                        File.SetAttributes(staged, File.GetAttributes(staged) & ~FileAttributes.ReadOnly);
                        try { File.Delete(staged); readOnlyLinks.Remove(staged); }
                        finally
                        {
                            try { File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.ReadOnly); } catch { }
                        }
                    }
                    Directory.Delete(stage, recursive: true);
                    return;
                }
                catch
                {
                    Thread.Sleep(500); // IMAPI2 may still be letting go of a file
                }
            }
            log?.Invoke($"Note: couldn't remove the temporary folder {stage} -- it's safe to delete by hand.");
        }

        // IMAPI2 settings follow the PowerShell version: UDF only (2.60
        // for Blu-ray, 1.02 for DVD) when a file is 2 GB+ or the media
        // isn't a CD; ISO9660 + Joliet + UDF otherwise.
        //
        // fit: when given, IMAPI2's real image size is checked first, and
        // fit(bytes) grows (positive) or trims (negative) the filler by that
        // much, then the image is measured again -- so the finished ISO is
        // the disc's exact size with no empty space left. Returns false if
        // the filler can't absorb the change.
        private static void WriteIso(string sourceDir, string isoPath, MediaPreset media, string volume, long maxBytes, bool hasLargeFile, Func<long, bool>? fit = null)
        {
            long maxBlocks = maxBytes / Sector;
            for (int attempt = 0; ; attempt++)
            {
                var (fsi, resultObj, totalBlocks) = CreateImage(sourceDir, media, volume, hasLargeFile);
                try
                {
                    if (fit is not null && totalBlocks != maxBlocks && attempt < 6)
                    {
                        long delta = (maxBlocks - totalBlocks) * Sector;
                        // Let go of the staged files before resizing the filler.
                        Release(resultObj);
                        Release(fsi);
                        resultObj = null; fsi = null;
                        if (fit(delta)) continue;
                        (fsi, resultObj, totalBlocks) = CreateImage(sourceDir, media, volume, hasLargeFile);
                    }
                    if (totalBlocks > maxBlocks)
                        throw new InvalidOperationException(
                            $"The image came out {FormatBytes((totalBlocks - maxBlocks) * Sector)} bigger than the disc. Pick bigger media or remove some files.");
                    SaveImage(resultObj!, isoPath, maxBytes);
                    return;
                }
                finally
                {
                    Release(resultObj);
                    Release(fsi);
                }
            }
        }

        private static (object? Fsi, object? Result, long TotalBlocks) CreateImage(string sourceDir, MediaPreset media, string volume, bool hasLargeFile)
        {
            var t = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage")
                ?? throw new InvalidOperationException("Windows' disc image builder (IMAPI2) isn't available on this PC.");
            dynamic fsi = Activator.CreateInstance(t)!;
            try
            {
                if (hasLargeFile || media.Kind > 1)
                {
                    if (media.Kind >= 4) { TryCom(() => fsi.ChooseImageDefaultsForMediaType(18)); TryCom(() => fsi.UDFRevision = 0x260); }
                    else if (media.Kind == 3) { TryCom(() => fsi.ChooseImageDefaultsForMediaType(8)); TryCom(() => fsi.UDFRevision = 0x102); }
                    else { TryCom(() => fsi.ChooseImageDefaultsForMediaType(6)); TryCom(() => fsi.UDFRevision = 0x102); }
                    fsi.FileSystemsToCreate = 4;
                }
                else
                {
                    TryCom(() => fsi.ChooseImageDefaultsForMediaType(2));
                    fsi.FileSystemsToCreate = 7;
                }
                // Lets IMAPI2 accept content that fills the whole disc.
                TryCom(() => fsi.FreeMediaBlocks = 0);

                var clean = Regex.Replace(string.IsNullOrWhiteSpace(volume) ? "TARGETFILL" : volume, "[^a-zA-Z0-9_]", "_");
                if (clean.Length > 32) clean = clean.Substring(0, 32);
                fsi.VolumeName = clean;
                fsi.Root.AddTree(sourceDir, false);
                dynamic result = fsi.CreateResultImage();
                long total = Convert.ToInt64(result.TotalBlocks);
                return ((object)fsi, (object)result, total);
            }
            catch
            {
                Release((object)fsi);
                throw;
            }
        }

        private static void SaveImage(object resultObj, string isoPath, long maxBytes)
        {
            dynamic result = resultObj;
            IStream? stream = null;
            var temp = isoPath + ".partial";
            try
            {
                object streamObj = result.ImageStream;
                stream = (IStream)streamObj;
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buf = new byte[1 << 20];
                    var readPtr = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        while (true)
                        {
                            stream.Read(buf, buf.Length, readPtr);
                            int read = Marshal.ReadInt32(readPtr);
                            if (read <= 0) break;
                            fs.Write(buf, 0, read);
                        }
                    }
                    finally { Marshal.FreeHGlobal(readPtr); }
                    // Pad the image to the exact disc size, like the PowerShell version.
                    if (fs.Length < maxBytes) fs.SetLength(maxBytes);
                }
                File.Move(temp, isoPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
            finally
            {
                // Release IMAPI2's stream so it lets go of the staged files.
                if (stream is not null) Release(stream);
            }
        }

        private static void Release(object? com)
        {
            if (com is null) return;
            try { Marshal.FinalReleaseComObject(com); } catch { }
        }

        // Grows (delta > 0) or trims (delta < 0) the filler in dir by delta
        // bytes, in whole sectors: the end filler first, then the start
        // filler. A filler trimmed to nothing is deleted. Returns false when
        // the filler can't absorb it (the files alone are too big).
        private static bool FitFiller(string dir, string headRel, string tailRel, long delta, bool noise)
        {
            if (delta == 0) return true;
            var tail = Path.Combine(dir, tailRel);
            var head = Path.Combine(dir, headRel);
            if (delta > 0)
            {
                var target = File.Exists(tail) ? tail : File.Exists(head) ? head : tail;
                long cur = File.Exists(target) ? new FileInfo(target).Length : 0;
                ResizeFiller(target, RoundUp(cur) + delta, noise);
                return true;
            }
            long remaining = -delta;
            foreach (var path in new[] { tail, head })
            {
                if (remaining <= 0) break;
                if (!File.Exists(path)) continue;
                long cur = RoundUp(new FileInfo(path).Length);
                long cut = Math.Min(cur, remaining);
                ResizeFiller(path, cur - cut, noise);
                remaining -= cut;
            }
            return remaining <= 0;
        }

        private static void ResizeFiller(string path, long newSize, bool noise)
        {
            if (newSize <= 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            if (!File.Exists(path))
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (noise) CreateNoiseFile(path, newSize); else CreateSparseFile(path, newSize);
                return;
            }
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            long cur = fs.Length;
            if (newSize <= cur || !noise)
            {
                fs.SetLength(newSize); // a sparse file stays sparse when it grows
                return;
            }
            fs.Seek(0, SeekOrigin.End);
            var buf = new byte[1024 * 1024];
            long rem = newSize - cur;
            while (rem > 0)
            {
                int n = (int)Math.Min(buf.Length, rem);
                RandomNumberGenerator.Fill(buf.AsSpan(0, n));
                fs.Write(buf, 0, n);
                rem -= n;
            }
        }

        private static void TryCom(Action a)
        {
            try { a(); } catch { /* setting not supported on this Windows version -- skip */ }
        }

        // IMAPI2 and WPF drawing both behave best on an STA thread.
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

        // ---------------------------------------------------------------
        // Disc label and case inlay art (PNG), drawn like TargetFill Pro's
        // ---------------------------------------------------------------
        private static Media.Color C(byte r, byte g, byte b, byte a = 255) => Media.Color.FromArgb(a, r, g, b);
        private static Media.SolidColorBrush B(Media.Color c) { var b = new Media.SolidColorBrush(c); b.Freeze(); return b; }

        private static readonly Media.Color HeadColor = C(6, 182, 212);     // cyan: start filler
        private static readonly Media.Color DataColor = C(34, 197, 94);     // green: your data
        private static readonly Media.Color TailColor = C(147, 51, 234);    // purple: end filler

        private static Media.FormattedText Text(string s, double pt, bool bold, Media.Color color) =>
            new(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Media.Typeface(new Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                    bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal),
                pt * 96.0 / 72.0, B(color), 1.0);

        // The radial disc (or USB bar) at 250x250, as in the PowerShell
        // version's preview canvas; headBytes/payload decide the rings.
        private static void DrawDisc(Media.DrawingContext dc, MediaPreset media, long headBytes, long payload)
        {
            dc.DrawRectangle(B(C(15, 23, 42)), null, new System.Windows.Rect(0, 0, 250, 250));
            double cap = Math.Max(1, media.Bytes);
            double headFrac = Math.Clamp(headBytes / cap, 0, 1);
            double payFrac = Math.Clamp(payload / cap, 0, 1);
            var rim = new Media.Pen(B(C(51, 65, 85)), 2);

            if (media.IsUsb)
            {
                double w = 200, h = 60, x = 25, y = 95;
                dc.DrawRectangle(B(C(148, 163, 184)), null, new System.Windows.Rect(x - 20, y + 15, 20, 30));
                double wHead = w * headFrac, wPay = w * payFrac, wTail = Math.Max(0, w - wHead - wPay);
                if (wHead > 0) dc.DrawRectangle(B(HeadColor), null, new System.Windows.Rect(x, y, wHead, h));
                if (wPay > 0) dc.DrawRectangle(B(DataColor), null, new System.Windows.Rect(x + wHead, y, wPay, h));
                if (wTail > 0) dc.DrawRectangle(B(TailColor), null, new System.Windows.Rect(x + wHead + wPay, y, wTail, h));
                dc.DrawRectangle(null, rim, new System.Windows.Rect(x, y, w, h));
                return;
            }

            var center = new System.Windows.Point(125, 125);
            double maxR = 115, hubR = 30, r0 = hubR;
            double area = maxR * maxR - r0 * r0;
            double r1 = Math.Sqrt(r0 * r0 + headFrac * area);
            double r2 = Math.Min(maxR, Math.Sqrt(r1 * r1 + payFrac * area));
            dc.DrawEllipse(null, rim, center, maxR + 2, maxR + 2);
            dc.DrawEllipse(B(TailColor), null, center, maxR, maxR);
            if (r2 > r1) dc.DrawEllipse(B(DataColor), null, center, r2, r2);
            if (r1 > r0) dc.DrawEllipse(B(HeadColor), null, center, r1, r1);
            dc.DrawEllipse(B(C(30, 41, 59)), rim, center, hubR, hubR);
            dc.DrawEllipse(B(C(11, 15, 25)), null, center, 10, 10);
            var hub = Text("HUB", 8, true, C(148, 163, 184));
            dc.DrawText(hub, new System.Windows.Point(125 - hub.Width / 2, 125 - hub.Height / 2));
        }

        private static void SavePng(Media.DrawingVisual visual, int w, int h, string path)
        {
            var bmp = new Imaging.RenderTargetBitmap(w, h, 96, 96, Media.PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new Imaging.PngBitmapEncoder();
            enc.Frames.Add(Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        // 250x250 disc label; "DISC n OF N" badge for multi-disc sets.
        public static void ExportDiscLabel(string path, MediaPreset media, long headBytes, long payload, int discNum = 0, int totalDiscs = 0)
        {
            try
            {
                RunSta(() =>
                {
                  try
                  {
                    var v = new Media.DrawingVisual();
                    using (var dc = v.RenderOpen())
                    {
                        DrawDisc(dc, media, headBytes, payload);
                        if (discNum > 0 && totalDiscs > 1)
                        {
                            var rect = new System.Windows.Rect(45, 218, 160, 22);
                            dc.DrawRectangle(B(C(15, 23, 42, 230)), new Media.Pen(B(C(51, 65, 85)), 1), rect);
                            var t = Text($"DISC {discNum} OF {totalDiscs}", 8, true, DataColor);
                            dc.DrawText(t, new System.Windows.Point(rect.X + (rect.Width - t.Width) / 2, rect.Y + (rect.Height - t.Height) / 2));
                        }
                    }
                    SavePng(v, 250, 250, path);
                  }
                  finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                });
            }
            catch { /* artwork is a bonus -- never fail a build over it */ }
        }

        // 1400x1000 jewel case / inlay card, same layout as TargetFill Pro's.
        public static void ExportCaseInlay(string path, string prefix, string targetDir, MediaPreset media, long headBytes, long payload, bool parity)
        {
            RunSta(() =>
            {
              try
              {
                var v = new Media.DrawingVisual();
                using (var dc = v.RenderOpen())
                {
                    var muted = C(148, 163, 184);
                    var cyan = C(56, 189, 248);
                    var border = new Media.Pen(B(C(51, 65, 85)), 2);
                    dc.DrawRectangle(B(C(10, 14, 26)), null, new System.Windows.Rect(0, 0, 1400, 1000));
                    dc.DrawRectangle(null, border, new System.Windows.Rect(16, 16, 1368, 968));

                    var dashed = new Media.Pen(B(C(71, 85, 105)), 2) { DashStyle = Media.DashStyles.Dash };
                    dc.DrawLine(dashed, new System.Windows.Point(140, 16), new System.Windows.Point(140, 984));
                    dc.DrawLine(dashed, new System.Windows.Point(1260, 16), new System.Windows.Point(1260, 984));

                    // Spines (rotated)
                    var spineL = Text($"{prefix} - ARCHIVAL MASTER", 11, true, muted);
                    dc.PushTransform(new Media.RotateTransform(-90, 70, 500));
                    dc.DrawText(spineL, new System.Windows.Point(70 - spineL.Width / 2, 500 - spineL.Height / 2));
                    dc.Pop();
                    var spineR = Text(parity ? $"TARGETFILL (ARCHIVSECTOR-OD v{Version}) - PAR2 RECOVERY DATA" : $"TARGETFILL (ARCHIVSECTOR-OD v{Version})", 11, true, muted);
                    dc.PushTransform(new Media.RotateTransform(90, 1330, 500));
                    dc.DrawText(spineR, new System.Windows.Point(1330 - spineR.Width / 2, 500 - spineR.Height / 2));
                    dc.Pop();

                    // Header banner
                    var headerBg = B(C(15, 23, 42));
                    dc.DrawRectangle(headerBg, new Media.Pen(B(cyan), 2), new System.Windows.Rect(165, 35, 1070, 100));
                    dc.DrawText(Text(prefix.ToUpperInvariant(), 22, true, cyan), new System.Windows.Point(185, 48));
                    dc.DrawText(Text($"Mastered: {DateTime.Now:yyyy-MM-dd HH:mm}  |  Engine: TargetFill (ArchivSector-OD v{Version})  |  Standard: Dual-Split Optical Core", 10.5, false, muted), new System.Windows.Point(185, 95));

                    // Disc visual on the left (the 250px disc scaled to 300px)
                    dc.PushTransform(new Media.TranslateTransform(190, 165));
                    dc.PushTransform(new Media.ScaleTransform(1.2, 1.2));
                    DrawDisc(dc, media, headBytes, payload);
                    dc.Pop();
                    dc.Pop();

                    // Specifications panel
                    dc.DrawRectangle(headerBg, border, new System.Windows.Rect(520, 165, 715, 680));
                    var white = C(255, 255, 255);
                    var purple = C(192, 132, 252);
                    dc.DrawText(Text("DISC ARCHITECTURE SPECIFICATIONS", 12, true, cyan), new System.Windows.Point(540, 185));
                    dc.DrawText(Text($"Volume Label: {prefix}", 10, false, white), new System.Windows.Point(540, 220));
                    var dirLine = Text($"Target Directory: {targetDir}", 10, false, muted);
                    dirLine.MaxTextWidth = 675;
                    dc.DrawText(dirLine, new System.Windows.Point(540, 250));
                    dc.DrawText(Text("[Track 01] Lead-In: 00_lead_in_part1_filler.dat", 10, false, cyan), new System.Windows.Point(540, 290));
                    dc.DrawText(Text("[Payload] Core Data Zone: Center Tracks (Maximum Jitter Immunity)", 10, false, DataColor), new System.Windows.Point(540, 325));
                    dc.DrawText(Text("[Track 02] Lead-Out: zz_lead_out_part2_filler.dat", 10, false, purple), new System.Windows.Point(540, 360));
                    dc.DrawText(Text("[Integrity] Standalone Verifier: Verify_Disc.bat (Zero-Dependency)", 10, false, white), new System.Windows.Point(540, 400));
                    dc.DrawText(Text(parity ? "[Parity] PAR2 recovery files (repair with MultiPar / par2)" : "[Parity] Not included", 10, false, DataColor), new System.Windows.Point(540, 435));
                    dc.DrawText(Text("[Catalog] Registered in Offline Virtual Disc Index", 10, false, muted), new System.Windows.Point(540, 470));
                    dc.DrawText(Text($"[Media] {media.Label} -- {FormatBytes(media.Bytes)} capacity, {FormatBytes(payload)} of data", 10, false, muted), new System.Windows.Point(540, 505));

                    // Footer bar
                    dc.DrawRectangle(headerBg, border, new System.Windows.Rect(165, 870, 1070, 75));
                    dc.DrawText(Text($"TARGETFILL (ARCHIVSECTOR-OD v{Version}) ARCHIVAL MASTER * CAPACITY PADDED * SHA-256 MANIFEST", 9.5, true, muted), new System.Windows.Point(185, 900));
                }
                SavePng(v, 1400, 1000, path);
              }
              finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
        }

        // Manual "Export Disc Label" / "Export Jewel Case" buttons.
        public static Result ExportArt(string targetDir, Options o, bool caseInlay)
        {
            var r = new Result();
            try
            {
                targetDir = NormalizePath(targetDir);
                var stats = ScanPayload(targetDir);
                long needed = o.Media.IsUsb ? 0 : Math.Max(0, o.Media.Bytes - stats.Bytes - Overhead(stats, o.Media));
                var (head, _) = o.Media.IsUsb ? (0L, 0L) : SplitBytes(needed, o.Placement, o.SplitPercent);
                var prefix = PrefixFor(targetDir, o);
                var path = Path.Combine(targetDir, prefix + (caseInlay ? "_CaseInsert.png" : "_DiscLabel.png"));
                if (File.Exists(path)) File.Delete(path);
                if (caseInlay) ExportCaseInlay(path, prefix, targetDir, o.Media, head, stats.Bytes, o.Parity);
                else ExportDiscLabel(path, o.Media, head, stats.Bytes);
                Track(targetDir, path, "artifact");
                Chime(o);
                r.Ok = File.Exists(path);
                r.Message = r.Ok ? $"Exported {(caseInlay ? "the jewel case / inlay card" : "the disc label")}." : "Couldn't create the image.";
                if (r.Ok) r.Outputs.Add(Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                r.Message = $"Couldn't export the image: {ex.Message}";
            }
            return r;
        }

        // ---------------------------------------------------------------
        // ImgBurn
        // ---------------------------------------------------------------
        // Opens ImgBurn in Build mode with everything that belongs on the
        // disc (your files, filler, manifest, verifier, recovery data),
        // UDF 2.60 and the volume label, like TargetFill Pro's button.
        // Exports the disc label first.
        public static Result LaunchImgBurn(string targetDir, Options o)
        {
            var r = new Result();
            try
            {
                targetDir = NormalizePath(targetDir);
                var exe = ImgBurnService.FindExecutable(ConfigService.Load().ImgBurnPathOverride);
                if (exe is null) { r.Message = "ImgBurn.exe couldn't be found. Install ImgBurn, or set its path in Settings."; return r; }

                ExportArt(targetDir, new Options { Media = o.Media, Placement = o.Placement, SplitPercent = o.SplitPercent, VolumeLabel = o.VolumeLabel, Sound = false }, caseInlay: false);

                var map = LoadTracker(targetDir);
                bool tool = File.Exists(Path.Combine(targetDir, "TargetFill.ps1"));
                var items = new List<string>();
                foreach (var entry in new DirectoryInfo(targetDir).EnumerateFileSystemInfos())
                {
                    var kind = KindOf(entry, targetDir, map, tool);
                    if (kind is "iso" or "discdir" or "tool" or "tracker" or "system") continue;
                    if (LabelPngRx.IsMatch(entry.Name)) continue;
                    if (entry is FileInfo fi && fi.Extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) && kind is not null) continue;
                    items.Add(entry.FullName);
                }

                var args = new StringBuilder("/MODE BUILD");
                if (items.Count > 0) args.Append(" /SRC \"").Append(string.Join("|", items)).Append("\" /FILESYSTEM UDF /UDFREVISION 2.60");
                if (!string.IsNullOrWhiteSpace(o.VolumeLabel))
                    args.Append(" /VOLUMELABEL \"").Append(Regex.Replace(o.VolumeLabel.Trim(), "[^a-zA-Z0-9_]", "_")).Append('"');
                Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args.ToString(), UseShellExecute = false });
                r.Ok = true;
                r.Message = "Opened ImgBurn in Build mode with this folder's contents.";
            }
            catch (Exception ex)
            {
                r.Message = $"Couldn't open ImgBurn: {ex.Message}";
            }
            return r;
        }

        // ---------------------------------------------------------------
        // Right-click menu ("TargetFill (ArchivSector-OD)" on folders)
        // ---------------------------------------------------------------
        // Per-user (HKCU), no admin needed. Adds the entry to folders and to
        // the empty space inside a folder; both start this exe with
        // --targetfill "<folder>", which opens a TargetFill-only window.
        private const string MenuKey = "ArchivSectorOD.TargetFill";
        private static readonly string[] MenuRoots = { @"Software\Classes\Directory\shell", @"Software\Classes\Directory\Background\shell" };

        public static bool IsRightClickInstalled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"{MenuRoots[0]}\{MenuKey}\command");
                var cmd = k?.GetValue("") as string;
                return cmd is not null && Environment.ProcessPath is string exe && cmd.Contains(exe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string SetRightClick(bool install)
        {
            try
            {
                foreach (var root in MenuRoots)
                {
                    if (!install)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{MenuKey}", throwOnMissingSubKey: false);
                        continue;
                    }
                    var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Couldn't find this program's location.");
                    using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{MenuKey}");
                    key.SetValue("MUIVerb", "TargetFill (ArchivSector-OD)");
                    key.SetValue("Icon", $"\"{exe}\",0");
                    using var cmd = key.CreateSubKey("command");
                    cmd.SetValue("", $"\"{exe}\" --targetfill \"%V\"");
                }
                return install
                    ? "Added \"TargetFill (ArchivSector-OD)\" to the right-click menu for folders. (On Windows 11 it's under \"Show more options\".)"
                    : "Removed TargetFill from the right-click menu.";
            }
            catch (Exception ex)
            {
                return $"Couldn't change the right-click menu: {ex.Message}";
            }
        }

        // The folder passed by the right-click menu, if the app was started that way.
        public static string? StartupFolder()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i].Equals("--targetfill", StringComparison.OrdinalIgnoreCase))
                {
                    // A drive root arrives as E:" (Windows reads the \" in
                    // "E:\" as an escaped quote), so put the backslash back.
                    var p = args[i + 1].Trim().Trim('"').Trim();
                    if (p.Length == 2 && p[1] == ':') p += "\\";
                    return p.Length == 0 ? null : p;
                }
            return null;
        }

        // ---------------------------------------------------------------
        // History
        // ---------------------------------------------------------------
        // Adds a History record, so backups appear next to the rips.
        public static void RecordHistory(string title, string media, string outputPath)
        {
            HistoryService.Append(new HistoryEntry
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Title = title,
                System = $"TargetFill backup ({media})",
                OutputPath = outputPath,
                Capacity = media,
                RedumpMatch = "N/A (backup disc)",
            });
        }
    }

    // Lets a drive bay hand a finished rip to the TargetFill tab
    // ("Make archival backup disc"). AppShell listens and switches tabs.
    public static class TargetFillHandoff
    {
        public static event Action<string>? Requested;
        public static void Request(string folder) => Requested?.Invoke(folder);
    }
}
