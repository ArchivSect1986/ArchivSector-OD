using System.Text;

namespace ArchivSector_OD.Burning
{
    // The Drive info screen (v4.0 week 4): asks a drive about itself and
    // its disc using direct commands (ScsiDrive). Read-only: every command
    // here only asks questions, nothing is written. It's also the first
    // proof that the direct connection works on a PC before any burning
    // is done through it.
    //
    // Commands used (MMC, the standard every CD/DVD/BD drive follows):
    //   INQUIRY ............... maker, model, firmware
    //   GET CONFIGURATION ..... every disc type ("profile") it supports, the current one
    //   MODE SENSE page 2A .... buffer size
    //   READ DISC INFORMATION . blank / appendable / finalized, erasable, sessions
    //   READ TRACK INFORMATION  space on a blank disc
    //   READ CAPACITY ......... size of a written disc
    //   READ DISC STRUCTURE ... the disc's maker code (BD, DVD+R/RW)
    //   READ TOC (ATIP) ....... a CD-R/RW's maker code (lead-in time) and real size
    //   GET PERFORMANCE ....... write speeds the drive offers for the disc in it
    public static class DriveInspector
    {
        public sealed class Row
        {
            public string Label = "";
            public string Value = "";
            public Row(string label, string value) { Label = label; Value = value; }
        }

        public sealed class Section
        {
            public string Title = "";
            public List<Row> Rows = new();
            public Section(string title) { Title = title; }
            public void Add(string label, string value) => Rows.Add(new Row(label, value));
        }

        public sealed class Report
        {
            public string DriveLetter = "";
            public string? Problem;                 // couldn't talk to the drive at all
            public List<Section> Sections = new();
            public List<string> Notes = new();      // commands the drive refused (for the log)

            // For later use by the writer.
            public string Vendor = "", Product = "", Firmware = "";
            public int CurrentProfile;
            public HashSet<int> Profiles = new();
            public HashSet<int> Features = new();

