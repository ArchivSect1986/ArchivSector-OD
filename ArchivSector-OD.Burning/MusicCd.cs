using System.Text;

namespace ArchivSector_OD.Burning
{
    // Music CD from WAV files (v4.0 beta 4). Reads what each .wav holds
    // (its length, and the title / artist / album a tagger saved in it), and
    // writes the .cue that lists them: one FILE per track, the 2-second gap
    // between tracks if wanted, and the album and track names as CD-Text.
    // The .cue is then burned like any other BIN/CUE (CueImage + the own
    // writer), so nothing here touches a drive.
    // Only plain CD audio (PCM, 44.1 kHz, 16-bit, stereo) can go straight
    // onto a CD. A .wav in another format is marked Convertible, and FLAC,
    // MP3 and other files are read for their names here (ReadTags); the app
    // converts them to CD audio (AudioDecoder) before the .cue is written.
    public static class MusicCd
    {
        public const long SectorsPer80Min = 359_849;   // what an 80-minute CD-R holds (the drive reports this for a blank)
        public const long SectorsPer74Min = 333_000;

        public sealed class Wav
        {
            public string Path = "";
            public string? Problem;            // why it can't go on a CD as it is, if it can't
            public bool Convertible;           // the problem is only its format (sample rate, bits, channels): converting fixes it
            public long DataBytes;             // the audio itself
            public string Title = "", Artist = "", Album = "";
            public long Sectors => (DataBytes + 2351) / 2352;   // the last part sector is filled with silence
            public TimeSpan Length => TimeSpan.FromSeconds(DataBytes / 176_400.0);
        }

        public sealed class Track
        {
            public string Path = "";
            public string Title = "";
            public string Performer = "";
        }

