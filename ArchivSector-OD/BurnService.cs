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
