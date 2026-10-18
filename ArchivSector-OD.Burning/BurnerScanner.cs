using System.Runtime.InteropServices;

namespace ArchivSector_OD.Burning;

/// <summary>
/// Finds disc recorders and reads what disc (if any) is in each one,
/// using IMAPI2 (built into Windows 7 and later).
///
/// IMAPI2 is used through late-bound COM ("dynamic"), so the project
/// needs no COM references and builds with Visual Studio or dotnet CLI.
/// Don't turn on PublishTrimmed for the app - trimming breaks dynamic COM.
/// </summary>
public static class BurnerScanner
{
    // IMAPI_FORMAT2_DATA_MEDIA_STATE flags
    private const int StateOverwriteOnly = 0x0001;
    private const int StateBlank = 0x0002;
    private const int StateAppendable = 0x0004;
    private const int StateFinalSession = 0x0008;
    private const int StateDamaged = 0x0400;
    private const int StateEraseRequired = 0x0800;
    private const int StateWriteProtected = 0x2000;
    private const int StateFinalized = 0x4000;
    private const int StateUnsupportedMedia = 0x8000;

    // IMAPI_PROFILE_TYPE values grouped by what they mean for writing
    private static readonly HashSet<int> CdWriteProfiles = new() { 0x09, 0x0A };
    private static readonly HashSet<int> DvdWriteProfiles = new() { 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x1A, 0x1B, 0x2A, 0x2B };
    private static readonly HashSet<int> DvdDualLayerProfiles = new() { 0x15, 0x16, 0x2A, 0x2B };
    private static readonly HashSet<int> BluRayWriteProfiles = new() { 0x41, 0x42, 0x43 };

    /// <summary>
    /// Scans every recorder. Runs on its own STA background thread so the
    /// UI stays responsive while drives spin up.
    /// <paramref name="isBusy"/> is asked with each drive letter ("E:");
    /// a busy drive (ripping, burning...) only gets its name and letter
    /// read, never a disc query, so its work isn't disturbed.
    /// </summary>
    public static Task<IReadOnlyList<BurnerDrive>> ScanAsync(Func<string, bool>? isBusy = null) =>
        RunOnStaThread(() => Scan(isBusy));

    /// <summary>Re-reads a single drive, e.g. after a disc is inserted.</summary>
    public static Task<BurnerDrive> ScanDriveAsync(string uniqueId, Func<string, bool>? isBusy = null) =>
        RunOnStaThread(() => ReadDrive(uniqueId, isBusy));

    // ------------------------------------------------------------------

    private static IReadOnlyList<BurnerDrive> Scan(Func<string, bool>? isBusy)
    {
        var drives = new List<BurnerDrive>();
        dynamic? master = null;
        try
        {
            master = CreateCom("IMAPI2.MsftDiscMaster2");

            if (!(bool)master.IsSupportedEnvironment)
                return drives; // no recorders, or burning disabled by policy

            int count = (int)master.Count;
            for (int i = 0; i < count; i++)
            {
                string uniqueId = (string)master[i];
                drives.Add(ReadDrive(uniqueId, isBusy));
            }
        }
        finally
        {
            Release(master);
        }

        return drives
            .OrderBy(d => d.DriveLetter.Length == 0 ? "~" : d.DriveLetter)
            .ToList();
    }

    private static BurnerDrive ReadDrive(string uniqueId, Func<string, bool>? isBusy)
    {
        dynamic? recorder = null;
        try
        {
            recorder = CreateCom("IMAPI2.MsftDiscRecorder2");
            recorder.InitializeDiscRecorder(uniqueId);

            // Name, letter and abilities come from Windows' own records;
            // only the disc query below actually talks to the drive.
            string letter = FirstDriveLetter(TryGet(() => (object?)recorder.VolumePathNames));
            bool busy = letter.Length > 0 && isBusy is not null && SafeIsBusy(isBusy, letter);
            // Asking what a drive can write is a command to the drive itself;
            // a busy (burning) drive can't answer, so it isn't asked.
            var supported = busy ? new HashSet<int>() : ToInts(TryGet(() => (object?)recorder.SupportedProfiles));

            return new BurnerDrive
            {
                UniqueId = uniqueId,
                DriveLetter = letter,
                Vendor = ((string?)TryGet(() => (object?)recorder.VendorId) ?? "").Trim(),
                Model = ((string?)TryGet(() => (object?)recorder.ProductId) ?? "").Trim(),
                Firmware = ((string?)TryGet(() => (object?)recorder.ProductRevision) ?? "").Trim(),
                CanWriteCd = supported.Overlaps(CdWriteProfiles),
                CanWriteDvd = supported.Overlaps(DvdWriteProfiles),
                CanWriteDvdDualLayer = supported.Overlaps(DvdDualLayerProfiles),
                CanWriteBluRay = supported.Overlaps(BluRayWriteProfiles),
                Busy = busy,
                CapabilitiesKnown = !busy,
                Media = busy ? MediaInfo.None : ReadMedia(recorder)
            };
        }
        catch (Exception ex)
        {
            return new BurnerDrive
            {
                UniqueId = uniqueId,
                DriveLetter = "",
                Error = ex.Message
            };
        }
        finally
        {
            Release(recorder);
        }
    }

