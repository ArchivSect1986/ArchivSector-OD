using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArchivSector_OD.Burning
{
    // Direct drive connection (v4.0 week 4): sends SCSI/MMC commands
    // straight to an optical drive through Windows' SCSI pass-through
    // (SPTI, IOCTL_SCSI_PASS_THROUGH_DIRECT), the same way ImgBurn talks
    // to drives. This is what lets ArchivSector-OD write discs itself
    // instead of going through Windows' burning engine (IMAPI2).
    //
    // This file only carries commands to the drive and back; what to send
    // is decided elsewhere (DriveInspector for the Drive info screen, the
    // writer later). Windows sometimes only allows pass-through for apps
    // run as administrator; Open() says so when that's the problem.
    public sealed class ScsiDrive : IDisposable
    {
        public sealed class Reply
        {
            public bool Ok;                 // the drive accepted the command (status GOOD)
            public int Win32Error;          // the ioctl itself failed (0 = it got to the drive)
            public byte ScsiStatus;         // 0 GOOD, 2 CHECK CONDITION, 8 BUSY
            public byte SenseKey;           // why it said no: 2 not ready, 3 medium error, 5 illegal request, 6 unit attention
            public byte Asc, Ascq;          // the detailed reason code
            public int BytesReturned;
            /// <summary>The drive is only busy (getting ready, buffer full, still writing): try again shortly.</summary>
            public bool IsTryAgain => !Ok && Win32Error == 0 &&
                (ScsiStatus == 0x08 || (SenseKey == 2 && Asc == 0x04 && Ascq is 0x00 or 0x01 or 0x04 or 0x07 or 0x08));
            public string Describe() =>
                Ok ? "OK"
                : Win32Error != 0 ? $"Windows error {Win32Error}"
                : $"drive said no (status {ScsiStatus:X2}, sense {SenseKey:X}/{Asc:X2}/{Ascq:X2}: {SenseText(SenseKey, Asc, Ascq)})";
        }

        private readonly SafeFileHandle handle;
        public string DriveLetter { get; }

        private ScsiDrive(SafeFileHandle h, string letter) { handle = h; DriveLetter = letter; }

        /// <summary>Opens a drive for direct commands. Returns null with a reason if it can't.</summary>
        public static ScsiDrive? Open(string driveLetter, out string? problem)
        {
            var letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
            var h = CreateFile($@"\\.\{letter}:", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int e = Marshal.GetLastWin32Error();
                h.Dispose();
                problem = e == 5
                    ? $"Windows didn't allow direct access to drive {letter}:. Close ArchivSector-OD and start it with right-click → Run as administrator."
                    : $"Couldn't open drive {letter}: (Windows error {e}).";
                return null;
            }
            problem = null;
            return new ScsiDrive(h, letter);
        }

        public void Dispose()
        {
            if (locked) Unlock();
            handle.Dispose();
        }

        private bool locked;

        /// <summary>
        /// Before writing: takes the disc away from Windows (locks and
        /// dismounts its file system) so Explorer, AutoPlay or antivirus
        /// can't read it in the middle of a write. A blank disc has no
        /// file system, so a failure here is normal and harmless.
        /// </summary>
        public bool LockAndDismount()
        {
            bool ok = DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            locked = ok;
            return ok;
        }

        public void Unlock()
        {
            DeviceIoControl(handle, FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            locked = false;
        }

        /// <summary>A command that reads data back from the drive (or none).</summary>
        public Reply Read(byte[] cdb, byte[]? data, int timeoutSeconds = 30) => Send(cdb, data, DataIn, timeoutSeconds);

        /// <summary>A command that sends data to the drive.</summary>
        public Reply Write(byte[] cdb, byte[] data, int timeoutSeconds = 60) => Send(cdb, data, DataOut, timeoutSeconds);

        /// <summary>A command with no data either way.</summary>
        public Reply Command(byte[] cdb, int timeoutSeconds = 60) => Send(cdb, null, DataUnspecified, timeoutSeconds);

        private Reply Send(byte[] cdb, byte[]? data, byte direction, int timeoutSeconds)
        {
            var reply = new Reply();
            int len = data?.Length ?? 0;
            IntPtr dataBuf = len > 0 ? Marshal.AllocHGlobal(len) : IntPtr.Zero;
            var sptd = new SptdWithSense
            {
                Sptd = new Sptd
                {
                    Length = (ushort)Marshal.SizeOf<Sptd>(),
                    CdbLength = (byte)cdb.Length,
                    SenseInfoLength = SenseSize,
                    DataIn = len > 0 ? direction : DataUnspecified,
                    DataTransferLength = (uint)len,
                    TimeOutValue = (uint)timeoutSeconds,
                    DataBuffer = dataBuf,
                    SenseInfoOffset = (uint)Marshal.OffsetOf<SptdWithSense>(nameof(SptdWithSense.Sense)),
                    Cdb = new byte[16],
                },
                Sense = new byte[SenseSize],
            };
            Array.Copy(cdb, sptd.Sptd.Cdb, Math.Min(16, cdb.Length));
            int size = Marshal.SizeOf<SptdWithSense>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (len > 0)
                {
                    if (direction == DataOut) Marshal.Copy(data!, 0, dataBuf, len);
                    else Marshal.Copy(new byte[len], 0, dataBuf, len);
                }
                Marshal.StructureToPtr(sptd, buf, false);
                bool ok = DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH_DIRECT, buf, (uint)size, buf, (uint)size, out uint _, IntPtr.Zero);
                if (!ok)
                {
                    reply.Win32Error = Marshal.GetLastWin32Error();
                    return reply;
                }
                var back = Marshal.PtrToStructure<SptdWithSense>(buf);
                reply.ScsiStatus = back.Sptd.ScsiStatus;
                reply.BytesReturned = (int)back.Sptd.DataTransferLength;
                if (back.Sptd.ScsiStatus == 0)
                {
                    reply.Ok = true;
                    if (len > 0 && direction == DataIn) Marshal.Copy(dataBuf, data!, 0, len);
                }
                else if (back.Sense is { Length: >= 14 })
                {
                    // Fixed-format sense data: key in byte 2, ASC/ASCQ in 12/13.
                    reply.SenseKey = (byte)(back.Sense[2] & 0x0F);
                    reply.Asc = back.Sense[12];
                    reply.Ascq = back.Sense[13];
                }
                return reply;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                if (dataBuf != IntPtr.Zero) Marshal.FreeHGlobal(dataBuf);
            }
        }

        // Plain-English meaning of the commonest answers.
        public static string SenseText(byte key, byte asc, byte ascq) => (key, asc, ascq) switch
        {
            (2, 0x3A, _) => "no disc in the drive",
            (2, 0x04, _) => "the drive is getting ready",
            (5, 0x20, 0) => "the drive doesn't know this command",
            (5, 0x24, 0) => "the drive didn't accept a value in the command",
            (5, 0x30, _) => "this command doesn't work with the disc in the drive",
            (6, 0x28, 0) => "the disc was just changed",
            (6, 0x29, _) => "the drive was reset",
            // Write problems on the disc itself (sense key 3, "medium error").
            (3, 0x73, 0x01) or (3, 0x73, 0x02) => "the disc's laser-calibration area is full (an old or much-rewritten disc)",
            (3, 0x73, 0x03) => "the drive couldn't tune its laser for this disc",
            (3, 0x73, 0x04) => "the drive couldn't record where the track is on the disc",
            (3, 0x73, 0x05) => "the disc's track list is full",
            (3, 0x73, _) => "writing failed on this disc",
            (3, 0x0C, 0x09) => "the data stopped arriving in time while writing",
            (3, 0x0C, _) => "a write to the disc failed",
            (3, 0x11, _) => "the disc couldn't be read there",
            (3, 0x31, _) => "the disc's format is damaged",
            (3, _, _) => "the disc couldn't be read or written there",
            (4, _, _) => "the drive reported a hardware problem",
            _ => key switch { 2 => "not ready", 5 => "not allowed", 6 => "something changed", _ => "unknown reason" },
        };

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
        private const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_UNLOCK_VOLUME = 0x0009001C, FSCTL_DISMOUNT_VOLUME = 0x00090020;
        private const byte DataOut = 0, DataIn = 1, DataUnspecified = 2;
        private const byte SenseSize = 32;

        [StructLayout(LayoutKind.Sequential)]
        private struct Sptd
        {
            public ushort Length;
            public byte ScsiStatus;
            public byte PathId;
            public byte TargetId;
            public byte Lun;
            public byte CdbLength;
            public byte SenseInfoLength;
            public byte DataIn;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public IntPtr DataBuffer;
            public uint SenseInfoOffset;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Cdb;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SptdWithSense
        {
            public Sptd Sptd;
            public uint Filler;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = SenseSize)]
            public byte[] Sense;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    }
}
