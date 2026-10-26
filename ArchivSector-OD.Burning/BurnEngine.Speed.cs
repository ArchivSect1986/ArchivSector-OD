using System.Globalization;

namespace ArchivSector_OD.Burning
{
    // Write speed (v4.0 week 6): which speeds the drive offers for the disc
    // in it, and asking for one of them when burning. Every program takes
    // the speed as a multiple of the disc's 1x ("4x"); 0 = fastest.
    //   * Our own writer: SET CD SPEED, plus SET STREAMING on DVD and BD
    //     (what most DVD/BD drives listen to).
    //   * ImgBurn: its /SPEED option.
    //   * Windows: IMAPI2's SetWriteSpeed. Windows may ignore it.
    // A drive is free to round the speed to one it likes, and many discs
    // only allow one or two speeds.
    public static partial class BurnEngine
    {
        public sealed class WriteSpeed
        {
            public double X;            // 4 = 4x
            public int KBs;             // kilobytes (1000 bytes) per second
            public string Label => SpeedText(X);
        }

        // 1x in kilobytes (1000 bytes) per second, as drives count it.
        private static double OneXKBs(string family) => family switch { "CD" => 176.4, "BD" => 4495.5, _ => 1385.0 };

        private static string FamilyOfProfile(int profile) => profile switch
        {
            >= 0x08 and <= 0x0A => "CD",
            >= 0x40 and <= 0x43 => "BD",
            _ => "DVD",
        };

        /// <summary>"4x", "2.4x". Always with a dot, whatever the PC's language.</summary>
        public static string SpeedText(double x) => x.ToString("0.#", CultureInfo.InvariantCulture) + "x";

        /// <summary>
        /// The write speeds the drive offers for the disc in it, fastest
        /// first. Empty if the drive is busy, has no writable disc, or
        /// doesn't say (then only "fastest" can be offered).
        /// </summary>
        public static List<WriteSpeed> QueryWriteSpeeds(string driveLetter)
        {
            var list = new List<WriteSpeed>();
            var letter = Norm(driveLetter);
            if (IsBusy(letter)) return list;
            try
            {
                using var drive = ScsiDrive.Open(letter, out _);
                if (drive is null) return list;

                var cfg = new byte[8];
                var rep = drive.Read(new byte[] { 0x46, 0x02, 0, 0, 0, 0, 0, 0, 8, 0 }, cfg, 10);
                if (!rep.Ok) return list;
                double oneX = OneXKBs(FamilyOfProfile((cfg[6] << 8) | cfg[7]));

                // GET PERFORMANCE, type 3 (write speed descriptors), up to 32.
                var perf = new byte[8 + 16 * 32];
                rep = drive.Read(new byte[] { 0xAC, 0, 0, 0, 0, 0, 0, 0, 0, 32, 0x03, 0 }, perf, 10);
                if (!rep.Ok) return list;
                long len = Math.Min(BE32(perf, 0) + 4, perf.Length);
                var seen = new HashSet<double>();
                for (int off = 8; off + 16 <= len; off += 16)
                {
                    long kbs = BE32(perf, off + 12);
                    if (kbs <= 0) continue;
                    double x = Math.Round(kbs / oneX, 1);
                    if (x <= 0 || !seen.Add(x)) continue;
                    list.Add(new WriteSpeed { X = x, KBs = (int)kbs });
                }
                list.Sort((a, b) => b.X.CompareTo(a.X));
            }
            catch { list.Clear(); }
            return list;
        }

