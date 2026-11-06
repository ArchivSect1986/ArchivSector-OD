using System.Diagnostics;
using System.Net.Http;

namespace ArchivSector_OD.Burning
{
    // AccurateRip check (v4.0 beta 4): are a BIN/CUE's music tracks the same
    // as other people's copies of the same CD? AccurateRip keeps, for each
    // CD, checksums of every music track from rips made with EAC,
    // dBpoweramp and others. A track that matches is a bit-perfect copy;
    // the "confidence" is how many rips agree.
    //
    // How (the same as whipper / CUETools):
    //  - the CD's IDs come from where its tracks start (the TOC), and say
    //    which file to fetch from accuraterip.com;
    //  - each music track's checksums: every stereo sample as one 32-bit
    //    number times its position in the track, added up (v1: the low
    //    32 bits; v2: the low and high halves added). The first 5 sectors of
    //    track 1 and the last 5 of the last track are left out;
    //  - every drive reads music a few samples early or late (its "read
    //    offset"), so the checksums are tried at every shift up to ±3000
    //    samples (cheaply: one pass per track, then slid along), and the
    //    shift most tracks agree on is used. v1 slides; v2 doesn't, so for
    //    a CD the database only has v2 checksums for, the shift comes from
    //    the database's "frame 450" checksums (one sector of each track,
    //    stored for exactly this), or failing that from the read offsets
    //    drives commonly have (beta 4: a store CD with only a v2 set).
    // A track runs from its INDEX 01 to the next track's INDEX 01 (the gap
    // belongs to the track before). Only the image is read: no drive.
    public static class AccurateRip
    {
        private const int Shift = 3000;          // samples tried each way
        private const int Edge = 5 * 588 - 1;    // 2939: samples left out of the first / last track (positions are 1-based)

        public sealed class TrackResult
        {
            public int Number;
            public uint V1, V2;
            public int Confidence;          // rips in the database with this checksum (0 = no match)
            public int BestConfidence;      // the most rips any checksum for this track has
            public bool Accurate => Confidence > 0;
        }

        public sealed class Result
        {
            public bool Ok;                 // the check ran (whatever the outcome)
            public bool InDatabase;
            public bool Cancelled;
            public string Message = "";
            public string Url = "";
            public int? Offset;             // samples the image is shifted by against the database
            public int Pressings;           // checksum sets the database has for this CD
            public List<TrackResult> Tracks = new();
            public int AccurateCount => Tracks.Count(t => t.Accurate);
        }

        // ---- drive read offsets (beta 4) ----

        private static string? offsetPage;   // the list, fetched once per run

