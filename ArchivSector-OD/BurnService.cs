using System.IO;
using ArchivSector_OD.Burning;

namespace ArchivSector_OD
{
    // The app's side of burning (v4.0 week 1). The burning itself now lives
    // in the ArchivSector-OD.Burning library (BurnEngine), shared by the
    // drive bays, the Burn tab and any future standalone burner. This
    // wrapper adds what belongs to the app:
    //   * the "drive busy" list (RipActivity), so rips, health checks and
    //     the bays' polling leave a burning drive alone;
    //   * ImgBurn's location from Settings;
    //   * the app's log and History;
    //   * moving the ISO to the Recycle Bin after a verified burn.
    // Everything that used BurnService before (bays, tray control, polling)
    // calls the same methods as before.
    public static class BurnService
    {
        public static bool IsBusy(string driveLetter) => BurnEngine.IsBusy(driveLetter);
        public static void PausePolling(string driveLetter, TimeSpan howLong) => BurnEngine.PausePolling(driveLetter, howLong);
        public static bool IsPaused(string driveLetter) => BurnEngine.IsPaused(driveLetter);
        public static BurnEngine.BlankInfo? QueryBlank(string driveLetter) => BurnEngine.QueryBlank(driveLetter);
        public static bool Eject(string driveLetter) => BurnEngine.Eject(driveLetter);

        // Is ImgBurn installed (or set in Settings)? Returns its path, or null.
        public static string? ImgBurnPath()
        {
            try { return ImgBurnService.FindExecutable(ConfigService.Load().ImgBurnPathOverride); }
            catch { return null; }
        }

        public static async Task<BurnEngine.Result> BurnAsync(string driveLetter, string isoPath, bool verify, string discTitle, string driveModel,
            IProgress<BurnEngine.Progress>? progress, CancellationToken ct, bool useImgBurn = false, bool deleteIsoAfter = false)
        {
            var letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
            string? imgBurn = null;
            if (useImgBurn)
            {
                imgBurn = ImgBurnPath();
                if (imgBurn is null)
                    return new BurnEngine.Result { Message = "ImgBurn isn't installed (or wasn't found). Install it, set its location in Settings, or burn with Windows instead." };
            }

            RipActivity.Set(letter, "burn", true);
            BurnEngine.Result r;
            try
            {
                r = await BurnEngine.BurnAsync(letter, isoPath, verify, progress, ct, imgBurn,
                    line => LogService.Write($"{letter}:", line));
            }
            finally
            {
                RipActivity.Set(letter, "burn", false);
            }

            // A refused start (already burning) isn't a burn: nothing to record.
            if (r.Message == "This drive is already burning.") return r;

            RecordHistory(r, letter, isoPath, discTitle, driveModel);

            // Only a burn that was checked and matched every byte lets the
            // ISO go, and only to the Recycle Bin.
            if (deleteIsoAfter && r.Burned && r.Verified == true && File.Exists(isoPath))
            {
                try
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(isoPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    r.IsoDeleted = true;
                    r.Message += " The ISO was moved to the Recycle Bin.";
                }
                catch (Exception ex) { r.Message += $" (The ISO couldn't be moved to the Recycle Bin: {ex.Message})"; }
            }
            return r;
        }

        // v4.0 week 2: the disc health check on a disc that was just burned.
        // Waits (up to 2 minutes) for Windows to re-read the fresh disc,
        // marks the drive busy while it reads, saves the report like the
        // bay's own health check, and logs the verdict. Returns null if it
        // couldn't run or was stopped before it started.
        public static async Task<DiscHealthService.Result?> HealthCheckAfterBurnAsync(string driveLetter, string discTitle, string driveModel,
            IProgress<BurnEngine.Progress>? progress, CancellationToken ct)
        {
            var letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
            progress?.Report(new BurnEngine.Progress { Phase = "Waiting for the drive", Percent = -1 });
            var start = DateTime.Now;
            while (!ct.IsCancellationRequested && DateTime.Now - start < TimeSpan.FromMinutes(2))
            {
                bool ready = false;
                try { ready = new DriveInfo(letter + ":\\").IsReady; } catch { }
                if (ready) break;
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { }
            }
            if (ct.IsCancellationRequested) return null;

            RipActivity.Set(letter, "health", true);
            try
            {
                var reporter = new Progress<DiscHealthService.Progress>(p =>
                {
                    double pct = p.TotalSectors > 0 ? Math.Min(100.0, 100.0 * p.SectorsDone / p.TotalSectors) : -1;
                    var left = p.CurrentBytesPerSec > 0 && p.TotalSectors > p.SectorsDone
                        ? TimeSpan.FromSeconds((p.TotalSectors - p.SectorsDone) * 2048.0 / p.CurrentBytesPerSec)
                        : TimeSpan.Zero;
                    progress?.Report(new BurnEngine.Progress { Phase = "Checking disc health", Percent = pct, BytesPerSec = p.CurrentBytesPerSec, Remaining = left });
                });
                var r = await DiscHealthService.RunAsync(letter, reporter, ct);
                if (r.Final.SectorsDone > 0)
                    await Task.Run(() => DiscHealthService.SaveReport(r, letter, discTitle, driveModel));
                LogService.Write($"{letter}:", $"Health check after burning ({discTitle}): {r.VerdictTitle} — {r.BadSectors:N0} unreadable, {r.SlowSectors:N0} slow" +
                                 (r.ReportPath is not null ? " — report: " + r.ReportPath : ""));
                return r;
            }
            catch (Exception ex)
            {
                LogService.Write($"{letter}:", $"Health check after burning ({discTitle}) couldn't run: {ex.Message}");
                return null;
            }
            finally
            {
                RipActivity.Set(letter, "health", false);
            }
        }

        // One line for a burn result: "Health check: Healthy (0 unreadable, 12 slow)."
        public static string HealthLine(DiscHealthService.Result r) =>
            $"Health check: {r.VerdictTitle}{(r.Completed ? "" : " (partial)")} ({r.BadSectors:N0} unreadable, {r.SlowSectors:N0} slow).";

        private static void RecordHistory(BurnEngine.Result r, string letter, string isoPath, string discTitle, string driveModel)
        {
            try
            {
                HistoryService.Append(new HistoryEntry
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    DriveLetter = letter,
                    DriveModel = driveModel,
                    Title = string.IsNullOrWhiteSpace(discTitle) ? Path.GetFileNameWithoutExtension(isoPath) : discTitle,
                    System = "Disc burn",
                    OutputPath = isoPath,
                    Capacity = File.Exists(isoPath) ? DiscHealthService.FormatBytes(new FileInfo(isoPath).Length) : "",
                    RedumpMatch = !r.Burned ? (r.Cancelled ? "Cancelled" : "Failed")
                        : r.Verified == true ? "Burned + verified"
                        : r.Verified == false ? "Burned, VERIFY FAILED"
                        : "Burned (not verified)",
                });
            }
            catch { }
        }
    }
}
