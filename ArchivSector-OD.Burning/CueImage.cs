using System.Text;
using System.Text.RegularExpressions;

namespace ArchivSector_OD.Burning
{
    // A BIN/CUE CD image (v4.0 beta 2), read so ArchivSector-OD's own writer
    // can burn it "session at once". Handles what Redumper and most rippers
    // write: one .bin per track or one .bin for the whole disc, data tracks
    // (MODE1/2352, MODE2/2352, MODE1/2048, MODE2/2336) and music tracks
    // (AUDIO), INDEX 00 pregaps inside the .bin, and PREGAP/POSTGAP silence
    // that isn't in the .bin. Music can also come from .wav files (plain CD
    // audio: 44.1 kHz, 16-bit, stereo); a .wav that doesn't end on a whole
    // sector gets silence to fill the last one. Anything else (FLAC/MP3
    // files, CD+G...) gives a clear Problem, and ImgBurn can burn those instead.
    //
    // Positions: LBA 0 is where the disc's first track starts by default
    // (00:02:00). Every disc starts with a 150-sector gap before that
    // (LBA -150 to -1), which is written as zeros when the .bin doesn't hold it.
    public sealed class CueImage
    {
        public enum TrackMode { Audio, Mode1Raw, Mode2Raw, Mode1Cooked, Mode2Cooked }

        public sealed class Track
        {
            public int Number;
            public TrackMode Mode;
            public bool PreEmphasis, CopyPermitted;
            public long PregapLba;                       // index 00 (= Index1Lba when there's no pregap)
            public long Index1Lba;                       // where the track starts
            public List<long> MoreIndexLbas = new();     // index 02, 03...
            public long EndLba;                          // first sector after the track
            public int Session = 1;                      // Enhanced CD / CD-Extra: 2 for the data session
            public bool IsAudio => Mode == TrackMode.Audio;
            public long Sectors => EndLba - Index1Lba;
        }

        // One stretch of sectors to write, in disc order: from a file, or zeros.
        public sealed class Piece
        {
            public long StartLba;
            public long Count;
            public TrackMode Mode;
            public string? File;                         // null = zeros
            public long FileOffset;                      // bytes
            public int FileSectorSize;                   // 2352, 2048 or 2336
            public long FileDataEnd = long.MaxValue;     // bytes: past this (end of a .wav's audio) is silence
            public bool SwapAudio;                       // MOTOROLA (big-endian) audio
        }

        public string CuePath = "";
        public List<Track> Tracks = new();
        public List<Piece> Pieces = new();
        public string? Problem;                          // why it can't be burned with the own writer
        public int Sessions = 1;                         // "REM SESSION nn" lines (an Enhanced CD / CD-Extra has 2)
        /// <summary>Album and track names from TITLE / PERFORMER / SONGWRITER lines (CD-Text), or null if there are none (beta 4).</summary>
        public CdText? CdText;
        /// <summary>The tracks of one session (1, 2...).</summary>
        public List<Track> SessionTracks(int session) => Tracks.Where(t => t.Session == session).ToList();
        /// <summary>The pieces written in one session (a session's lead-out and the next lead-in aren't written by us).</summary>
        public List<Piece> SessionPieces(int session)
        {
            var ts = SessionTracks(session);
            if (ts.Count == 0) return new();
            long from = ts[0].PregapLba, to = ts[^1].EndLba;
            return Pieces.Where(p => p.StartLba >= from && p.StartLba < to).ToList();
        }
        public long FirstLba => Pieces.Count > 0 ? Pieces[0].StartLba : -150;
        public long LeadOutLba => Tracks.Count > 0 ? Tracks[^1].EndLba : 0;
        public bool HasData => Tracks.Any(t => !t.IsAudio);
        public bool HasAudio => Tracks.Any(t => t.IsAudio);
        public bool HasMode2 => Tracks.Any(t => t.Mode is TrackMode.Mode2Raw or TrackMode.Mode2Cooked);
        public long BurnSectors => Pieces.Sum(p => p.Count);   // every sector written, gaps included (not the space between sessions)