        /// <summary>
        /// The read offset AccurateRip lists for a drive ("ASUS BW-16D1HT" ->
        /// +6), from accuraterip.com/driveoffsets.htm, or null if the drive
        /// isn't listed or the page can't be reached. The page lists drives as
        /// "VENDOR - MODEL" next to their offset; letters and digits are
        /// compared, so spaces and dashes don't matter.
        /// </summary>
        public static async Task<int?> DriveOffsetAsync(string vendorAndModel, CancellationToken ct = default)
        {
            string want = Squash(vendorAndModel);
            if (want.Length < 4) return null;
            try
            {
                if (offsetPage is null)
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchivSector-OD");
                    offsetPage = await http.GetStringAsync("http://www.accuraterip.com/driveoffsets.htm", ct);
                }
                // Row by row; in each, the cells as text.
                foreach (System.Text.RegularExpressions.Match row in System.Text.RegularExpressions.Regex.Matches(offsetPage, @"<tr[^>]*>(.*?)</tr>",
                    System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    var cells = System.Text.RegularExpressions.Regex.Matches(row.Groups[1].Value, @"<td[^>]*>(.*?)</td>",
                            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        .Select(c => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(c.Groups[1].Value, "<[^>]+>", "")).Trim())
                        .ToList();
                    for (int i = 0; i + 1 < cells.Count; i++)
                    {
                        if (Squash(cells[i]) != want) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(cells[i + 1], @"^([+-]?\d{1,4})$");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int off)) return off;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        // ---- the CD's IDs ----

        /// <summary>The database file for the CD, or null (with why) if the .cue has no music.</summary>
        public static string? DatabasePath(CueImage img, out string? problem) => DatabasePaths(img, out problem).FirstOrDefault();

        /// <summary>
        /// The database files the CD may be under. A CD with a data track
        /// (games, Enhanced CDs) is counted differently by different rippers
        /// (all tracks, or only the music ones), so each way is listed, the
        /// usual one first. A plain music CD has just one.
        /// </summary>
        public static List<string> DatabasePaths(CueImage img, out string? problem)
        {
            problem = null;
            var list = new List<string>();
            if (img.Tracks.Count == 0 || !img.HasAudio) { problem = "The image has no music tracks."; return list; }
            foreach (var (allTracks, ownNumbers) in new[] { (true, true), (false, true), (false, false) })
            {
                var p = PathFor(img, allTracks, ownNumbers);
                if (!list.Contains(p)) list.Add(p);
            }
            return list;
        }

        // allTracks: the data tracks count in the number of tracks;
        // ownNumbers: a music track is weighted by its own number on the CD,
        // not by its place among the music tracks.
        private static string PathFor(CueImage img, bool allTracks, bool ownNumbers)
        {
            var tracks = img.Tracks;
            long leadOut = img.LeadOutLba;
            // An Enhanced CD (music, then a data session): the music's end is
            // the data track's start less the gap between sessions (11,400).
            if (!tracks[^1].IsAudio && tracks[^1].Session > 1) leadOut = tracks[^1].Index1Lba - 11_400;
            uint id1 = 0, id2 = 0;
            int place = 0;
            foreach (var t in tracks.Where(t => t.IsAudio))
            {
                place++;
                uint at = (uint)Math.Max(0, t.Index1Lba);
                id1 += at;
                id2 += Math.Max(at, 1) * (uint)(ownNumbers ? t.Number : place);
            }
            int count = allTracks ? tracks.Count : tracks.Count(t => t.IsAudio);
            id1 += (uint)leadOut;
            id2 += (uint)Math.Max(leadOut, 1) * (uint)(count + 1);
            uint cddb = CddbId(tracks.Select(t => t.Index1Lba).ToList(), img.LeadOutLba);
            return $"{id1 & 0xF:x}/{(id1 >> 4) & 0xF:x}/{(id1 >> 8) & 0xF:x}/dBAR-{count:D3}-{id1:x8}-{id2:x8}-{cddb:x8}.bin";
        }

        // freedb's disc ID: the digit sums of each track's start in seconds,
        // the length in seconds, and the number of tracks.
        private static uint CddbId(List<long> starts, long leadOut)
        {
            static int Digits(long n) { int s = 0; while (n > 0) { s += (int)(n % 10); n /= 10; } return s; }
            long sum = starts.Sum(s => (long)Digits((s + 150) / 75));
            long length = (leadOut + 150) / 75 - (starts[0] + 150) / 75;
            return (uint)(((sum % 255) << 24) | (length << 8) | (long)starts.Count);
        }

        // ---- the check ----

        public static Task<Result> CheckAsync(string cuePath, IProgress<double>? progress, CancellationToken ct, Action<string>? log = null) =>
            Task.Run(async () =>
            {
                var r = new Result();
                try { await Check(cuePath, progress, ct, r, log); }
                catch (OperationCanceledException) { r.Cancelled = true; r.Message = "Stopped."; }
                catch (Exception ex) { r.Message = "The check stopped: " + ex.Message; }
                log?.Invoke("AccurateRip: " + r.Message);
                return r;
            });

        private static async Task Check(string cuePath, IProgress<double>? progress, CancellationToken ct, Result r, Action<string>? log)
        {
            var img = CueImage.Load(cuePath);
            if (img.Problem is not null) { r.Message = img.Problem; return; }
            var paths = DatabasePaths(img, out var problem);
            if (paths.Count == 0) { r.Message = problem!; return; }
            log?.Invoke($"AccurateRip: {Path.GetFileName(cuePath)}: {img.Summary}");

            // The database entry (each way the CD may be listed, until one is there).
            byte[]? data = null;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchivSector-OD");
                foreach (var path in paths)
                {
                    r.Url = "http://www.accuraterip.com/accuraterip/" + path;
                    log?.Invoke("AccurateRip: looking up " + r.Url);
                    HttpResponseMessage resp;
                    try { resp = await http.GetAsync(r.Url, ct); }
                    catch (HttpRequestException ex) { r.Message = "AccurateRip couldn't be reached (" + ex.Message + "). Is the PC online?"; return; }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested) { r.Message = "AccurateRip didn't answer in time. Try again later."; return; }
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
                    if (!resp.IsSuccessStatusCode) { r.Message = $"AccurateRip answered {(int)resp.StatusCode} {resp.ReasonPhrase}."; return; }
                    data = await resp.Content.ReadAsByteArrayAsync(ct);
                    break;
                }
            }
            if (data is null)
            {
                r.Ok = true;
                r.Message = "This CD isn't in the AccurateRip database: nobody has submitted a rip of it yet, so it can't be compared.";
                return;
            }
            var known = Parse(data, img.Tracks.Count, out int pressings);
            r.InDatabase = true;
            r.Pressings = pressings;
            log?.Invoke($"AccurateRip: in the database, {pressings} checksum set(s)");

            // Each music track: where it starts and ends (absolute samples) and which positions count.
            var audio = new List<(CueImage.Track T, long Start, long Count, int From, long To)>();
            for (int i = 0; i < img.Tracks.Count; i++)
            {
                var t = img.Tracks[i];
                if (!t.IsAudio) continue;
                var next = i + 1 < img.Tracks.Count ? img.Tracks[i + 1] : null;
                long end = next is not null && next.IsAudio && next.Session == t.Session ? next.Index1Lba : t.EndLba;
                long count = (end - t.Index1Lba) * 588;
                if (count <= 2 * (Edge + 1)) continue;   // too short to check
                bool first = t.Number == img.Tracks[0].Number, last = t.Number == img.Tracks[^1].Number;
                audio.Add((t, t.Index1Lba * 588, count, first ? Edge + 1 : 1, last ? count - (Edge + 1) : count));
            }
            if (audio.Count == 0) { r.Message = "The music tracks are too short to check."; return; }

            using var src = new SampleSource(img);
            long work = audio.Sum(a => a.Count) * 2, done = 0;
            void Done(long n) { done += n; progress?.Report(100.0 * done / work); }

            // Pass 1: every track's v1 at every shift.
            var v1At = new Dictionary<int, uint[]>();
            foreach (var a in audio)
            {
                ct.ThrowIfCancellationRequested();
                v1At[a.T.Number] = SlideV1(src, a.Start, a.Count, a.From, a.To, ct);
                Done(a.Count);
            }

            // The shift most tracks match at (the smallest one on a tie).
            int bestShift = 0, bestCount = -1;
            for (int k = -Shift; k <= Shift; k++)
            {
                int n = audio.Count(a => known.TryGetValue(a.T.Number, out var list) && list.Any(c => c.Crc == v1At[a.T.Number][k + Shift]));
                if (n > bestCount || (n == bestCount && Math.Abs(k) < Math.Abs(bestShift))) { bestCount = n; bestShift = k; }
            }
            string how = "the v1 checksums";

            // No v1 match at any shift: the frame-450 checksums (sector 450
            // of each track, 588 samples, weighted 1 to 588).
            if (bestCount <= 0)
            {
                var f450At = new Dictionary<int, uint[]>();
                foreach (var a in audio.Where(a => a.Count > 451 * 588 + Shift && known.ContainsKey(a.T.Number)))
                    f450At[a.T.Number] = SlideV1(src, a.Start + 450 * 588, 588, 1, 588, ct);
                bestShift = 0; bestCount = -1;
                for (int k = -Shift; k <= Shift; k++)
                {
                    int n = f450At.Count(f => known[f.Key].Any(c => c.Frame450 == f.Value[k + Shift]));
                    if (n > bestCount || (n == bestCount && Math.Abs(k) < Math.Abs(bestShift))) { bestCount = n; bestShift = k; }
                }
                how = "the frame-450 checksums";
                var sample = audio.FirstOrDefault(a => f450At.ContainsKey(a.T.Number));
                if (sample.T is not null && known.TryGetValue(sample.T.Number, out var dbl) && dbl.Count > 0)
                    log?.Invoke($"AccurateRip: track {sample.T.Number:D2} in the database: checksum {dbl[0].Crc:X8}, frame 450 {dbl[0].Frame450:X8}; this image's frame 450 at no shift: {f450At[sample.T.Number][Shift]:X8}");
            }

            // Still nothing: v2 worked out in full at the read offsets drives
            // commonly have, on the shortest track the database knows.
            if (bestCount <= 0)
            {
                var probe = audio.Where(a => known.TryGetValue(a.T.Number, out var l) && l.Count > 0).OrderBy(a => a.Count).FirstOrDefault();
                bestShift = 0; bestCount = -1;
                if (probe.T is not null)
                    foreach (int k in CommonOffsets)
                    {
                        ct.ThrowIfCancellationRequested();
                        var (pv1, pv2) = Sums(src, probe.Start + k, probe.Count, probe.From, probe.To);
                        if (known[probe.T.Number].Any(c => c.Crc == pv1 || c.Crc == pv2)) { bestShift = k; bestCount = 1; break; }
                    }
                how = "the usual drive read offsets";
            }

            int shift = bestCount > 0 ? bestShift : 0;
            r.Offset = bestCount > 0 ? shift : null;
            log?.Invoke(r.Offset is null ? "AccurateRip: no shift lines the image up with the database"
                : $"AccurateRip: the image lines up with the database at a shift of {shift:+0;-0;0} samples (found with {how})");

            // Pass 2: v1 and v2 at that shift.
            foreach (var a in audio)
            {
                ct.ThrowIfCancellationRequested();
                var (v1, v2) = Sums(src, a.Start + shift, a.Count, a.From, a.To);
                Done(a.Count);
                known.TryGetValue(a.T.Number, out var list);
                list ??= new();
                int conf = list.Where(c => c.Crc == v1 || c.Crc == v2).Sum(c => c.Confidence);
                r.Tracks.Add(new TrackResult { Number = a.T.Number, V1 = v1, V2 = v2, Confidence = conf, BestConfidence = list.Count == 0 ? 0 : list.Max(c => c.Confidence) });
            }

            r.Ok = true;
            int accurate = r.AccurateCount, total = r.Tracks.Count;
            string shiftText = r.Offset is null ? "" : r.Offset == 0 ? "" : $" (the image is {Math.Abs(r.Offset.Value)} samples {(r.Offset > 0 ? "late" : "early")} against the database, a drive's normal read offset)";
            r.Message = accurate == total ? $"All {total} music tracks are accurate: they match other people's rips of this CD{shiftText}."
                : accurate == 0 ? "None of the music tracks match the database. It may be a different pressing of the CD (they can differ), or the copy has errors."
                : $"{accurate} of {total} music tracks are accurate{shiftText}; the others don't match (a different pressing, or errors in those tracks).";
            foreach (var t in r.Tracks)
                log?.Invoke($"AccurateRip: track {t.Number:D2}: v1 {t.V1:X8}, v2 {t.V2:X8}: " + (t.Accurate ? $"accurate (confidence {t.Confidence})" : t.BestConfidence > 0 ? $"no match (the database's best has confidence {t.BestConfidence})" : "not in the database"));
        }

