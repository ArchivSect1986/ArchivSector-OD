using System.Diagnostics;

namespace ArchivSector_OD.Burning
{
    // Tray watching (v4.0 week 6): is a drive's tray open or shut? Asked
    // with MECHANISM STATUS, which only reads the drive's state: it doesn't
    // spin the disc up and doesn't use up the "disc changed" news Windows
    // itself listens for. So the Burn tab can notice a tray opened or shut
    // with the drive's own button, and re-read the drive by itself.
    public static partial class BurnEngine
    {
        public enum TrayState { Unknown, Open, Closed }

        /// <summary>
        /// Open or closed, or Unknown when the drive is busy here, can't be
        /// opened, or doesn't say (then nothing changes on screen).
        /// </summary>
        public static TrayState QueryTray(string driveLetter)
        {
            var letter = Norm(driveLetter);
            if (IsBusy(letter)) return TrayState.Unknown;
            try
            {
                using var drive = ScsiDrive.Open(letter, out _);
                if (drive is null) return TrayState.Unknown;
                var status = new byte[8];
                var rep = drive.Read(new byte[] { 0xBD, 0, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0 }, status, 5);
                if (!rep.Ok) return TrayState.Unknown;
                return (status[1] & 0x10) != 0 ? TrayState.Open : TrayState.Closed;   // byte 1, bit 4: door open
            }
            catch { return TrayState.Unknown; }
        }

        // Before verifying (beta 4), the way ImgBurn does: the tray is opened
        // and closed again, so the drive forgets everything about the disc
        // and reads it fresh, like another drive or player would, instead of
        // checking what it remembers from writing it. A drive whose tray
        // won't close by itself (a laptop or slim drive) waits up to 10
        // minutes for it to be pushed shut. A tray that won't open just
        // means the verify goes ahead without it. False only if the disc
        // didn't come back (or the burn was stopped); then there's no verify.
        private static bool CycleTray(ScsiDrive drive, IProgress<Progress>? progress, CancellationToken ct, Action<string>? log, string who)
        {
            progress?.Report(new Progress { Phase = "Cycling the tray", Percent = -1 });
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 0, 0 }, 10);                       // ALLOW MEDIUM REMOVAL
            var open = drive.Command(new byte[] { 0x1B, 0, 0, 0, 0x02, 0 }, 30);        // START STOP UNIT: eject
            if (!open.Ok)
            {
                log?.Invoke($"{who}: the tray couldn't be opened before verifying ({open.Describe()}), so the disc is verified without it");
                drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);
                return true;
            }
            Thread.Sleep(1500);
            var close = drive.Command(new byte[] { 0x1B, 0, 0, 0, 0x03, 0 }, 30);       // START STOP UNIT: load
            if (!close.Ok)
            {
                log?.Invoke($"{who}: the tray didn't close by itself; waiting for it to be closed");
                progress?.Report(new Progress { Phase = "Close the tray to verify", Percent = -1 });
            }
            // Ready again: "no disc" while the tray is open or still closing.
            var clock = Stopwatch.StartNew();
            bool ready = false;
            while (clock.Elapsed < TimeSpan.FromMinutes(10) && !ct.IsCancellationRequested)
            {
                var rep = drive.Command(new byte[] { 0x00, 0, 0, 0, 0, 0 }, 10);
                if (rep.Ok) { ready = true; break; }
                Thread.Sleep(rep.SenseKey == 6 ? 100 : 500);
            }
            if (!ready)
            {
                log?.Invoke(ct.IsCancellationRequested ? $"{who}: stopped while the tray was cycling" : $"{who}: the disc wasn't back in the drive within 10 minutes, so it wasn't verified");
                return false;
            }
            drive.Command(new byte[] { 0x1E, 0, 0, 0, 1, 0 }, 10);                       // keep it shut for the verify
            FullDriveSpeed(drive);                                                        // read back at full speed
            log?.Invoke($"{who}: tray cycled, the disc is read back fresh ({clock.Elapsed.TotalSeconds:0} s)");
            return true;
        }
    }
}
