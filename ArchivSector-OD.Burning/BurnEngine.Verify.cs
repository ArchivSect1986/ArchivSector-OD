using System.Diagnostics;

namespace ArchivSector_OD.Burning
{
    // Verify mode (v4.0 week 5): compares the disc in a drive with an ISO,
    // sector by sector, like ImgBurn's "Verify" mode. For discs burned
    // earlier, by any program, or re-checked after time on the shelf.
    // Reads straight from the drive (ScsiDrive), so nothing Windows has
    // cached is compared. Read-only: nothing is written.
    // BIN/CUE (v4.0 beta 4): a CD is compared with its .cue the same way the
    // own writer checks a BIN/CUE after burning (VerifyCue): data tracks
    // exactly, music tracks after lining up the drive's read offset.
    public static partial class BurnEngine
    {
        public sealed class VerifyProgress
        {
            public double Percent;
            public double BytesPerSec;
            public double OneX;
            public TimeSpan Remaining;
            public long Different;          // sectors that read but don't match
            public long Unreadable;         // sectors the drive couldn't read
        }

        public sealed class VerifyResult
        {
            public bool Ok;                 // every sector matched
            public bool Cancelled;
            public string Message = "";
            public long Sectors;
            public long Different;
            public long Unreadable;
            public List<(long Lba, long Count)> BadRanges = new();
            public TimeSpan Time;
            public double AverageBytesPerSec;
        }

