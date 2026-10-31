using System.Text;

namespace ArchivSector_OD.Burning
{
    // CD-Text (v4.0 beta 4): the album and track names some music CDs carry
    // in their lead-in, which car stereos and players show. Read with READ
    // TOC format 5, written (later) by the own writer from a .cue's TITLE /
    // PERFORMER / SONGWRITER lines.
    //
    // The data is a list of 18-byte "packs":
    //   0     pack type: 0x80 title, 0x81 performer, 0x82 songwriter,
    //         0x83 composer, 0x84 arranger, 0x85 message, 0x86 disc id,
    //         0x87 genre, 0x8E UPC/EAN and ISRC codes, 0x8F size info
    //   1     track number the first character belongs to (0 = the album)
    //   2     sequence number (0, 1, 2... across all packs)
    //   3     bits 4-6: block (language) number; bits 0-3: how many
    //         characters of the current string came in earlier packs
    //   4-15  12 bytes of text: the strings one after another, each ended
    //         by a zero (album first, then track 1, 2...). A tab means
    //         "the same as the track before".
    //   16-17 CRC-16 (CCITT) of bytes 0-15, inverted
    // Only block 0 (the first language) is used; text is ISO-8859-1.
    public sealed class CdText
    {
        public static readonly (byte Type, string Field)[] Fields =
        {
            (0x80, "TITLE"), (0x81, "PERFORMER"), (0x82, "SONGWRITER"),
            (0x83, "COMPOSER"), (0x84, "ARRANGER"), (0x85, "MESSAGE"),
        };

        /// <summary>Field ("TITLE", "PERFORMER"...) → track (0 = the album) → text.</summary>
        public Dictionary<string, Dictionary<int, string>> Text = new();
        /// <summary>UPC/EAN of the album (track 0) and ISRC of each track, if the CD-Text has them.</summary>
        public Dictionary<int, string> Codes = new();
        public int BadPacks;

        public bool HasAny => Text.Values.Any(d => d.Values.Any(v => v.Length > 0));
        public string Get(string field, int track) =>
            Text.TryGetValue(field, out var d) && d.TryGetValue(track, out var v) ? v : "";

        public void Set(string field, int track, string value)
        {
            if (!Text.TryGetValue(field, out var d)) Text[field] = d = new();
            d[track] = value;
        }

        public int LastTrack => Text.Values.SelectMany(d => d.Keys).DefaultIfEmpty(0).Max();

        private static readonly Encoding Latin1 = Encoding.Latin1;

        // ---- reading ----

        /// <summary>The CD-Text on the disc in the drive, or null if it has none.</summary>
        public static CdText? Read(ScsiDrive drive)
        {
            var head = new byte[4];
            var rep = drive.Read(new byte[] { 0x43, 0x00, 0x05, 0, 0, 0, 0, 0, 4, 0 }, head, 10);
            if (!rep.Ok) return null;
            int len = ((head[0] << 8) | head[1]) + 2;
            if (len < 4 + 18) return null;
            var buf = new byte[Math.Min(len, 4 + 18 * 2048)];
            rep = drive.Read(new byte[] { 0x43, 0x00, 0x05, 0, 0, 0, 0, (byte)(buf.Length >> 8), (byte)buf.Length, 0 }, buf, 10);
            if (!rep.Ok) return null;
            var packs = new List<byte[]>();
            for (int o = 4; o + 18 <= buf.Length && o + 18 <= len; o += 18) packs.Add(buf.AsSpan(o, 18).ToArray());
            var t = FromPacks(packs);
            return t.HasAny || t.Codes.Count > 0 ? t : null;
        }

        /// <summary>Turns packs into text. Packs with a bad CRC are skipped (and counted).</summary>
        public static CdText FromPacks(List<byte[]> packs)
        {
            var t = new CdText();
            var good = new List<byte[]>();
            foreach (var p in packs)
            {
                if (p.Length < 18) continue;
                ushort crc = (ushort)~Crc16(p, 16);
                if (p[16] != (crc >> 8) || p[17] != (crc & 0xFF)) { t.BadPacks++; continue; }
                if (((p[3] >> 4) & 7) != 0) continue;              // other languages
                good.Add(p);
            }
            foreach (var group in good.GroupBy(p => p[0]))
            {
                byte type = group.Key;
                string? field = Fields.FirstOrDefault(f => f.Type == type).Field;
                if (field is null && type != 0x8E) continue;
                var ordered = group.OrderBy(p => p[2]).ToList();
                var bytes = new List<byte>();
                foreach (var p in ordered) bytes.AddRange(p.AsSpan(4, 12).ToArray());
                int track = ordered[0][1] & 0x7F;
                // Strings end with a zero. Trailing zeros only pad the last pack.
                var strings = new List<string>();
                int start = 0;
                for (int i = 0; i < bytes.Count; i++)
                {
                    if (bytes[i] != 0) continue;
                    strings.Add(Latin1.GetString(bytes.GetRange(start, i - start).ToArray()));
                    start = i + 1;
                }
                string prev = "";
                foreach (var s in strings)
                {
                    if (track > 99) break;
                    string v = s == "\t" ? prev : s;
                    if (field is not null) t.Set(field, track, v.Trim());
                    else if (v.Trim().Length > 0) t.Codes[track] = v.Trim();
                    prev = v;
                    track++;
                }
            }
            // Drop entries past the last track that has anything (zero padding).
            foreach (var d in t.Text.Values)
                foreach (var k in d.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList())
                    if (k > d.Where(kv => kv.Value.Length > 0).Select(kv => kv.Key).DefaultIfEmpty(-1).Max()) d.Remove(k);
            return t;
        }