        // Read offsets drives often have (samples; image shift = the offset).
        private static readonly int[] CommonOffsets =
        {
            0, 6, 12, 30, 48, 91, 97, 102, 103, 116, 118, 120, 594, 667, 679, 685, 691, 696, 702, 704, 738, 1164, 1194, 1776,
            -6, -12, -30, -48, -91, -97, -102, -103, -116, -472, -582, -594, -667, -685, -696, -702, -704, -738, -1164, -1194,
        };

        // The database file: blocks of [track count, id1, id2, cddb], then
        // for each track [confidence, checksum, frame-450 checksum].
        private static Dictionary<int, List<(uint Crc, int Confidence, uint Frame450)>> Parse(byte[] d, int trackCount, out int blocks)
        {
            var known = new Dictionary<int, List<(uint, int, uint)>>();
            blocks = 0;
            int p = 0;
            while (p + 13 <= d.Length)
            {
                int n = d[p];
                p += 13;
                if (p + n * 9 > d.Length) break;
                blocks++;
                for (int t = 1; t <= n; t++)
                {
                    int conf = d[p];
                    uint crc = BitConverter.ToUInt32(d, p + 1);
                    uint f450 = BitConverter.ToUInt32(d, p + 5);
                    p += 9;
                    if (!known.TryGetValue(t, out var list)) known[t] = list = new();
                    if (conf > 0) list.Add((crc, conf, f450));
                }
            }
            return known;
        }