        /// <summary>The size of a sector as the writer sends it (the drive adds the rest).</summary>
        public static int BlockSize(TrackMode m) => m switch
        {
            TrackMode.Audio => 2352,
            TrackMode.Mode1Raw or TrackMode.Mode1Cooked => 2048,
            _ => 2336,
        };

        public string Summary
        {
            get
            {
                int data = Tracks.Count(t => !t.IsAudio), audio = Tracks.Count - data;
                var parts = new List<string>();
                if (data > 0) parts.Add($"{data} data track{(data == 1 ? "" : "s")}");
                if (audio > 0) parts.Add($"{audio} music track{(audio == 1 ? "" : "s")}");
                long minutes = (LeadOutLba + 150) / 75 / 60, seconds = (LeadOutLba + 150) / 75 % 60;
                return $"{string.Join(", ", parts)}{(Sessions > 1 ? $" in {Sessions} sessions (Enhanced CD)" : "")} · {minutes}:{seconds:D2} long ({LeadOutLba + 150:N0} sectors)";
            }
        }

        private static readonly Regex Msf = new(@"^(\d+):(\d{1,2}):(\d{1,2})$");

        private static long ParseMsf(string s)
        {
            var m = Msf.Match(s.Trim());
            if (!m.Success) throw new FormatException($"\"{s}\" isn't a time like 00:02:00");
            return (long.Parse(m.Groups[1].Value) * 60 + long.Parse(m.Groups[2].Value)) * 75 + long.Parse(m.Groups[3].Value);
        }

        // "FILE "name with spaces.bin" BINARY" → name, type
        private static (string Name, string Type) SplitFile(string rest)
        {
            rest = rest.Trim();
            string name, type;
            if (rest.StartsWith('"'))
            {
                int end = rest.IndexOf('"', 1);
                if (end < 0) throw new FormatException("a FILE line has an unclosed quote");
                name = rest[1..end];
                type = rest[(end + 1)..].Trim();
            }
            else
            {
                int sp = rest.LastIndexOf(' ');
                name = sp > 0 ? rest[..sp] : rest;
                type = sp > 0 ? rest[(sp + 1)..] : "BINARY";
            }
            return (name, type.ToUpperInvariant());
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
        }

        private sealed class RawTrack
        {
            public int Number;
            public TrackMode Mode;
            public int SectorSize;
            public bool Pre, Dcp;
            public int FileIndex;
            public long Pregap, Postgap;                 // generated sectors (PREGAP / POSTGAP)
            public SortedDictionary<int, long> Indexes = new();   // index → sector in its file
            public int Session = 1;
            public long SessionGap;                      // first track of a later session: the previous lead-out + this lead-in (not written by us)
        }

        /// <summary>Reads a .cue and works out every sector's position. Problem says what's wrong, if anything.</summary>
        public static CueImage Load(string cuePath)
        {
            var img = new CueImage { CuePath = cuePath };
            try { Parse(img, cuePath); }
            catch (Exception ex) { img.Problem = ex is FormatException or FileNotFoundException or IOException ? ex.Message : "The .cue couldn't be read: " + ex.Message; }
            if (img.Problem is null && img.Tracks.Count == 0) img.Problem = "The .cue doesn't list any tracks.";

            return img;
        }

        private static void Parse(CueImage img, string cuePath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cuePath)) ?? ".";
            var files = new List<(string Path, string Type, long Offset, long Length)>();
            var tracks = new List<RawTrack>();
            RawTrack? cur = null;
            int lineNo = 0;
            // Sessions, ImgBurn style: REM SESSION nn, then before the next
            // session's first track REM LEAD-OUT (of the one before), REM
            // LEAD-IN and REM PREGAP. Standard lengths if they're left out.
            int session = 1;
            long? remLeadOut = null, remLeadIn = null, remPregap = null;