        /// <summary>
        /// For the burn queue: "Fastest" (0), "Middle" or "Slowest" of the
        /// speeds offered for the disc in the drive. 0 = fastest.
        /// </summary>
        public static double PickSpeed(string driveLetter, string choice)
        {
            if (string.Equals(choice, "Fastest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(choice)) return 0;
            var speeds = QueryWriteSpeeds(driveLetter);
            if (speeds.Count <= 1) return 0;   // only one speed: it's also the fastest
            return string.Equals(choice, "Slowest", StringComparison.OrdinalIgnoreCase)
                ? speeds[^1].X
                : speeds[speeds.Count / 2].X;
        }

        // Our own writer: ask the drive for a write speed (0 = fastest).
        // Failures are only logged: the drive then writes at its own speed.
        private static void SetDirectWriteSpeed(ScsiDrive drive, int profile, double speedX, long endLba, Action<string>? log)
        {
            if (speedX <= 0) return;
            string family = FamilyOfProfile(profile);
            int kbs = (int)Math.Round(speedX * OneXKBs(family));
            if (kbs <= 0) return;

            // SET CD SPEED: read as fast as possible, write at kbs.
            int w16 = Math.Min(kbs, 0xFFFF);
            var rep = drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, (byte)(w16 >> 8), (byte)w16, 0, 0, 0, 0, 0, 0 }, 10);
            bool ok = rep.Ok;

            // SET STREAMING (DVD, BD): one performance descriptor covering
            // the part being written, with kbs per 1000 ms for reading and writing.
            if (family != "CD")
            {
                var d = new byte[28];
                long end = Math.Max(0, endLba);
                d[8] = (byte)(end >> 24); d[9] = (byte)(end >> 16); d[10] = (byte)(end >> 8); d[11] = (byte)end;
                void Put(int at, long v) { d[at] = (byte)(v >> 24); d[at + 1] = (byte)(v >> 16); d[at + 2] = (byte)(v >> 8); d[at + 3] = (byte)v; }
                Put(12, kbs); Put(16, 1000);      // read size (KB) per read time (ms)
                Put(20, kbs); Put(24, 1000);      // write size (KB) per write time (ms)
                var rs = drive.Write(new byte[] { 0xB6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 28, 0 }, d, 10);
                ok |= rs.Ok;
                if (!rs.Ok) log?.Invoke("Direct write: the drive didn't accept SET STREAMING: " + rs.Describe());
            }
            log?.Invoke(ok
                ? $"Direct write: asked for {SpeedText(speedX)} ({kbs:N0} KB/s); the drive may round it to a speed it supports"
                : $"Direct write: the drive didn't accept the {SpeedText(speedX)} speed ({rep.Describe()}); it writes at its own speed");
        }

        // After writing at a chosen speed: back to the drive's own (fastest)
        // speeds, so the verify and later reads aren't held to the write
        // speed. SET STREAMING with "restore drive defaults", then SET CD
        // SPEED to the maximum. Failures don't matter.
        private static void RestoreDriveSpeed(ScsiDrive drive, int profile, double speedX)
        {
            if (speedX <= 0) return;
            FullDriveSpeed(drive);
        }

        // The drive's own (fastest) speeds, whatever was asked for before:
        // used before reading a disc back (Verify mode), since a slower
        // speed set earlier (by us or another program) can stay in force
        // until the disc is taken out. Drives that don't know a command
        // just ignore it.
        private static void FullDriveSpeed(ScsiDrive drive)
        {
            var d = new byte[28];
            d[0] = 0x04;                          // SET STREAMING, RDD: restore drive defaults
            drive.Write(new byte[] { 0xB6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 28, 0 }, d, 10);
            drive.Command(new byte[] { 0xBB, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0 }, 10);
        }

        // Windows' engine: IMAPI2 counts in sectors per second.
        private static void SetImapiWriteSpeed(dynamic fmt, double speedX)
        {
            if (speedX <= 0) return;
            try
            {
                int type = (int)fmt.CurrentPhysicalMediaType;
                double oneX = type is >= 1 and <= 3 ? 176.4 : type is >= 17 and <= 19 ? 4495.5 : 1385.0;
                int sectorsPerSecond = (int)Math.Round(speedX * oneX * 1000 / Sector);
                fmt.SetWriteSpeed(sectorsPerSecond, false);
            }
            catch { /* Windows writes at its own speed */ }
        }
    }
}