        public static Task<VerifyResult> VerifyAgainstIsoAsync(string driveLetter, string isoPath, IProgress<VerifyProgress>? progress,
            CancellationToken ct, Action<string>? log = null)
        {
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new VerifyResult { Message = "This drive is busy burning or checking." });
            return Task.Factory.StartNew(() =>
            {
                var r = new VerifyResult();
                try { VerifyAgainstIso(letter, isoPath, progress, ct, r, log); }
                catch (Exception ex) { r.Message = "The check stopped: " + ex.Message; log?.Invoke("Verify: " + r.Message); }
                finally { Busy.TryRemove(letter, out _); }
                return r;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void VerifyAgainstIso(string letter, string isoPath, IProgress<VerifyProgress>? progress, CancellationToken ct, VerifyResult r, Action<string>? log)
        {
            var iso = new FileInfo(isoPath);
            if (!iso.Exists) { r.Message = "The ISO file isn't there any more."; return; }
            if (iso.Length == 0 || iso.Length % Sector != 0) { r.Message = "This file isn't a disc image made of whole 2 KB sectors."; return; }
            long isoSectors = iso.Length / Sector;

            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }
            if (!WaitReady(drive, ct, TimeSpan.FromSeconds(30))) { r.Message = "There's no readable disc in the drive."; return; }
            FullDriveSpeed(drive);   // read at full speed, even if a slower one was set when burning

            // How much is on the disc?
            var cap = new byte[8];
            var rep = drive.Read(new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, cap, 10);
            long discSectors = rep.Ok ? BE32(cap, 0) + 1 : 0;
            if (rep.Ok && discSectors < isoSectors)
            {
                r.Message = $"The disc holds {discSectors:N0} sectors but the ISO has {isoSectors:N0}, so they can't be the same. (Is it the right disc and ISO?)";
                return;
            }
            double oneX = iso.Length <= 1_000_000_000L ? 153_600 : iso.Length <= 9_000_000_000L ? 1_385_000 : 4_500_000;
            log?.Invoke($"Verify: {letter}: against {isoPath} ({isoSectors:N0} sectors; the disc holds {discSectors:N0})");

            // Beta 5: a DVD+R DL burned with its capacity raised (an Xbox 360
            // XGD3 game) is read from the wrong place by some drives once the
            // blank's original layer 0 is passed. Said up front, and used to
            // explain the result.
            bool overburned = IsOverburnedDvd(drive, out long recordedL0, out long blankL0);
            if (overburned)
                log?.Invoke($"Verify: this DVD+R DL was burned with its capacity raised (layer 0 holds {recordedL0:N0} sectors; the blank was made for {blankL0:N0}). Some drives read discs like this from the wrong place after sector {blankL0:N0}: if the differences start there, verify it in the drive that burned it.");

            var disc = new byte[DirectChunkSectors * Sector];
            var file = new byte[DirectChunkSectors * Sector];
            long lba = 0;
            var check = new IsoCheck();

            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var window = new Queue<(TimeSpan T, long Lba)>();
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            while (lba < isoSectors)
            {
                if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
                int n = (int)Math.Min(DirectChunkSectors, isoSectors - lba);
                ReadFully(fs, file, n * Sector);
                bool goOn = CheckChunk(drive, lba, lba, n, file, disc, check, ct);
                lba += n;

                var now = clock.Elapsed;
                if (now - lastReport > TimeSpan.FromMilliseconds(250) || lba >= isoSectors || !goOn)
                {
                    lastReport = now;
                    window.Enqueue((now, lba));
                    while (window.Count > 1 && now - window.Peek().T > TimeSpan.FromSeconds(5)) window.Dequeue();
                    var first = window.Peek();
                    double secs = (now - first.T).TotalSeconds;
                    double bps = secs > 0.2 ? (lba - first.Lba) * (double)Sector / secs : 0;
                    progress?.Report(new VerifyProgress
                    {
                        Percent = 100.0 * lba / isoSectors,
                        BytesPerSec = bps,
                        OneX = oneX,
                        Remaining = bps > 0 ? TimeSpan.FromSeconds((isoSectors - lba) * (double)Sector / bps) : TimeSpan.Zero,
                        Different = check.Different,
                        Unreadable = check.Unreadable,
                    });
                }
                if (!goOn) break;
            }
            check.Finish();
            r.Different = check.Different;
            r.Unreadable = check.Unreadable;
            r.BadRanges = check.Ranges;
            r.Sectors = Math.Min(lba, isoSectors);
            r.Time = clock.Elapsed;
            r.AverageBytesPerSec = r.Time.TotalSeconds > 0 ? r.Sectors * (double)Sector / r.Time.TotalSeconds : 0;
            long bad = check.Bad;
            r.Ok = !r.Cancelled && check.StoppedEarly is null && bad == 0;
            r.Message = r.Cancelled
                ? $"Stopped at {100.0 * r.Sectors / isoSectors:0.0}%." + (bad > 0 ? $" {bad:N0} sector(s) didn't match so far ({check.Different:N0} differ, {check.Unreadable:N0} can't be read)." : " Everything checked so far matched.")
                : check.StoppedEarly is not null ? $"Stopped early at {100.0 * r.Sectors / isoSectors:0.0}%: {check.StoppedEarly}."
                : bad == 0 ? "The disc matches the ISO: every sector is identical."
                : $"The disc doesn't match the ISO: {check.Different:N0} sector(s) differ and {check.Unreadable:N0} couldn't be read.";
            if (overburned && bad > 0)
            {
                if (Math.Abs(check.FirstBad - blankL0) <= 64)
                {
                    // Not a bad disc: this drive misreads raised-capacity discs. Say just that.
                    string upTo = r.Cancelled ? $"Stopped at {100.0 * r.Sectors / isoSectors:0.0}%."
                        : check.StoppedEarly is not null ? $"Stopped early at {100.0 * r.Sectors / isoSectors:0.0}%."
                        : "Checked to the end.";
                    r.Message = $"{upTo} Every sector matched up to {blankL0:N0}, where the blank's original layer 0 ended; from there on this drive reads the disc from the wrong place ({bad:N0} sector(s) didn't match). It does that with discs burned with their capacity raised, so the disc is probably fine: verify it in the drive that burned it.";
                }
                else r.Message += " This disc was burned with its capacity raised: if another drive burned it, verify it there too before giving up on it.";
            }
            else if (bad > 0 && BdLayerChangeHint(check.FirstBad) is { } layerHint)
                r.Message += layerHint;
            log?.Invoke($"Verify: {r.Message} ({r.Time:hh\\:mm\\:ss})");
            if (r.BadRanges.Count > 0)
                log?.Invoke(overburned && Math.Abs(check.FirstBad - blankL0) <= 64
                    ? "Verify: sectors this drive read from the wrong place: " + check.RangesText()
                    : "Verify: problem sectors at " + check.RangesText());
        }

        // ---- Comparing a disc with an ISO (beta 5) ----
        // One check for Verify disc and the verify after burning, so both
        // count the same way: sectors that read but differ, and sectors that
        // can't be read, each listed exactly (a chunk that won't read is
        // re-read one sector at a time). It gives up early on a hopeless
        // disc instead of grinding on for an hour: after 10,000 bad sectors,
        // or 10 minutes spent re-reading sectors that won't read.

        private const long VerifyGiveUpBad = 10_000;

        // Beta 5: a BD-R DL's second layer starts at sector 12,219,392. Some
        // drives read its first few megabytes poorly (the burning drive
        // straight after burning especially), while another drive reads the
        // same disc perfectly: seen in testing, and by ImgBurn users with
        // Verbatim BD-R DL. Problems that start there get this hint.
        private const long BdLayer1Start = 12_219_392;
        internal static string? BdLayerChangeHint(long firstBad) =>
            firstBad >= BdLayer1Start && firstBad < BdLayer1Start + 65_536
                ? $" The problems start just after the layer change (sector {BdLayer1Start:N0}): some drives read the start of a BD-R DL's second layer poorly. Check the disc in another drive with Verify disc before giving up on it."
                : null;
        // Beta 5: 10 minutes (was 3). Each unreadable sector can take the
        // drive several seconds to give up on, and 3 minutes stopped a
        // 42 GB check after only 23 bad sectors, before it could say
        // whether the rest of the disc was fine.
        private static readonly TimeSpan VerifyGiveUpRereads = TimeSpan.FromMinutes(10);

        private sealed class IsoCheck
        {
            public long Different, Unreadable;
            public long FirstBad = -1;
            public List<(long Lba, long Count)> Ranges = new();
            public string? StoppedEarly;
            public TimeSpan RereadTime;
            private long runStart = -1, runCount;
            public long Bad => Different + Unreadable;

            public void Mark(long lba, bool unreadable)
            {
                if (unreadable) Unreadable++; else Different++;
                if (FirstBad < 0) FirstBad = lba;
                if (runStart >= 0 && runStart + runCount == lba) runCount++;
                else
                {
                    if (runStart >= 0) Ranges.Add((runStart, runCount));
                    runStart = lba; runCount = 1;
                }
            }

            public void Finish()
            {
                if (runStart >= 0) Ranges.Add((runStart, runCount));
                runStart = -1;
            }

            public string RangesText(int max = 20) =>
                string.Join(", ", Ranges.Take(max).Select(b => b.Count == 1 ? $"{b.Lba:N0}" : $"{b.Lba:N0}-{b.Lba + b.Count - 1:N0}"))
                + (Ranges.Count > max ? $" and {Ranges.Count - max} more ranges" : "");
        }

        // Reads n sectors from the disc at discLba and compares them with
        // file (the ISO's sectors from isoLba). False: stop, the disc is
        // hopeless (check.StoppedEarly says why).
        private static bool CheckChunk(ScsiDrive drive, long discLba, long isoLba, int n, byte[] file, byte[] disc, IsoCheck check, CancellationToken ct)
        {
            var target = n * Sector == disc.Length ? disc : new byte[n * Sector];
            var cdb = new byte[] { 0x28, 0, (byte)(discLba >> 24), (byte)(discLba >> 16), (byte)(discLba >> 8), (byte)discLba, 0, (byte)(n >> 8), (byte)n, 0 };
            var rep = RetryWhileBusy(() => drive.Read(cdb, target, 60), 100, 50);
            if (rep.Ok)
            {
                for (int s = 0; s < n; s++)
                    if (!target.AsSpan(s * Sector, Sector).SequenceEqual(file.AsSpan(s * Sector, Sector))) check.Mark(isoLba + s, false);
            }
            else
            {
                // Sector by sector, to find exactly which ones won't read.
                var sw = Stopwatch.StartNew();
                var one = new byte[Sector];
                for (int s = 0; s < n; s++)
                {
                    if (ct.IsCancellationRequested) break;
                    long at = discLba + s;
                    var c1 = new byte[] { 0x28, 0, (byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at, 0, 0, 1, 0 };
                    var r1 = RetryWhileBusy(() => drive.Read(c1, one, 30), 20, 50);
                    if (!r1.Ok) check.Mark(isoLba + s, true);
                    else if (!one.AsSpan().SequenceEqual(file.AsSpan(s * Sector, Sector))) check.Mark(isoLba + s, false);
                }
                check.RereadTime += sw.Elapsed;
            }
            if (check.Bad >= VerifyGiveUpBad)
            {
                check.StoppedEarly = $"more than {VerifyGiveUpBad:N0} sectors don't match, from sector {check.FirstBad:N0} on. It's the wrong disc or ISO, a badly written disc, or a drive that reads this disc differently";
                return false;
            }
            if (check.RereadTime > VerifyGiveUpRereads)
            {
                check.StoppedEarly = $"{check.Unreadable:N0} sector(s) so far can't be read (the first problem at sector {check.FirstBad:N0}), and checking them one by one was taking too long. The disc has bad spots: don't rely on it";
                return false;
            }
            return true;
        }

        // ---- BIN/CUE (beta 4) ----

        public static Task<VerifyResult> VerifyAgainstCueAsync(string driveLetter, string cuePath, IProgress<VerifyProgress>? progress,
            CancellationToken ct, Action<string>? log = null)
        {
            var letter = Norm(driveLetter);
            if (!Busy.TryAdd(letter, true))
                return Task.FromResult(new VerifyResult { Message = "This drive is busy burning or checking." });
            return Task.Factory.StartNew(() =>
            {
                var r = new VerifyResult();
                try { VerifyAgainstCue(letter, cuePath, progress, ct, r, log); }
                catch (Exception ex) { r.Message = "The check stopped: " + ex.Message; log?.Invoke("Verify: " + r.Message); }
                finally { Busy.TryRemove(letter, out _); }
                return r;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static void VerifyAgainstCue(string letter, string cuePath, IProgress<VerifyProgress>? progress, CancellationToken ct, VerifyResult r, Action<string>? log)
        {
            if (!File.Exists(cuePath)) { r.Message = "The .cue file isn't there any more."; return; }
            var img = CueImage.Load(cuePath);
            if (img.Problem is not null) { r.Message = img.Problem; return; }

            using var drive = ScsiDrive.Open(letter, out var problem);
            if (drive is null) { r.Message = problem ?? "The drive couldn't be opened."; return; }
            if (!WaitReady(drive, ct, TimeSpan.FromSeconds(30))) { r.Message = "There's no readable disc in the drive."; return; }

            // The disc's tracks have to be where the .cue puts them, or every
            // sector would be compared with the wrong part of the .bin.
            var toc = CdImageReader.ReadToc(drive, out problem);
            if (toc is null) { r.Message = (problem ?? "The disc's track list couldn't be read.") + " A BIN/CUE can only be checked against a CD."; return; }
            string? layout = null;
            if (toc.Tracks.Count != img.Tracks.Count)
                layout = $"The CD has {toc.Tracks.Count} track(s) but the .cue has {img.Tracks.Count}, so they can't be the same.";
            else
                for (int i = 0; i < img.Tracks.Count && layout is null; i++)
                {
                    var want = img.Tracks[i];
                    var got = toc.Tracks[i];
                    if (got.IsData == want.IsAudio)
                        layout = $"Track {want.Number} is {(want.IsAudio ? "music" : "data")} in the .cue but {(got.IsData ? "data" : "music")} on the CD.";
                    else if (got.StartLba != want.Index1Lba)
                        layout = $"Track {want.Number} starts at sector {got.StartLba:N0} on the CD but at {want.Index1Lba:N0} in the .cue.";
                }
            if (layout is null && toc.LeadOutLba != img.LeadOutLba)
                layout = $"The CD ends at sector {toc.LeadOutLba:N0} but the .cue at {img.LeadOutLba:N0}.";
            if (layout is not null)
            {
                r.Message = layout + " (Is it the right disc and .cue?)";
                log?.Invoke("Verify: " + r.Message);
                return;
            }
            FullDriveSpeed(drive);   // read at full speed, even if a slower one was set when burning

            long sectors = img.Pieces.Where(x => x.File is not null).Sum(x => x.Count);
            log?.Invoke($"Verify: {letter}: against {cuePath} ({img.Summary}; {sectors:N0} sectors to compare)");
            var clock = Stopwatch.StartNew();
            var inner = new SyncProgress<Progress>(p =>
            {
                if (p.Phase != "Verifying") return;
                progress?.Report(new VerifyProgress
                {
                    Percent = Math.Max(0, p.Percent),
                    BytesPerSec = p.BytesPerSec,
                    OneX = 176_400,
                    Remaining = p.Remaining,
                });
            });
            var br = new Result();
            string audioNote = VerifyCue(drive, img, inner, ct, br, log);

            r.Time = clock.Elapsed;
            r.Cancelled = br.Cancelled || ct.IsCancellationRequested;
            r.Sectors = r.Cancelled ? 0 : sectors;
            r.Different = br.MismatchedSectors;
            r.BadRanges = ToRanges(br.BadSectorLbas);
            r.AverageBytesPerSec = r.Time.TotalSeconds > 0 && r.Sectors > 0 ? r.Sectors * 2352.0 / r.Time.TotalSeconds : 0;
            r.Ok = !r.Cancelled && br.Verified == true;
            r.Message = r.Cancelled ? "Stopped before the end, so the disc wasn't fully checked."
                : r.Ok ? $"The disc matches the BIN/CUE{audioNote}."
                : $"The disc doesn't match the BIN/CUE: {br.MismatchedSectors:N0} sector(s) differ or couldn't be read.";
            log?.Invoke("Verify: " + r.Message);
        }

        // Progress<T> posts to the thread that made it; this one just calls
        // straight through (the panel's own Progress does the hand-over).
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> report;
            public SyncProgress(Action<T> report) => this.report = report;
            public void Report(T value) => report(value);
        }
    }
}