            public string ToText()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"ArchivSector-OD drive info — {DriveLetter}: — {DateTime.Now:yyyy-MM-dd HH:mm}");
                if (Problem is not null) sb.AppendLine(Problem);
                foreach (var s in Sections)
                {
                    sb.AppendLine();
                    sb.AppendLine(s.Title.ToUpperInvariant());
                    foreach (var r in s.Rows) sb.AppendLine($"  {r.Label,-22} {r.Value}");
                }
                if (Notes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("NOT ANSWERED");
                    foreach (var n in Notes) sb.AppendLine("  " + n);
                }
                return sb.ToString();
            }
        }

        public static Task<Report> InspectAsync(string driveLetter) =>
            Task.Factory.StartNew(() => Inspect(driveLetter), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        public static Report Inspect(string driveLetter)
        {
            var rep = new Report { DriveLetter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant() };
            using var drive = ScsiDrive.Open(rep.DriveLetter, out var problem);
            if (drive is null) { rep.Problem = problem; return rep; }

            var driveSec = new Section("Drive");
            rep.Sections.Add(driveSec);

            // INQUIRY
            var inq = new byte[96];
            var r = drive.Read(new byte[] { 0x12, 0, 0, 0, 96, 0 }, inq, 10);
            if (r.Ok)
            {
                rep.Vendor = Ascii(inq, 8, 8);
                rep.Product = Ascii(inq, 16, 16);
                rep.Firmware = Ascii(inq, 32, 4);
                driveSec.Add("Maker", rep.Vendor);
                driveSec.Add("Model", rep.Product);
                driveSec.Add("Firmware", rep.Firmware);
            }
            else
            {
                rep.Problem = "The drive didn't answer the direct connection: " + r.Describe() + ". Burning through it won't work on this PC yet.";
                return rep;
            }

            // MODE SENSE (10), page 2A: buffer size
            var mode = new byte[256];
            r = drive.Read(new byte[] { 0x5A, 0x08, 0x2A, 0, 0, 0, 0, 0x01, 0x00, 0 }, mode, 10);
            if (r.Ok)
            {
                int bdl = BE16(mode, 6);
                int p = 8 + bdl;
                if (p + 14 <= mode.Length && (mode[p] & 0x3F) == 0x2A)
                {
                    int kb = BE16(mode, p + 12);
                    if (kb > 0) driveSec.Add("Buffer", $"{kb:N0} KB" + (kb >= 1024 ? $" ({kb / 1024.0:0.#} MB)" : ""));
                }
            }
            else rep.Notes.Add("Buffer size (MODE SENSE 2A): " + r.Describe());

            // GET CONFIGURATION: profiles and features
            var cfg = new byte[4096];
            r = drive.Read(new byte[] { 0x46, 0x00, 0, 0, 0, 0, 0, 0x10, 0x00, 0 }, cfg, 10);
            if (r.Ok)
            {
                int dataLen = (int)Math.Min(BE32(cfg, 0) + 4, cfg.Length);
                rep.CurrentProfile = BE16(cfg, 6);
                int off = 8;
                while (off + 4 <= dataLen)
                {
                    int code = BE16(cfg, off);
                    int add = cfg[off + 3];
                    rep.Features.Add(code);
                    if (code == 0x0000)
                        for (int i = 0; i + 4 <= add && off + 4 + i + 4 <= dataLen; i += 4)
                            rep.Profiles.Add(BE16(cfg, off + 4 + i));
                    off += 4 + add;
                }

                var reads = new List<string>();
                var writes = new List<string>();
                foreach (var pr in rep.Profiles.OrderBy(x => x))
                {
                    var name = ProfileName(pr);
                    if (name is null) continue;
                    if (IsWritable(pr)) writes.Add(name); else reads.Add(name);
                }
                var capSec = new Section("Discs it handles");
                capSec.Add("Writes", writes.Count > 0 ? string.Join(", ", writes.Distinct()) : "—");
                capSec.Add("Reads", string.Join(", ", reads.Distinct().Concat(writes.Distinct())));
                var extras = new List<string>();
                if (rep.Features.Contains(0x002D)) extras.Add("CD track-at-once");
                if (rep.Features.Contains(0x002E)) extras.Add("CD disc-at-once");
                if (rep.Features.Contains(0x0107)) extras.Add("speed control");
                if (rep.Features.Contains(0x0108)) extras.Add("serial number");
                if (rep.Features.Contains(0x010D)) extras.Add("AACS");
                if (extras.Count > 0) capSec.Add("Also supports", string.Join(", ", extras));
                rep.Sections.Add(capSec);
            }
            else rep.Notes.Add("Supported discs (GET CONFIGURATION): " + r.Describe());

            // Serial number (feature 0x0108), if the drive reports one.
            if (rep.Features.Contains(0x0108))
            {
                var ser = new byte[256];
                r = drive.Read(new byte[] { 0x46, 0x02, 0x01, 0x08, 0, 0, 0, 0x01, 0x00, 0 }, ser, 10);
                if (r.Ok && BE16(ser, 8) == 0x0108)
                {
                    int add = ser[11];
                    var serial = Ascii(ser, 12, Math.Min(add, ser.Length - 12));
                    if (serial.Length > 0) driveSec.Add("Serial number", serial);
                }
            }

            // ---- the disc ----
            var disc = new Section("Disc in the drive");
            rep.Sections.Add(disc);
            if (rep.CurrentProfile == 0)
            {
                disc.Add("Disc", "None (or the drive can't read it)");
                return rep;
            }
            disc.Add("Type", ProfileName(rep.CurrentProfile) ?? $"profile 0x{rep.CurrentProfile:X4}");
            string family = Family(rep.CurrentProfile);

            bool overwriteInPlaceDisc = rep.CurrentProfile is 0x43 or 0x1A or 0x2A or 0x12;

            // READ DISC INFORMATION
            var di = new byte[34];
            r = drive.Read(new byte[] { 0x51, 0, 0, 0, 0, 0, 0, 0, 34, 0 }, di, 10);
            bool blank = false;
            int lastTrack = 1;
            if (r.Ok)
            {
                int status = di[2] & 3;
                blank = status == 0;
                bool erasable = (di[2] & 0x10) != 0;
                int sessions = di[4] | (di[9] << 8);
                lastTrack = di[6] | (di[11] << 8);
                // BD-RE, DVD+RW and DVD-RAM are written over in place: once
                // formatted they report "complete" whether or not they hold
                // anything, so "finalized" would be misleading for them.
                bool overwriteInPlace = rep.CurrentProfile is 0x43 or 0x1A or 0x2A or 0x12;
                disc.Add("Status", status switch
                {
                    0 => "Blank",
                    2 when overwriteInPlace => "Formatted, can be written over (it may hold data)",
                    1 => "Has data, more can be added",
                    2 => "Finalized (closed)",
                    _ => IsWritable(rep.CurrentProfile) ? "Written over in place (random access)" : "Pressed / read-only",
                });
                disc.Add("Rewritable", erasable ? "Yes" : "No");
                if (!blank && status != 3 && !overwriteInPlace) disc.Add("Sessions", sessions.ToString());
            }
            else rep.Notes.Add("Disc status (READ DISC INFORMATION): " + r.Describe());

            // Size: blank -> free space of the open track; written -> READ CAPACITY.
            if (blank || (r.Ok && (di[2] & 3) == 1))
            {
                var ti = new byte[48];
                int track = Math.Max(1, lastTrack);
                r = drive.Read(new byte[] { 0x52, 0x01, (byte)(track >> 24), (byte)(track >> 16), (byte)(track >> 8), (byte)track, 0, 0, 48, 0 }, ti, 10);
                long free = r.Ok ? BE32(ti, 16) : 0;
                if (free > 0)
                    disc.Add("Free space", $"{free:N0} sectors = {free * 2048:N0} bytes ({Gb(free * 2048)})");
                else if (!r.Ok)
                    rep.Notes.Add("Free space (READ TRACK INFORMATION): " + r.Describe());
            }

            // Rewritables (BD-RE, DVD+RW, DVD-RAM): the sizes they can be
            // formatted to. An erased BD-RE has no free space until it's
            // formatted, so this is where its real size shows.
            if (overwriteInPlaceDisc)
            {
                var fc = new byte[252];
                r = drive.Read(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0, 252, 0 }, fc, 10);
                if (r.Ok && fc[3] >= 8)
                {
                    long cur = BE32(fc, 4);
                    int curType = fc[8] & 3;   // 1 unformatted, 2 formatted
                    disc.Add("Formatted", curType == 2 ? $"Yes, {cur:N0} sectors = {cur * 2048:N0} bytes ({Gb(cur * 2048)})" : "No (it's formatted when it's burned)");
                    long noSpare = 0, withSpare = 0;
                    for (int off = 12; off + 8 <= 4 + fc[3] && off + 8 <= fc.Length; off += 8)
                    {
                        long blocks = BE32(fc, off);
                        int type = fc[off + 4] >> 2;
                        if (type == 0x31) noSpare = Math.Max(noSpare, blocks);          // BD-RE, no spare areas
                        else if (type == 0x30 && withSpare == 0) withSpare = blocks;    // BD-RE, standard spare areas
                        else if (type == 0x26 && noSpare == 0) noSpare = blocks;        // DVD+RW full format
                    }
                    if (noSpare > 0) disc.Add("Whole disc", $"{noSpare:N0} sectors = {noSpare * 2048:N0} bytes ({Gb(noSpare * 2048)}), no spare areas");
                    if (withSpare > 0 && withSpare != noSpare) disc.Add("With spare areas", $"{withSpare:N0} sectors = {withSpare * 2048:N0} bytes ({Gb(withSpare * 2048)})");
                }
                else if (!r.Ok) rep.Notes.Add("Format sizes (READ FORMAT CAPACITIES): " + r.Describe());
            }
            if (!blank)
            {
                var cap = new byte[8];
                r = drive.Read(new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, cap, 10);
                if (r.Ok)
                {
                    long sectors = BE32(cap, 0) + 1L;
                    long block = BE32(cap, 4);
                    if (block == 0) block = 2048;
                    disc.Add("Recorded size", $"{sectors:N0} sectors = {sectors * block:N0} bytes ({Gb(sectors * block)})");
                }
            }

            // Maker code
            switch (family)
            {
                case "BD":
                {
                    var ds = new byte[4100];
                    r = drive.Read(new byte[] { 0xAD, 0x01, 0, 0, 0, 0, 0, 0x00, (byte)(ds.Length >> 8), (byte)ds.Length, 0, 0 }, ds, 10);
                    if (r.Ok && ds[4] == (byte)'D' && ds[5] == (byte)'I')
                    {
                        string maker = Ascii(ds, 4 + 100, 6), media = Ascii(ds, 4 + 106, 3);
                        int rev = ds[4 + 111];
                        if (maker.Length > 0) disc.Add("Maker code", $"{maker}-{media}-{rev:D3}");
                    }
                    else rep.Notes.Add("BD maker code (READ DISC STRUCTURE): " + r.Describe());
                    break;
                }
                case "DVD+":
                {
                    var ds = new byte[260];
                    r = drive.Read(new byte[] { 0xAD, 0x00, 0, 0, 0, 0, 0, 0x11, (byte)(ds.Length >> 8), (byte)ds.Length, 0, 0 }, ds, 10);
                    if (r.Ok)
                    {
                        string maker = Ascii(ds, 4 + 19, 8), media = Ascii(ds, 4 + 27, 3);
                        int rev = ds[4 + 30];
                        if (maker.Length > 0) disc.Add("Maker code", $"{maker}-{media}-{rev:D3}");
                    }
                    else rep.Notes.Add("DVD+ maker code (READ DISC STRUCTURE 11h): " + r.Describe());
                    break;
                }
                case "CD":
                {
                    if (rep.CurrentProfile is 0x09 or 0x0A)
                    {
                        var atip = new byte[28];
                        r = drive.Read(new byte[] { 0x43, 0x02, 0x04, 0, 0, 0, 0, 0, 28, 0 }, atip, 10);
                        if (r.Ok && BE16(atip, 0) >= 22)
                        {
                            int m = atip[8], s = atip[9], f = atip[10];
                            disc.Add("Maker code (ATIP)", $"lead-in {m:D2}:{s:D2}:{f:D2}");
                            int lm = atip[12], ls = atip[13], lf = atip[14];
                            long leadOut = (lm * 60L + ls) * 75 + lf - 150;
                            if (leadOut > 0)
                                disc.Add("End marker (ATIP)", $"{lm:D2}:{ls:D2}:{lf:D2} = {leadOut:N0} sectors (the drive keeps the last few for itself; Free space is what can be written)");
                        }
                        else rep.Notes.Add("CD-R maker code (READ TOC ATIP): " + r.Describe());
                    }
                    break;
                }
            }

            // Write speeds for this disc
            if (IsWritable(rep.CurrentProfile))
            {
                var perf = new byte[8 + 16 * 32];
                r = drive.Read(new byte[] { 0xAC, 0, 0, 0, 0, 0, 0, 0, 0, 32, 0x03, 0 }, perf, 10);
                if (r.Ok)
                {
                    long len = Math.Min(BE32(perf, 0) + 4, perf.Length);
                    double oneX = family switch { "CD" => 176.4, "BD" => 4495.5, _ => 1385.0 };
                    var speeds = new SortedSet<double>();
                    for (int off = 8; off + 16 <= len; off += 16)
                    {
                        long kbs = BE32(perf, off + 12);
                        if (kbs > 0) speeds.Add(Math.Round(kbs / oneX, 1));
                    }
                    if (speeds.Count > 0)
                        disc.Add("Write speeds", string.Join(", ", speeds.Reverse().Select(x => $"{x:0.#}x")));
                }
                else rep.Notes.Add("Write speeds (GET PERFORMANCE): " + r.Describe());
            }
            return rep;
        }

        // MMC profile numbers → names.
        public static string? ProfileName(int p) => p switch
        {
            0x08 => "CD-ROM", 0x09 => "CD-R", 0x0A => "CD-RW",
            0x10 => "DVD-ROM", 0x11 => "DVD-R", 0x12 => "DVD-RAM", 0x13 => "DVD-RW", 0x14 => "DVD-RW",
            0x15 => "DVD-R DL", 0x16 => "DVD-R DL", 0x1A => "DVD+RW", 0x1B => "DVD+R",
            0x2A => "DVD+RW DL", 0x2B => "DVD+R DL",
            0x40 => "BD-ROM", 0x41 => "BD-R", 0x42 => "BD-R", 0x43 => "BD-RE",
            0x50 => "HD DVD-ROM", 0x51 => "HD DVD-R", 0x52 => "HD DVD-RAM",
            _ => null,
        };

        public static bool IsWritable(int p) => p is 0x09 or 0x0A or 0x11 or 0x12 or 0x13 or 0x14 or 0x15 or 0x16
            or 0x1A or 0x1B or 0x2A or 0x2B or 0x41 or 0x42 or 0x43 or 0x51 or 0x52;

        private static string Family(int p) => p switch
        {
            >= 0x08 and <= 0x0A => "CD",
            0x1A or 0x1B or 0x2A or 0x2B => "DVD+",
            >= 0x10 and <= 0x2B => "DVD",
            >= 0x40 and <= 0x43 => "BD",
            _ => "",
        };

        private static int BE16(byte[] b, int o) => o + 1 < b.Length ? (b[o] << 8) | b[o + 1] : 0;
        private static long BE32(byte[] b, int o) => o + 3 < b.Length ? ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3] : 0;

        private static string Ascii(byte[] b, int o, int n)
        {
            if (o < 0 || o >= b.Length) return "";
            n = Math.Min(n, b.Length - o);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                byte c = b[o + i];
                if (c == 0) break;
                sb.Append(c >= 32 && c < 127 ? (char)c : ' ');
            }
            return sb.ToString().Trim();
        }

        private static string Gb(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0.0} MB";
    }
}
