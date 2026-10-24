namespace ArchivSector_OD.Burning;

/// <summary>
/// Physical disc type, using the same numbers as IMAPI2's
/// IMAPI_MEDIA_PHYSICAL_TYPE so values can be cast straight across.
/// </summary>
public enum DiscMediaType
{
    Unknown = 0,
    CdRom = 1,
    CdR = 2,
    CdRw = 3,
    DvdRom = 4,
    DvdRam = 5,
    DvdPlusR = 6,
    DvdPlusRw = 7,
    DvdPlusRDualLayer = 8,
    DvdDashR = 9,
    DvdDashRw = 10,
    DvdDashRDualLayer = 11,
    Disk = 12,
    DvdPlusRwDualLayer = 13,
    HdDvdRom = 14,
    HdDvdR = 15,
    HdDvdRam = 16,
    BdRom = 17,
    BdR = 18,
    BdRe = 19
}

/// <summary>What can be done with the disc currently in the tray.</summary>
public enum MediaState
{
    NoDisc,         // tray empty (or drive can't see the disc)
    Blank,          // ready to burn
    Appendable,     // has data, more sessions can be added
    Overwritable,   // rewritable disc with data on it (DVD+RW, BD-RE)
    Full,           // closed / finalized - nothing more can be written
    NotWritable,    // pressed disc (movie, game), write-protected or damaged
    Unknown         // drive answered but IMAPI2 couldn't classify it
}

/// <summary>Information about the disc in a drive.</summary>
public sealed class MediaInfo
{
    public static readonly MediaInfo None = new() { State = MediaState.NoDisc };

    public MediaState State { get; init; }
    public DiscMediaType Type { get; init; }

    /// <summary>Raw IMAPI2 media status flags, kept for troubleshooting.</summary>
    public int StatusFlags { get; init; }

    public long TotalSectors { get; init; }
    public long FreeSectors { get; init; }

    /// <summary>Data discs use 2048-byte sectors.</summary>
    public long TotalBytes => TotalSectors * 2048L;
    public long FreeBytes => FreeSectors * 2048L;

    // Windows formats a blank BD-R/BD-RE with spare areas for error
    // handling (768 MB on a single-layer disc) and only reports what's left,
    // so a BD-25 shows as 24,220,008,448 bytes. Burning without the spare
    // areas (ImgBurn, or this engine later) uses the whole disc.
    private const long Bd25Bytes = 12_219_392L * 2048, Bd50Bytes = 24_438_784L * 2048;

    /// <summary>
    /// The disc's real size. Same as TotalBytes except for a blank BD-R
    /// and any BD-RE, where it's the standard BD-25 / BD-50 size: Windows
    /// reports them with 768 MB (BD-25) kept spare, but ImgBurn and
    /// ArchivSector-OD's own writer use the whole disc (a BD-RE is
    /// reformatted to it when needed), as Drive info confirms.
    /// </summary>
    public long FullDiscBytes
    {
        get
        {
            bool wholeBd = (State == MediaState.Blank && Type == DiscMediaType.BdR) || Type == DiscMediaType.BdRe;
            if (!wholeBd || TotalBytes <= 0) return TotalBytes;
            if (TotalBytes <= Bd25Bytes) return Bd25Bytes;
            if (TotalBytes <= Bd50Bytes) return Bd50Bytes;
            return TotalBytes;   // BD-XL: use what Windows says
        }
    }

    /// <summary>How much Windows' burning engine keeps spare (0 if none).</summary>
    public long WindowsSpareBytes => Math.Max(0, FullDiscBytes - TotalBytes);

    public bool IsRewritable => Type is DiscMediaType.CdRw
        or DiscMediaType.DvdRam
        or DiscMediaType.DvdPlusRw
        or DiscMediaType.DvdDashRw
        or DiscMediaType.DvdPlusRwDualLayer
        or DiscMediaType.BdRe
        or DiscMediaType.HdDvdRam;

