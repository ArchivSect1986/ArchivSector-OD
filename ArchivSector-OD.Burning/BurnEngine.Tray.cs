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
    }
}
