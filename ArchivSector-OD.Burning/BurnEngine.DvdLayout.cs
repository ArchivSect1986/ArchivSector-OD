namespace ArchivSector_OD.Burning
{
    // A DVD's layout as the disc itself describes it (v4.0 beta 5): its
    // "book type" (what it tells players it is: a burned DVD+R can say
    // DVD-ROM), how many layers, and where layer 0 ends. Read with READ DISC
    // STRUCTURE (a standard command every DVD drive answers):
    //   format 00h: the physical format information as recorded on the disc
    //               (for a blank, the drive's own idea of it);
    //   format 11h: the DVD+R/+RW's ADIP, what the blank was made as. The
    //               same layout at the start, so the same reader works.
    // Used by Drive info (book type, layer 0), the burn log (book type after
    // a DVD+R burn) and Verify disc (a disc burned with its capacity raised,
    // like an Xbox 360 XGD3 game, which some drives read from the wrong place).
    public static partial class BurnEngine
    {
        internal sealed class DvdLayout
        {
            public int BookType;            // 0 DVD-ROM, 0xA DVD+R, 0xE DVD+R DL...
            public int Layers;              // 1 or 2
            public bool OppositeTrackPath;  // dual layer: layer 1 runs back from the outside
            public long StartPsn, EndPsn, L0EndPsn;

            /// <summary>Sectors in layer 0 (dual-layer discs), else 0.</summary>
            public long L0Sectors => Layers > 1 && L0EndPsn >= StartPsn ? L0EndPsn - StartPsn + 1 : 0;
            public string BookName => BookTypeName(BookType);
        }

        /// <summary>"DVD-ROM", "DVD+R DL"... for a DVD book type number.</summary>
        public static string BookTypeName(int bookType) => bookType switch
        {
            0x0 => "DVD-ROM",
            0x1 => "DVD-RAM",
            0x2 => "DVD-R",
            0x3 => "DVD-RW",
            0x9 => "DVD+RW",
            0xA => "DVD+R",
            0xD => "DVD+RW DL",
            0xE => "DVD+R DL",
            _ => $"type {bookType}",
        };

        // Format 00h (recorded) or 11h (ADIP, DVD+R/+RW only). Null if the drive won't say.
        internal static DvdLayout? ReadDvdLayout(ScsiDrive drive, byte format)
        {
            try
            {
                var ds = new byte[4 + 256];
                var rep = drive.Read(new byte[] { 0xAD, 0x00, 0, 0, 0, 0, 0, format, (byte)(ds.Length >> 8), (byte)ds.Length, 0, 0 }, ds, 10);
                if (!rep.Ok) return null;
                int len = (ds[0] << 8) | ds[1];
                if (len < 2 + 16) return null;
                var d = new DvdLayout
                {
                    BookType = ds[4] >> 4,
                    Layers = ((ds[6] >> 5) & 3) + 1,
                    OppositeTrackPath = (ds[6] & 0x10) != 0,
                    StartPsn = BE32(ds, 8) & 0xFFFFFF,
                    EndPsn = BE32(ds, 12) & 0xFFFFFF,
                    L0EndPsn = BE32(ds, 16) & 0xFFFFFF,
                };
                return d.StartPsn > 0 ? d : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// For a DVD+R DL burned with its capacity raised (BurnerMAX, Burner
        /// Max firmware: Xbox 360 XGD3 games): layer 0 as recorded and as the
        /// blank was made, in sectors. False for any other disc.
        /// </summary>
        internal static bool IsOverburnedDvd(ScsiDrive drive, out long recordedL0, out long blankL0)
        {
            recordedL0 = blankL0 = 0;
            var rec = ReadDvdLayout(drive, 0x00);
            var adip = ReadDvdLayout(drive, 0x11);
            if (rec is null || adip is null || rec.Layers < 2 || adip.Layers < 2) return false;
            recordedL0 = rec.L0Sectors;
            blankL0 = adip.L0Sectors;
            return blankL0 > 0 && recordedL0 > blankL0;
        }
    }
}