    public bool CanBurnNow => State is MediaState.Blank
        or MediaState.Appendable
        or MediaState.Overwritable;

    public string TypeName => Type switch
    {
        DiscMediaType.CdRom => "CD-ROM",
        DiscMediaType.CdR => "CD-R",
        DiscMediaType.CdRw => "CD-RW",
        DiscMediaType.DvdRom => "DVD-ROM",
        DiscMediaType.DvdRam => "DVD-RAM",
        DiscMediaType.DvdPlusR => "DVD+R",
        DiscMediaType.DvdPlusRw => "DVD+RW",
        DiscMediaType.DvdPlusRDualLayer => "DVD+R DL",
        DiscMediaType.DvdDashR => "DVD-R",
        DiscMediaType.DvdDashRw => "DVD-RW",
        DiscMediaType.DvdDashRDualLayer => "DVD-R DL",
        DiscMediaType.DvdPlusRwDualLayer => "DVD+RW DL",
        DiscMediaType.HdDvdRom => "HD DVD-ROM",
        DiscMediaType.HdDvdR => "HD DVD-R",
        DiscMediaType.HdDvdRam => "HD DVD-RAM",
        DiscMediaType.BdRom => "BD-ROM",
        DiscMediaType.BdR => "BD-R",
        DiscMediaType.BdRe => "BD-RE",
        DiscMediaType.Disk => "Disk",
        _ => "Unknown disc"
    };

    public string StateText => State switch
    {
        MediaState.NoDisc => "No disc",
        MediaState.Blank => "Blank",
        MediaState.Appendable => "Has data - can add more",
        MediaState.Overwritable => "Has data - can be overwritten",
        MediaState.Full => IsRewritable ? "Closed - erase it to reuse" : "Closed - can't write more",
        MediaState.NotWritable => "Not writable",
        _ => "Unknown"
    };
}

/// <summary>One optical recorder found on the system.</summary>
public sealed class BurnerDrive
{
    /// <summary>IMAPI2's ID for the drive. Needed later to burn to it.</summary>
    public required string UniqueId { get; init; }

    /// <summary>Drive letter like "E:", or empty if Windows hasn't assigned one.</summary>
    public required string DriveLetter { get; init; }

    public string Vendor { get; init; } = "";
    public string Model { get; init; } = "";
    public string Firmware { get; init; } = "";

    public bool CanWriteCd { get; init; }
    public bool CanWriteDvd { get; init; }
    public bool CanWriteDvdDualLayer { get; init; }
    public bool CanWriteBluRay { get; init; }
    public bool CanWrite => CanWriteCd || CanWriteDvd || CanWriteBluRay;

    public MediaInfo Media { get; init; } = MediaInfo.None;

    /// <summary>
    /// The drive was busy (ripping, burning, checking) so its disc wasn't
    /// read -- sending it commands could disturb that work.
    /// </summary>
    public bool Busy { get; init; }

    /// <summary>False when the drive was busy and couldn't be asked what it writes.</summary>
    public bool CapabilitiesKnown { get; init; } = true;

    /// <summary>Set if the drive couldn't be read; the other fields may be empty.</summary>
    public string? Error { get; init; }

    public string DisplayName
    {
        get
        {
            var name = $"{Vendor} {Model}".Trim();
            if (name.Length == 0) name = "Optical drive";
            return DriveLetter.Length > 0 ? $"({DriveLetter}) {name}" : name;
        }
    }

    /// <summary>Short list like "CD, DVD, DVD DL, BD" for the UI.</summary>
    public string WriteFormatsText
    {
        get
        {
            if (!CapabilitiesKnown) return "— (busy, not asked)";
            var parts = new List<string>();
            if (CanWriteCd) parts.Add("CD");
            if (CanWriteDvd) parts.Add("DVD");
            if (CanWriteDvdDualLayer) parts.Add("DVD DL");
            if (CanWriteBluRay) parts.Add("BD");
            return parts.Count > 0 ? string.Join(", ", parts) : "Read only";
        }
    }
}