        // v1 at every shift k (-Shift..+Shift) of: sum over positions m in
        // [from, to] of m × x[start + m - 1 + k]. One pass for k = -Shift,
        // then each next shift from the last:
        //   S(k+1) = S(k) - (from-1)·x[from+k] + to·x[to+1+k] - T(k)
        //   T(k+1) = T(k) - x[from+k] + x[to+1+k]      (T = the plain sum)
        // with x[j] = the sample at start + j - 1. All in 32-bit wrap-around.
        private static uint[] SlideV1(SampleSource src, long start, long count, int from, long to, CancellationToken ct)
        {
            uint s = 0, t = 0;
            long first = start + from - 1 - Shift;
            src.Begin(first);
            for (long m = from; m <= to; m++)
            {
                if ((m & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
                uint x = src.Next();
                s += (uint)m * x;
                t += x;
            }
            // The samples the sliding needs: around "from" and just past "to".
            var head = src.Range(start + from - 1 - Shift, 2 * Shift + 1);
            var tail = src.Range(start + to - Shift, 2 * Shift + 1);
            var result = new uint[2 * Shift + 1];
            result[0] = s;
            for (int i = 0; i < 2 * Shift; i++)
            {
                uint xa = head[i], xb = tail[i];       // x[from + k], x[to + 1 + k] for k = i - Shift
                s = s - (uint)(from - 1) * xa + (uint)to * xb - t;
                t = t - xa + xb;
                result[i + 1] = s;
            }
            return result;
        }

        // v1 and v2 at one shift.
        private static (uint V1, uint V2) Sums(SampleSource src, long start, long count, int from, long to)
        {
            uint lo = 0, hi = 0;
            src.Begin(start + from - 1);
            for (long m = from; m <= to; m++)
            {
                ulong prod = (ulong)src.Next() * (ulong)(uint)m;
                lo += (uint)prod;
                hi += (uint)(prod >> 32);
            }
            return (lo, lo + hi);
        }

        // The image's music as 32-bit stereo samples by absolute position
        // (LBA × 588 + sample). Gaps, data and anything outside the image
        // count as silence.
        private sealed class SampleSource : IDisposable
        {
            private readonly CueImage img;
            private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
            private readonly byte[] sector = new byte[2352];
            private long sectorLba = long.MinValue;
            private long pos;

            public SampleSource(CueImage img) => this.img = img;

            public void Begin(long sample) => pos = sample;

            public uint Next() => At(pos++);

            public uint[] Range(long sample, int n)
            {
                var a = new uint[n];
                for (int i = 0; i < n; i++) a[i] = At(sample + i);
                return a;
            }

            private uint At(long sample)
            {
                long lba = (long)Math.Floor(sample / 588.0);
                if (lba != sectorLba) Load(lba);
                int o = (int)(sample - lba * 588) * 4;
                return BitConverter.ToUInt32(sector, o);
            }

            private void Load(long lba)
            {
                sectorLba = lba;
                Array.Clear(sector);
                var piece = img.Pieces.FirstOrDefault(x => lba >= x.StartLba && lba < x.StartLba + x.Count);
                if (piece is null || piece.File is null || piece.Mode != CueImage.TrackMode.Audio) return;
                if (!files.TryGetValue(piece.File, out var fs))
                {
                    fs = new FileStream(piece.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    files[piece.File] = fs;
                }
                long at = piece.FileOffset + (lba - piece.StartLba) * 2352;
                int real = (int)Math.Max(0, Math.Min(2352, piece.FileDataEnd - at));
                if (fs.Position != at) fs.Position = at;
                int got = 0;
                while (got < real) { int k = fs.Read(sector, got, real - got); if (k <= 0) break; got += k; }
                if (piece.SwapAudio) for (int b = 0; b < 2352; b += 2) (sector[b], sector[b + 1]) = (sector[b + 1], sector[b]);
            }

            public void Dispose() { foreach (var f in files.Values) f.Dispose(); }
        }
    }
}