        /// <summary>What a .wav holds. Problem is set if it isn't CD audio or can't be read.</summary>
        public static Wav ReadWav(string path)
        {
            var w = new Wav { Path = path };
            string name = System.IO.Path.GetFileName(path);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                string Tag() => Encoding.ASCII.GetString(br.ReadBytes(4));
                if (fs.Length < 12 || Tag() != "RIFF") { w.Problem = "It isn't a WAV file."; return w; }
                br.ReadUInt32();
                if (Tag() != "WAVE") { w.Problem = "It isn't a WAV file."; return w; }
                bool fmtSeen = false, dataSeen = false;
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = Tag();
                    long size = br.ReadUInt32();
                    long start = fs.Position;
                    if (id == "fmt ")
                    {
                        fmtSeen = true;
                        int format = br.ReadUInt16(), channels = br.ReadUInt16();
                        long rate = br.ReadUInt32();
                        br.ReadUInt32(); br.ReadUInt16();
                        int bits = br.ReadUInt16();
                        if (format == 0xFFFE && size >= 40) { br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32(); format = br.ReadUInt16(); }   // extensible: the real format
                        var wrong = new List<string>();
                        if (format != 1) wrong.Add("compressed, not plain PCM");
                        if (rate != 44100) wrong.Add($"{rate / 1000.0:0.###} kHz instead of 44.1 kHz");
                        if (bits != 16) wrong.Add($"{bits}-bit instead of 16-bit");
                        if (channels != 2) wrong.Add(channels == 1 ? "mono instead of stereo" : $"{channels} channels instead of 2");
                        if (wrong.Count > 0)
                        {
                            w.Problem = "It's " + string.Join(", ", wrong) + ". A music CD needs 44.1 kHz, 16-bit stereo: convert it first (Audacity, foobar2000 or fre:ac can).";
                            w.Convertible = true;
                        }
                    }
                    else if (id == "data")
                    {
                        dataSeen = true;
                        long length = Math.Min(size == 0xFFFFFFFF ? long.MaxValue : size, fs.Length - start);
                        w.DataBytes = length - length % 4;
                        if (size == 0xFFFFFFFF) break;   // runs to the end of the file
                    }
                    else if (id == "LIST" && size >= 4)
                    {
                        if (Tag() == "INFO") ReadInfo(br, start + size, w);
                    }
                    fs.Position = start + size + (size & 1);   // chunks are padded to an even size
                }
                if (!fmtSeen && w.Problem is null) w.Problem = "It doesn't say what kind of audio it holds.";
                else if (!dataSeen && w.Problem is null) w.Problem = "It has no audio in it.";
                else if (w.DataBytes == 0 && w.Problem is null) w.Problem = "Its audio is empty.";
            }
            catch (Exception ex) { w.Problem = "It couldn't be read: " + ex.Message; }
            return w;
        }

        // LIST/INFO: INAM = title, IART = artist, IPRD = album.
        private static void ReadInfo(BinaryReader br, long end, Wav w)
        {
            var fs = br.BaseStream;
            while (fs.Position + 8 <= end)
            {
                string id = Encoding.ASCII.GetString(br.ReadBytes(4));
                long size = br.ReadUInt32();
                long start = fs.Position;
                if (start + size > end) break;
                if (id is "INAM" or "IART" or "IPRD")
                {
                    var text = Text(br.ReadBytes((int)Math.Min(size, 4096)));
                    if (id == "INAM") w.Title = text;
                    else if (id == "IART") w.Artist = text;
                    else w.Album = text;
                }
                fs.Position = start + size + (size & 1);
            }
        }

        // ---- names from FLAC and MP3 files (beta 4) ----

        /// <summary>
        /// The title, artist and album saved in a music file: FLAC (Vorbis
        /// comments), MP3 (ID3v2, or ID3v1 at the end), WAV (LIST/INFO).
        /// Empty strings for what isn't there or can't be read.
        /// </summary>
        public static (string Title, string Artist, string Album) ReadTags(string path)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".wav") { var w = ReadWav(path); return (w.Title, w.Artist, w.Album); }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var head = new byte[10];
                if (fs.Read(head, 0, 10) < 10) return ("", "", "");
                fs.Position = 0;
                if (head[0] == 'f' && head[1] == 'L' && head[2] == 'a' && head[3] == 'C') return FlacTags(fs);
                if (head[0] == 'I' && head[1] == 'D' && head[2] == '3') { var t = Id3v2Tags(fs); if (t.Title.Length + t.Artist.Length > 0) return t; }
                return Id3v1Tags(fs);
            }
            catch { return ("", "", ""); }
        }

        // FLAC: "fLaC", then metadata blocks (1 byte: last flag + type,
        // 3 bytes big-endian length); type 4 holds "KEY=value" comments.
        private static (string, string, string) FlacTags(FileStream fs)
        {
            string title = "", artist = "", album = "", albumArtist = "";
            fs.Position = 4;
            var h = new byte[4];
            for (int guard = 0; guard < 64 && fs.Read(h, 0, 4) == 4; guard++)
            {
                bool last = (h[0] & 0x80) != 0;
                int type = h[0] & 0x7F, len = (h[1] << 16) | (h[2] << 8) | h[3];
                long next = fs.Position + len;
                if (type == 4 && len > 8 && len < 16_000_000)
                {
                    var b = new byte[len];
                    if (fs.Read(b, 0, len) != len) break;
                    int p = 0;
                    int vendor = BitConverter.ToInt32(b, p); p += 4 + vendor;
                    if (p + 4 > len) break;
                    int count = BitConverter.ToInt32(b, p); p += 4;
                    for (int i = 0; i < count && p + 4 <= len; i++)
                    {
                        int n = BitConverter.ToInt32(b, p); p += 4;
                        if (n < 0 || p + n > len) break;
                        var c = Encoding.UTF8.GetString(b, p, n); p += n;
                        int eq = c.IndexOf('=');
                        if (eq <= 0) continue;
                        var key = c[..eq].ToUpperInvariant();
                        var val = c[(eq + 1)..].Trim();
                        if (key == "TITLE" && title.Length == 0) title = val;
                        else if (key == "ARTIST" && artist.Length == 0) artist = val;
                        else if (key == "ALBUM" && album.Length == 0) album = val;
                        else if (key == "ALBUMARTIST" && albumArtist.Length == 0) albumArtist = val;
                    }
                    break;
                }
                if (last) break;
                fs.Position = next;
            }
            return (title, artist.Length > 0 ? artist : albumArtist, album);
        }

        // ID3v2.2 / 2.3 / 2.4 text frames: title, artist, album.
        private static (string Title, string Artist, string Album) Id3v2Tags(FileStream fs)
        {
            var h = new byte[10];
            fs.Read(h, 0, 10);
            int ver = h[3];
            int flags = h[5];
            int size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9];
            if (size <= 0 || size > 64_000_000) return ("", "", "");
            var b = new byte[size];
            int got = fs.Read(b, 0, size);
            int p = 0;
            if ((flags & 0x40) != 0 && ver >= 3 && got >= 4)   // extended header
            {
                int ext = ver == 4 ? (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3] : ((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]) + 4;
                p = Math.Max(0, ext);
            }
            string title = "", artist = "", album = "", albumArtist = "";
            int idLen = ver == 2 ? 3 : 4, hdr = ver == 2 ? 6 : 10;
            while (p + hdr <= got)
            {
                if (b[p] == 0) break;   // padding
                string id = Encoding.ASCII.GetString(b, p, idLen);
                int len = ver == 2 ? (b[p + 3] << 16) | (b[p + 4] << 8) | b[p + 5]
                    : ver == 4 ? (b[p + 4] << 21) | (b[p + 5] << 14) | (b[p + 6] << 7) | b[p + 7]
                    : (b[p + 4] << 24) | (b[p + 5] << 16) | (b[p + 6] << 8) | b[p + 7];
                p += hdr;
                if (len <= 0 || p + len > got) break;
                string? text = id is "TIT2" or "TT2" or "TPE1" or "TP1" or "TALB" or "TAL" or "TPE2" or "TP2" ? Id3Text(b, p, len) : null;
                if (text is not null)
                {
                    if (id is "TIT2" or "TT2") title = text;
                    else if (id is "TPE1" or "TP1") artist = text;
                    else if (id is "TALB" or "TAL") album = text;
                    else albumArtist = text;
                }
                p += len;
            }
            return (title, artist.Length > 0 ? artist : albumArtist, album);
        }

        // A text frame: 1 byte for the encoding, then the text (several
        // values are split by a zero; the first is used).
        private static string Id3Text(byte[] b, int p, int len)
        {
            if (len < 2) return "";
            int enc = b[p];
            var raw = b.AsSpan(p + 1, len - 1).ToArray();
            string s;
            if (enc == 1)   // UTF-16 with a byte-order mark
            {
                if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) s = Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
                else if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) s = Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
                else s = Encoding.Unicode.GetString(raw);
            }
            else if (enc == 2) s = Encoding.BigEndianUnicode.GetString(raw);
            else if (enc == 3) s = Encoding.UTF8.GetString(raw);
            else s = Encoding.Latin1.GetString(raw);
            int z = s.IndexOf('\0');
            return (z >= 0 ? s[..z] : s).Trim();
        }

        // ID3v1: the last 128 bytes, "TAG", then 30 bytes each of title, artist, album.
        private static (string, string, string) Id3v1Tags(FileStream fs)
        {
            if (fs.Length < 128) return ("", "", "");
            fs.Position = fs.Length - 128;
            var b = new byte[128];
            if (fs.Read(b, 0, 128) < 128 || b[0] != 'T' || b[1] != 'A' || b[2] != 'G') return ("", "", "");
            string F(int o) { var s = Encoding.Latin1.GetString(b, o, 30); int z = s.IndexOf('\0'); return (z >= 0 ? s[..z] : s).Trim(); }
            return (F(3), F(33), F(63));
        }

        // Taggers write UTF-8 or the Windows code page; UTF-8 if it's valid.
        private static string Text(byte[] b)
        {
            int n = Array.IndexOf(b, (byte)0);
            if (n < 0) n = b.Length;
            try { return new UTF8Encoding(false, true).GetString(b, 0, n).Trim(); }
            catch { return Encoding.Latin1.GetString(b, 0, n).Trim(); }
        }

        /// <summary>A track name from a file name: "01 - Song.wav" → "Song".</summary>
        public static string TitleFromFileName(string path)
        {
            var s = System.IO.Path.GetFileNameWithoutExtension(path).Trim();
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^\d{1,3}\s*[-._)]?\s+(.+)$");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(s, @"^\d{1,3}\s*[-._)]\s*(.+)$");
            return m.Success ? m.Groups[1].Value.Trim() : s;
        }

        /// <summary>Every sector the CD will hold: the first 2-second gap, the tracks, and the gaps between them.</summary>
        public static long DiscSectors(IReadOnlyList<long> trackSectors, bool gaps) =>
            trackSectors.Count == 0 ? 0 : 150 + trackSectors.Sum() + (gaps ? 150L * (trackSectors.Count - 1) : 0);

        /// <summary>
        /// The .cue for the tracks. A .wav in the .cue's own folder is listed
        /// by name, any other by its full path. Names go in as CD-Text when
        /// cdText is on and they aren't empty.
        /// </summary>
        public static string BuildCue(string albumTitle, string albumArtist, IReadOnlyList<Track> tracks, bool gaps, bool cdText, string cueFolder)
        {
            var sb = new StringBuilder();
            sb.AppendLine("REM Made by ArchivSector-OD (music CD from WAV files)");
            if (cdText && Clean(albumArtist).Length > 0) sb.AppendLine($"PERFORMER \"{Clean(albumArtist)}\"");
            if (cdText && Clean(albumTitle).Length > 0) sb.AppendLine($"TITLE \"{Clean(albumTitle)}\"");
            string folder = System.IO.Path.GetFullPath(cueFolder).TrimEnd('\\', '/');
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var full = System.IO.Path.GetFullPath(t.Path);
                var dir = System.IO.Path.GetDirectoryName(full)?.TrimEnd('\\', '/') ?? "";
                var listed = string.Equals(dir, folder, StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileName(full) : full;
                sb.AppendLine($"FILE \"{listed}\" WAVE");
                sb.AppendLine($"  TRACK {i + 1:D2} AUDIO");
                if (cdText && Clean(t.Title).Length > 0) sb.AppendLine($"    TITLE \"{Clean(t.Title)}\"");
                if (cdText && Clean(t.Performer).Length > 0) sb.AppendLine($"    PERFORMER \"{Clean(t.Performer)}\"");
                if (gaps && i > 0) sb.AppendLine("    PREGAP 00:02:00");
                sb.AppendLine("    INDEX 01 00:00:00");
            }
            return sb.ToString();
        }

        /// <summary>Writes the .cue: plain text, UTF-8 (with a BOM only if a name needs it, as ImgBurn likes).</summary>
        public static void SaveCue(string cuePath, string text)
        {
            bool ascii = text.All(c => c < 128);
            File.WriteAllText(cuePath, text, ascii ? new UTF8Encoding(false) : new UTF8Encoding(true));
        }

        // A name inside quotes in a .cue: no quotes or control characters.
        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s.Trim())
                sb.Append(c == '"' ? '\'' : char.IsControl(c) ? ' ' : c);
            return sb.ToString().Trim();
        }
    }
}
