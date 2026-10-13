using System.IO;
using System.Text;

namespace ArchivSector_OD
{
    // Reads an Xbox game's identity from inside a finished Redumper dump.
    //
    // On a PC drive only the small video partition of an Original Xbox or
    // Xbox 360 disc is visible, so nothing useful can be read before the
    // dump. The dumped .iso holds the whole disc, though, including the
    // game partition (Xbox's own XDVDFS file system). Its main program:
    //   * Original Xbox: default.xbe -- its certificate holds the game's
    //     real name and Title ID.
    //   * Xbox 360: default.xex -- holds the Title ID (e.g. 4D5307E6) and
    //     the disc number. The name itself isn't stored readably, so 360
    //     names come from a Redump DAT match instead.
    public static class XboxGameInfoService
    {
        public sealed class Info
        {
            public string System = "";        // "Original Xbox" or "Xbox 360"
            public string? Title;             // Original Xbox only
            public string? TitleId;           // 8 hex digits
            public int DiscNumber;            // Xbox 360, 0 if unknown
            public int DiscCount;
        }

        private const int Sector = 2048;

        // Where the game partition starts in a full-disc image, per Xbox
        // disc generation (XGD1 = Original Xbox, XGD2/XGD3 = Xbox 360), plus
        // 0 for an image of the game partition alone.
        private static readonly long[] PartitionOffsets = { 0x18300000L, 0xFD90000L, 0x2080000L, 0L };
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

        public static Info? ReadFromIso(string isoPath)
        {
            try
            {
                using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                foreach (var start in PartitionOffsets)
                {
                    if (start + 0x10000 + Sector > fs.Length) continue;
                    var vd = ReadAt(fs, start + 0x10000, Sector);   // volume descriptor, sector 32
                    if (!StartsWith(vd, Magic)) continue;

                    long rootSector = BitConverter.ToUInt32(vd, 20);
                    long rootSize = BitConverter.ToUInt32(vd, 24);
                    if (rootSize == 0 || rootSize > 4 * 1024 * 1024) return null;
                    var root = ReadAt(fs, start + rootSector * Sector, (int)rootSize);

                    if (FindEntry(root, "default.xbe") is (long xbeSector, long xbeSize))
                        return ReadXbe(fs, start + xbeSector * Sector, xbeSize);
                    if (FindEntry(root, "default.xex") is (long xexSector, long xexSize))
                        return ReadXex(fs, start + xexSector * Sector, xexSize);
                    return null;
                }
            }
            catch { /* not an Xbox image, or unreadable */ }
            return null;
        }

        // XDVDFS directory: entries of (left, right, sector, size, attributes,
        // name length, name), each padded to 4 bytes; 0xFF fills the rest of
        // a 2 KB sector. A straight walk finds every entry.
        private static (long Sector, long Size)? FindEntry(byte[] dir, string name)
        {
            int pos = 0;
            while (pos + 14 <= dir.Length)
            {
                ushort left = BitConverter.ToUInt16(dir, pos);
                if (left == 0xFFFF)
                {
                    pos = (pos / Sector + 1) * Sector;
                    continue;
                }
                long sector = BitConverter.ToUInt32(dir, pos + 4);
                long size = BitConverter.ToUInt32(dir, pos + 8);
                int len = dir[pos + 13];
                if (len == 0 || pos + 14 + len > dir.Length) break;
                var entryName = Encoding.ASCII.GetString(dir, pos + 14, len);
                if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase)) return (sector, size);
                pos += (14 + len + 3) & ~3;
            }
            return null;
        }

        // XBE: "XBEH", base address at 0x104, certificate address at 0x118.
        // Certificate: Title ID at +8, title name (40 UTF-16 characters) at +0x0C.
        private static Info? ReadXbe(FileStream fs, long offset, long size)
        {
            var head = ReadAt(fs, offset, (int)Math.Min(size, 0x10000));
            if (head.Length < 0x180 || Encoding.ASCII.GetString(head, 0, 4) != "XBEH") return null;
            long baseAddr = BitConverter.ToUInt32(head, 0x104);
            long certAddr = BitConverter.ToUInt32(head, 0x118);
            long cert = certAddr - baseAddr;
            if (cert < 0 || cert + 0x0C + 80 > head.Length) return null;

            uint titleId = BitConverter.ToUInt32(head, (int)cert + 8);
            var title = Encoding.Unicode.GetString(head, (int)cert + 0x0C, 80).Split('\0')[0].Trim();
            return new Info
            {
                System = "Original Xbox",
                Title = string.IsNullOrWhiteSpace(title) ? null : title,
                TitleId = titleId.ToString("X8"),
            };
        }

        // XEX2 (big-endian): optional headers start at 0x18 as (key, value)
        // pairs; key 0x00040006 points to the execution info, which has the
        // Title ID at +12 and disc number / count at +18 / +19.
        private static Info? ReadXex(FileStream fs, long offset, long size)
        {
            var head = ReadAt(fs, offset, (int)Math.Min(size, 0x10000));
            if (head.Length < 0x18 || Encoding.ASCII.GetString(head, 0, 4) != "XEX2") return null;
            uint count = BE32(head, 0x14);
            var info = new Info { System = "Xbox 360" };
            for (uint i = 0; i < count && 0x18 + i * 8 + 8 <= head.Length; i++)
            {
                int at = (int)(0x18 + i * 8);
                uint key = BE32(head, at);
                if (key != 0x00040006) continue;
                uint exec = BE32(head, at + 4);
                if (exec + 24 > head.Length) break;
                info.TitleId = BE32(head, (int)exec + 12).ToString("X8");
                info.DiscNumber = head[exec + 18];
                info.DiscCount = head[exec + 19];
                break;
            }
            return info;
        }

        private static uint BE32(byte[] b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);

        private static byte[] ReadAt(FileStream fs, long offset, int count)
        {
            var buf = new byte[count];
            fs.Seek(offset, SeekOrigin.Begin);
            int total = 0;
            while (total < count)
            {
                int n = fs.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            if (total < count) Array.Resize(ref buf, total);
            return buf;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }
    }
}