            foreach (var raw in File.ReadAllLines(cuePath, Encoding.UTF8))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                string cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                string rest = sp < 0 ? "" : line[(sp + 1)..].Trim();
                switch (cmd)
                {
                    case "FILE":
                    {
                        var (name, type) = SplitFile(rest);
                        string ext = Path.GetExtension(name).ToLowerInvariant();
                        if (type == "WAVE" && ext is not ".wav")
                            throw new FormatException($"The .cue's music is in {ext.TrimStart('.').ToUpperInvariant()} files ({name}). ArchivSector-OD's own writer burns .bin and .wav: convert them to WAV (foobar2000 or fre:ac can, keeping the .cue), or burn with ImgBurn (it needs its FLAC/MP3 add-on for these).");
                        if (type is not ("BINARY" or "MOTOROLA" or "WAVE"))
                            throw new FormatException($"The .cue uses a {type} file ({name}). ArchivSector-OD's own writer burns .bin and .wav files; ImgBurn can burn this one.");
                        var full = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
                        if (!File.Exists(full))
                        {
                            // Some tools write the .bin name in a different case, or keep an old folder.
                            var alt = Path.Combine(dir, Path.GetFileName(name));
                            if (File.Exists(alt)) full = alt;
                            else throw new FileNotFoundException($"The .cue lists \"{name}\", but that file isn't next to it.");
                        }
                        if (type == "WAVE")
                        {
                            var (offset, length) = ReadWav(full);
                            files.Add((full, type, offset, length));
                        }
                        else files.Add((full, type, 0, new FileInfo(full).Length));
                        cur = null;
                        break;
                    }
                    case "TRACK":
                    {
                        if (files.Count == 0) throw new FormatException($"Line {lineNo}: a TRACK comes before any FILE.");
                        var bits = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (bits.Length < 2 || !int.TryParse(bits[0], out int no) || no < 1 || no > 99)
                            throw new FormatException($"Line {lineNo}: \"{line}\" isn't a track ArchivSector-OD understands.");
                        var (mode, size) = bits[1].ToUpperInvariant() switch
                        {
                            "AUDIO" => (TrackMode.Audio, 2352),
                            "MODE1/2352" => (TrackMode.Mode1Raw, 2352),
                            "MODE2/2352" => (TrackMode.Mode2Raw, 2352),
                            "MODE1/2048" => (TrackMode.Mode1Cooked, 2048),
                            "MODE2/2336" => (TrackMode.Mode2Cooked, 2336),
                            _ => throw new FormatException($"Track {no} is {bits[1]}, which ArchivSector-OD's own writer doesn't burn yet. ImgBurn can."),
                        };
                        if (files[^1].Type == "WAVE" && mode != TrackMode.Audio)
                            throw new FormatException($"Track {no} is a data track in a .wav file; a .wav can only hold music.");
                        cur = new RawTrack { Number = no, Mode = mode, SectorSize = size, FileIndex = files.Count - 1, Session = session };
                        if (session > 1 && tracks.Count > 0 && tracks[^1].Session != session)
                        {
                            cur.SessionGap = (remLeadOut ?? (tracks[^1].Session == 1 ? 6750 : 2250)) + (remLeadIn ?? 4500);
                            cur.Pregap += remPregap ?? 150;
                            remLeadOut = remLeadIn = remPregap = null;
                        }
                        tracks.Add(cur);
                        break;
                    }
                    case "INDEX":
                    {
                        if (cur is null) throw new FormatException($"Line {lineNo}: an INDEX comes before any TRACK.");
                        var bits = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (bits.Length < 2 || !int.TryParse(bits[0], out int idx) || idx < 0 || idx > 99)
                            throw new FormatException($"Line {lineNo}: \"{line}\" isn't an index ArchivSector-OD understands.");
                        cur.Indexes[idx] = ParseMsf(bits[1]);
                        break;
                    }
                    case "PREGAP":
                        if (cur is null) throw new FormatException($"Line {lineNo}: a PREGAP comes before any TRACK.");
                        cur.Pregap = ParseMsf(rest);
                        break;
                    case "POSTGAP":
                        if (cur is null) throw new FormatException($"Line {lineNo}: a POSTGAP comes before any TRACK.");
                        cur.Postgap = ParseMsf(rest);
                        break;
                    case "FLAGS":
                        if (cur is not null)
                        {
                            var f = rest.ToUpperInvariant();
                            cur.Pre = f.Contains("PRE");
                            cur.Dcp = f.Contains("DCP");
                        }
                        break;
                    // CD-Text: before any TRACK it's the album's, under a TRACK that track's.
                    case "TITLE":
                    case "PERFORMER":
                    case "SONGWRITER":
                        (img.CdText ??= new CdText()).Set(cmd, cur?.Number ?? 0, Unquote(rest));
                        break;
                    // "REM SESSION 02" (ImgBurn's way of marking a disc's sessions),
                    // and REM COMPOSER / ARRANGER / MESSAGE (more CD-Text).
                    case "REM":
                    {
                        var bits = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (bits.Length < 2) break;
                        string remField = bits[0].ToUpperInvariant();
                        if (remField is "COMPOSER" or "ARRANGER" or "MESSAGE")
                        {
                            (img.CdText ??= new CdText()).Set(remField, cur?.Number ?? 0, Unquote(rest[bits[0].Length..].Trim()));
                            break;
                        }
                        switch (bits[0].ToUpperInvariant())
                        {
                            case "SESSION" when int.TryParse(bits[1], out int sNo) && sNo >= 1:
                                if (sNo != session && sNo != session + 1) throw new FormatException($"Line {lineNo}: session {sNo} comes after session {session}.");
                                session = sNo;
                                img.Sessions = Math.Max(img.Sessions, session);
                                break;
                            case "LEAD-OUT": remLeadOut = ParseMsf(bits[1]); break;
                            case "LEAD-IN": remLeadIn = ParseMsf(bits[1]); break;
                            case "PREGAP": remPregap = ParseMsf(bits[1]); break;
                        }
                        break;
                    }
                    // Information only, nothing to write: CATALOG, TITLE, PERFORMER, SONGWRITER, ISRC, CDTEXTFILE.
                    default:
                        break;
                }
            }

            if (tracks.Count == 0) return;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (!t.Indexes.ContainsKey(1)) throw new FormatException($"Track {t.Number} has no INDEX 01 (where the track starts).");
                if (i > 0 && t.Number != tracks[i - 1].Number + 1) throw new FormatException($"Track {t.Number} comes after track {tracks[i - 1].Number}: the tracks must be numbered in order.");
                if (t.Indexes.Keys.Zip(t.Indexes.Keys.Skip(1)).Any(p => t.Indexes[p.Second] < t.Indexes[p.First]))
                    throw new FormatException($"Track {t.Number}'s indexes aren't in order.");
            }

            // Where each track's data is in its file: from its first index (00 if
            // there is one) to the next track's first index in the same file, or
            // to the end of the file.
            long FirstInFile(RawTrack t) => t.Indexes.First().Value;
            for (int f = 0; f < files.Count; f++)
            {
                int size = tracks.Where(t => t.FileIndex == f).Select(t => t.SectorSize).DefaultIfEmpty(2352).First();
                if (tracks.Where(t => t.FileIndex == f).Any(t => t.SectorSize != size))
                    throw new FormatException($"{Path.GetFileName(files[f].Path)} mixes sector sizes. ArchivSector-OD's own writer can't burn that; ImgBurn can.");
                if (files[f].Type != "WAVE" && files[f].Length % size != 0)
                    throw new FormatException($"{Path.GetFileName(files[f].Path)} isn't a whole number of {size}-byte sectors, so it doesn't match the .cue.");
            }

            // Lay the disc out: LBA -150 first.
            long lba = -150;
            var pieces = new List<Piece>();
            var outTracks = new List<Track>();
            long dataEnd = long.MaxValue;
            void Add(long count, TrackMode mode, string? file, long offset, int fsize, bool swap)
            {
                if (count <= 0) return;
                pieces.Add(new Piece { StartLba = lba, Count = count, Mode = mode, File = file, FileOffset = offset, FileSectorSize = fsize, SwapAudio = swap,
                                       FileDataEnd = file is null ? long.MaxValue : dataEnd });
                lba += count;
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var file = files[t.FileIndex];
                long first = FirstInFile(t), idx1 = t.Indexes[1];
                long end = i + 1 < tracks.Count && tracks[i + 1].FileIndex == t.FileIndex
                    ? FirstInFile(tracks[i + 1])
                    : (file.Length + t.SectorSize - 1) / t.SectorSize;   // a .wav's last part sector is filled with silence
                if (end < idx1) throw new FormatException($"Track {t.Number} ends before it starts: the .cue doesn't match {Path.GetFileName(file.Path)}.");
                bool swap = file.Type == "MOTOROLA" && t.Mode == TrackMode.Audio;
                dataEnd = file.Offset + file.Length;

                // A later session: past the previous lead-out and this lead-in,
                // which the drive writes itself.
                if (i > 0 && t.Session != tracks[i - 1].Session) lba += t.SessionGap;
                var track = new Track { Number = t.Number, Mode = t.Mode, PreEmphasis = t.Pre, CopyPermitted = t.Dcp, Session = t.Session };
                long pregapInFile = idx1 - first;
                long generated = t.Pregap;
                // The first track always has at least the standard 150-sector gap.
                if (i == 0 && pregapInFile + generated < 150) generated = 150 - pregapInFile;
                // A hidden track before track 1 (beta 4): music in track 1's gap,
                // more than the standard 2 seconds of it. As EAC rips it and
                // ImgBurn burns it, the .bin starts at sector 0: the standard
                // 2 seconds before that are silence, then the hidden music,
                // then track 1 proper at INDEX 01.
                else if (i == 0 && t.Mode == TrackMode.Audio && pregapInFile > 150) generated = Math.Max(generated, 150);

                track.PregapLba = lba;
                Add(generated, t.Mode, null, 0, t.SectorSize, false);
                Add(pregapInFile, t.Mode, file.Path, file.Offset + first * t.SectorSize, t.SectorSize, swap);
                track.Index1Lba = lba;
                foreach (var kv in t.Indexes.Where(k => k.Key >= 2)) track.MoreIndexLbas.Add(track.Index1Lba + (kv.Value - idx1));
                Add(end - idx1, t.Mode, file.Path, file.Offset + idx1 * t.SectorSize, t.SectorSize, swap);
                Add(t.Postgap, t.Mode, null, 0, t.SectorSize, false);
                track.EndLba = lba;
                if (track.EndLba - track.Index1Lba < 300) throw new FormatException($"Track {t.Number} is shorter than 4 seconds, the shortest track a CD allows.");
                outTracks.Add(track);
            }
            img.Tracks = outTracks;
            img.Pieces = pieces;
        }

        // Where a .wav's audio starts and how long it is. Only plain CD audio
        // (PCM, 44.1 kHz, 16-bit, 2 channels) can go straight onto a CD.
        private static (long Offset, long Length) ReadWav(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);
            string Tag() => Encoding.ASCII.GetString(br.ReadBytes(4));
            string name = Path.GetFileName(path);
            if (fs.Length < 12 || Tag() != "RIFF") throw new FormatException($"{name} isn't a WAV file.");
            br.ReadUInt32();
            if (Tag() != "WAVE") throw new FormatException($"{name} isn't a WAV file.");
            bool fmtOk = false;
            while (fs.Position + 8 <= fs.Length)
            {
                string id = Tag();
                long size = br.ReadUInt32();
                long start = fs.Position;
                if (id == "fmt ")
                {
                    int format = br.ReadUInt16(), channels = br.ReadUInt16();
                    long rate = br.ReadUInt32();
                    br.ReadUInt32(); br.ReadUInt16();
                    int bits = br.ReadUInt16();
                    if (format == 0xFFFE && size >= 40) { br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32(); format = br.ReadUInt16(); }   // extensible: the real format
                    if (format != 1 || channels != 2 || rate != 44100 || bits != 16)
                        throw new FormatException($"{name} is {rate / 1000.0:0.#} kHz, {bits}-bit, {channels} channel(s). A CD needs 44.1 kHz, 16-bit stereo: convert it to that first.");
                    fmtOk = true;
                }
                else if (id == "data")
                {
                    if (!fmtOk) throw new FormatException($"{name} doesn't say what kind of audio it holds.");
                    long length = Math.Min(size == 0xFFFFFFFF ? long.MaxValue : size, fs.Length - start);
                    return (start, length - length % 4);
                }
                fs.Position = start + size + (size & 1);   // chunks are padded to an even size
            }
            throw new FormatException($"{name} has no audio in it.");
        }

        /// <summary>
        /// The MMC "cue sheet" the drive needs before a session-at-once burn:
        /// 8 bytes per entry (lead-in, each track's index 00 and 01 and any
        /// more, lead-out). Track and index numbers are plain numbers; times
        /// are minutes/seconds/frames counted from the start of the disc
        /// (LBA + 150).
        /// </summary>
        /// <param name="cdTextInLeadIn">CD-Text (beta 4): the host writes the lead-in's R-W sub-channel (the lead-in entry's sub-channel form 01).</param>
        public byte[] BuildCueSheet(int session = 1, bool cdTextInLeadIn = false)
        {
            var tracks = SessionTracks(session);
            var e = new List<byte[]>();
            static byte DataForm(TrackMode m) => m switch
            {
                TrackMode.Audio => 0x00,                                    // CD-DA, 2352 bytes from the host
                TrackMode.Mode1Raw or TrackMode.Mode1Cooked => 0x10,        // CD-ROM mode 1, 2048 bytes; the drive adds header and error correction
                _ => 0x20,                                                  // CD-ROM XA (mode 2), 2336 bytes: sub-header and data
            };
            static byte LeadForm(TrackMode m) => m switch
            {
                TrackMode.Audio => 0x01,                                    // made by the drive
                TrackMode.Mode1Raw or TrackMode.Mode1Cooked => 0x14,
                _ => 0x24,
            };
            static byte Ctl(Track t) => (byte)(t.IsAudio ? (t.PreEmphasis ? 0x10 : 0) | (t.CopyPermitted ? 0x20 : 0) : 0x40);
            byte[] Entry(byte ctlAdr, byte tno, byte index, byte form, long lba)
            {
                long msf = lba + 150;
                return new[] { ctlAdr, tno, index, form, (byte)0, (byte)(msf / 75 / 60), (byte)(msf / 75 % 60), (byte)(msf % 75) };
            }

            static byte LeadCtl(Track t) => (byte)(t.IsAudio ? 0x01 : 0x41);   // lead-in/out: only "data or music"
            // The lead-in, at the start of the session's first gap (LBA -150 on
            // a one-session disc; a later session starts after the earlier
            // ones). Then each track, then the lead-out.
            var first = tracks[0];
            e.Add(Entry(LeadCtl(first), 0, 0, (byte)(LeadForm(first.Mode) | (cdTextInLeadIn ? 0x40 : 0)), first.PregapLba));
            foreach (var t in tracks)
            {
                byte ca = (byte)(Ctl(t) | 0x01), tno = (byte)t.Number, form = DataForm(t.Mode);
                if (t.PregapLba < t.Index1Lba) e.Add(Entry(ca, tno, 0, form, t.PregapLba));
                e.Add(Entry(ca, tno, 1, form, t.Index1Lba));
                for (int i = 0; i < t.MoreIndexLbas.Count; i++) e.Add(Entry(ca, tno, (byte)(i + 2), form, t.MoreIndexLbas[i]));
            }
            var last = tracks[^1];
            e.Add(Entry(LeadCtl(last), 0xAA, 1, LeadForm(last.Mode), last.EndLba));   // lead-out
            return e.SelectMany(b => b).ToArray();
        }

        /// <summary>The track a sector belongs to (its pregap counts as the track's).</summary>
        public Track TrackAt(long lba) => Tracks.LastOrDefault(t => t.PregapLba <= lba) ?? Tracks[0];
    }
}
