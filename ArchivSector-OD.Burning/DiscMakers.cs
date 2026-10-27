namespace ArchivSector_OD.Burning
{
    // Who made a blank disc (v4.0 week 6), from the maker code the drive
    // reads off it, the way ImgBurn shows it. The brand on the box is often
    // not the factory: a "Verbatim" DVD+R may come from Mitsubishi
    // (MKM/MCC) or from CMC. The lists hold the common factories only;
    // anything else just shows its code.
    public static class DiscMakers
    {
        // DVD and BD maker codes: the start of the code is enough
        // ("MKM-A02-000" → Mitsubishi). Longest prefixes are tried first.
        private static readonly (string Prefix, string Name)[] DvdBd =
        {
            ("MKM", "Mitsubishi Kagaku Media (Verbatim)"),
            ("MCC", "Mitsubishi Kagaku Media (Verbatim)"),
            ("VERBAT", "Verbatim"),
            ("CMC", "CMC Magnetics"),
            ("RITEK", "Ritek"),
            ("RICOHJPN", "Ricoh"),
            ("TYG", "Taiyo Yuden"),
            ("TTG", "Taiyo Yuden"),
            ("TAIYOYUDEN", "Taiyo Yuden"),
            ("JVC", "JVC"),
            ("SONY", "Sony"),
            ("PHILIPS", "Philips"),
            ("MEI", "Panasonic"),
            ("PAN", "Panasonic"),
            ("TDK", "TDK"),
            ("MAXELL", "Maxell"),
            ("MXL", "Maxell"),
            ("FUJIFILM", "Fujifilm"),
            ("OPTODISC", "Optodisc"),
            ("PRODISC", "Prodisc"),
            ("INFODISC", "Infodisc"),
            ("INFOME", "InfoMedia"),
            ("DAXON", "Daxon"),
            ("UME", "UmeDisc"),
            ("SKYMEDIA", "Sky Media"),
            ("MBI", "Moser Baer India"),
            ("GSC", "Gigastorage"),
            ("BEALL", "BeAll Developers"),
            ("MILLEN", "Millenniata (M-DISC)"),
            ("LEADDATA", "Lead Data"),
            ("ISMMEDIA", "ISM"),
        };

        /// <summary>The factory behind a DVD/BD maker code, or null if it's not in the list.</summary>
        public static string? ForDvdBd(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var c = new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            foreach (var (prefix, name) in DvdBd.OrderByDescending(p => p.Prefix.Length))
                if (c.StartsWith(prefix, StringComparison.Ordinal)) return name;
            return null;
        }

        // CD-R / CD-RW: the maker is coded in the ATIP lead-in start time,
        // minutes and seconds plus the tens of the frame number
        // (97:34:23 → "97:34:2").
        private static readonly (string Key, string Name)[] Cd =
        {
            ("97:34:2", "Mitsubishi Chemical (Verbatim)"),
            ("97:24:0", "Taiyo Yuden"),
            ("97:26:6", "CMC Magnetics"),
            ("97:15:1", "Ritek"),
            ("97:31:0", "Ritek"),
        };

        /// <summary>The factory behind a CD's ATIP lead-in time, or null if it's not in the list.</summary>
        public static string? ForCd(int minutes, int seconds, int frames)
        {
            string key = $"{minutes:D2}:{seconds:D2}:{frames / 10}";
            foreach (var (k, name) in Cd)
                if (k == key) return name;
            return null;
        }
    }
}