        // ---- writing ----

        /// <summary>
        /// The packs for this text, block 0, ISO-8859-1, for tracks 1 to lastTrack,
        /// ending with the three size-info packs. Fields with no text at all
        /// are left out; a track without a value for a field gets an empty string.
        /// </summary>
        public List<byte[]> ToPacks(int lastTrack)
        {
            var packs = new List<byte[]>();
            var counts = new int[16];
            int seq = 0;
            foreach (var (type, field) in Fields)
            {
                if (!Text.TryGetValue(field, out var d) || !d.Values.Any(v => v.Length > 0)) continue;
                // The strings, each with its track number and zero at the end.
                var chars = new List<(byte B, int Track, int Pos)>();
                for (int track = 0; track <= lastTrack; track++)
                {
                    var bytes = Latin1.GetBytes(Clean(d.TryGetValue(track, out var v) ? v : ""));
                    for (int i = 0; i < bytes.Length; i++) chars.Add((bytes[i], track, i));
                    chars.Add((0, track, bytes.Length));
                }
                for (int at = 0; at < chars.Count; at += 12)
                {
                    var p = new byte[18];
                    p[0] = type;
                    p[1] = (byte)chars[at].Track;
                    p[2] = (byte)seq++;
                    p[3] = (byte)Math.Min(15, chars[at].Pos);       // block 0; characters of this string in earlier packs
                    for (int i = 0; i < 12; i++) p[4 + i] = at + i < chars.Count ? chars[at + i].B : (byte)0;
                    packs.Add(p);
                    counts[type & 0x0F]++;
                }
            }
            if (packs.Count == 0) return packs;
            counts[0x0F] = 3;
            int lastSeq = seq + 2;
            // Size info: 3 packs (36 bytes): character set, first and last track,
            // copyright flag, the number of packs of each type, the last sequence
            // number of each block, and each block's language (0x09 = English).
            var info = new byte[36];
            info[0] = 0x00;                       // ISO-8859-1
            info[1] = 1;
            info[2] = (byte)lastTrack;
            info[3] = 0;                          // no copyright flags
            for (int i = 0; i < 16; i++) info[4 + i] = (byte)counts[i];
            info[20] = (byte)lastSeq;             // block 0's last sequence number
            info[28] = 0x09;                      // block 0: English
            for (int k = 0; k < 3; k++)
            {
                var p = new byte[18];
                p[0] = 0x8F;
                p[1] = (byte)k;
                p[2] = (byte)seq++;
                p[3] = 0;
                Array.Copy(info, k * 12, p, 4, 12);
                packs.Add(p);
            }
            foreach (var p in packs)
            {
                ushort crc = (ushort)~Crc16(p, 16);
                p[16] = (byte)(crc >> 8);
                p[17] = (byte)crc;
            }
            return packs;
        }

        // Latin-1 only, no control characters (a tab would mean "same as before").
        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s.Normalize(NormalizationForm.FormC))
                sb.Append(c < 0x20 || c > 0xFF ? (c is '‘' or '’' ? '\'' : c is '“' or '”' ? '"' : c is '–' or '—' ? '-' : c < 0x20 ? ' ' : '?') : c);
            return sb.ToString().Trim();
        }

        // CRC-16 CCITT (x^16 + x^12 + x^5 + 1), starting at 0.
        public static ushort Crc16(byte[] data, int count)
        {
            ushort crc = 0;
            for (int i = 0; i < count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int b = 0; b < 8; b++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }

        /// <summary>For the log and Drive info: "Album — Artist" style lines.</summary>
        public IEnumerable<(string Label, string Value)> Lines()
        {
            string Both(int track)
            {
                string title = Get("TITLE", track), who = Get("PERFORMER", track);
                return title.Length > 0 && who.Length > 0 ? $"{title} — {who}" : title.Length > 0 ? title : who;
            }
            var album = Both(0);
            if (album.Length > 0) yield return ("Album", album);
            for (int track = 1; track <= Math.Min(99, LastTrack); track++)
            {
                var line = Both(track);
                if (line.Length > 0) yield return ($"Track {track}", line);
            }
            if (Codes.TryGetValue(0, out var upc)) yield return ("UPC/EAN", upc);
        }
    }
}