    private static MediaInfo ReadMedia(dynamic recorder)
    {
        // No current profiles (or an error asking) means the tray is empty.
        HashSet<int> current;
        try
        {
            current = ToInts((object?)recorder.CurrentProfiles);
        }
        catch (COMException)
        {
            return MediaInfo.None;
        }
        if (current.Count == 0)
            return MediaInfo.None;

        dynamic? format = null;
        try
        {
            format = CreateCom("IMAPI2.MsftDiscFormat2Data");

            if (!(bool)format.IsRecorderSupported(recorder) ||
                !(bool)format.IsCurrentMediaSupported(recorder))
            {
                // A disc is in, but it can't be written (pressed movie/game disc etc.)
                return new MediaInfo
                {
                    State = MediaState.NotWritable,
                    Type = TypeFromProfiles(current)
                };
            }

            format.Recorder = recorder;

            int type = Convert.ToInt32(format.CurrentPhysicalMediaType);
            int status = Convert.ToInt32(format.CurrentMediaStatus);
            long total = Convert.ToInt64(format.TotalSectorsOnMedia);
            long free = Convert.ToInt64(format.FreeSectorsOnMedia);

            return new MediaInfo
            {
                Type = Enum.IsDefined(typeof(DiscMediaType), type) ? (DiscMediaType)type : DiscMediaType.Unknown,
                State = StateFromFlags(status),
                StatusFlags = status,
                TotalSectors = total,
                FreeSectors = free
            };
        }
        catch (COMException)
        {
            return new MediaInfo { State = MediaState.Unknown, Type = TypeFromProfiles(current) };
        }
        finally
        {
            Release(format);
        }
    }

    private static MediaState StateFromFlags(int flags)
    {
        if ((flags & (StateUnsupportedMedia | StateWriteProtected | StateDamaged)) != 0)
            return MediaState.NotWritable;
        if ((flags & StateBlank) != 0)
            return MediaState.Blank;
        if ((flags & StateAppendable) != 0)
            return MediaState.Appendable;
        if ((flags & (StateOverwriteOnly | StateEraseRequired)) != 0)
            return MediaState.Overwritable;
        if ((flags & (StateFinalized | StateFinalSession)) != 0)
            return MediaState.Full;
        return MediaState.Unknown;
    }

    /// <summary>Best guess at disc type for discs IMAPI2 won't write to.</summary>
    private static DiscMediaType TypeFromProfiles(HashSet<int> profiles)
    {
        if (profiles.Contains(0x40)) return DiscMediaType.BdRom;
        if (profiles.Contains(0x10)) return DiscMediaType.DvdRom;
        if (profiles.Contains(0x08)) return DiscMediaType.CdRom;
        if (profiles.Overlaps(BluRayWriteProfiles)) return DiscMediaType.BdR;
        if (profiles.Overlaps(DvdWriteProfiles)) return DiscMediaType.DvdPlusR;
        if (profiles.Overlaps(CdWriteProfiles)) return DiscMediaType.CdR;
        return DiscMediaType.Unknown;
    }

    // ------------------------------------------------------------------
    // Helpers

    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
            ?? throw new InvalidOperationException(
                $"Windows disc burning (IMAPI2) isn't available on this PC ({progId} not found).");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Couldn't create {progId}.");
    }

    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    private static bool SafeIsBusy(Func<string, bool> isBusy, string letter)
    {
        try { return isBusy(letter); }
        catch { return true; }   // if in doubt, leave the drive alone
    }

    private static object? TryGet(Func<object?> getter)
    {
        try { return getter(); }
        catch (COMException) { return null; }
    }

    private static HashSet<int> ToInts(object? comArray)
    {
        var result = new HashSet<int>();
        if (comArray is Array array)
            foreach (var item in array)
                if (item != null)
                    result.Add(Convert.ToInt32(item));
        return result;
    }

    private static string FirstDriveLetter(object? volumePaths)
    {
        if (volumePaths is Array array)
            foreach (var item in array)
                if (item is string path && path.Length >= 2 && path[1] == ':')
                    return path[..2].ToUpperInvariant();
        return "";
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "ArchivSector Burner Scan"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
