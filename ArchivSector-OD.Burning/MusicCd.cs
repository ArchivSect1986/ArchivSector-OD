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
    // onto a CD; anything else is refused with what it is instead.
    public static class MusicCd
    {
        public const long SectorsPer80Min = 359_849;   // what an 80-minute CD-R holds (the drive reports this for a blank)
        public const long SectorsPer74Min = 333_000;

        public sealed class Wav
        {
            public string Path = "";
            public string? Problem;            // why it can't go on a CD, if it can't
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
                            w.Problem = "It's " + string.Join(", ", wrong) + ". A music CD needs 44.1 kHz, 16-bit stereo: convert it first (Audacity, foobar2000 or fre:ac can).";
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
