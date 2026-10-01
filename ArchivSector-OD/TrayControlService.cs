using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ArchivSector_OD
{
    // Ported from the Python app's physical_eject_tray() and
    // physical_close_tray() -- a three-tier fallback chain for each.
    // TrayResult reports WHICH tier claimed success, not just a bool.
    //
    // Eject's raw-IOCTL tier now checks FSCTL_LOCK_VOLUME and
    // FSCTL_DISMOUNT_VOLUME's own return values instead of ignoring
    // them -- a real, confirmed bug: those two calls can silently
    // fail (a well-documented Windows quirk) if anything else has the
    // volume open at that moment, including this app's own 2-second
    // background drive-polling, and when the lock fails, the final
    // IOCTL_STORAGE_EJECT_MEDIA call can still report success at the
    // API level while the physical tray never actually moves. Now
    // retries the lock a few times with short pauses -- the standard
    // mitigation for this, since the contention is usually transient
    // -- and the result's Detail reports exactly which step failed if
    // it still doesn't work, instead of a blanket "via raw IOCTL"
    // that looked identical whether the tray really moved or not.
    public static class TrayControlService
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808;
        private const uint IOCTL_STORAGE_LOAD_MEDIA = 0x2D480C;
        private const uint FSCTL_LOCK_VOLUME = 0x00090018;
        private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(
            string lpstrCommand, System.Text.StringBuilder? lpstrReturnString,
            int uReturnLength, IntPtr hwndCallback);

        public class TrayResult
        {
            public bool Success;
            public string Detail = "";
        }

        public static TrayResult Eject(string driveLetter)
        {
            var letter = driveLetter.ToUpperInvariant().TrimEnd(':', '\\');

            try
            {
                var cmd = $"(New-Object -comObject Shell.Application).Namespace(17).ParseName('{letter}:').InvokeVerb('Eject')";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(cmd);

                using var proc = Process.Start(psi);
                if (proc is not null && proc.WaitForExit(3000) && proc.ExitCode == 0)
                    return new TrayResult { Success = true, Detail = "via PowerShell Shell.Application" };
                try { if (proc is not null && !proc.HasExited) proc.Kill(); } catch { }
            }
            catch { /* fall through to tier 2 */ }

            try
            {
                var handle = CreateFile($@"\\.\{letter}:",
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                {
                    // FSCTL_LOCK_VOLUME can fail if anything else has
                    // the volume open at this instant -- retry a few
                    // times with short pauses, since that contention
                    // is usually transient.
                    bool lockOk = false;
                    for (int attempt = 0; attempt < 5 && !lockOk; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(200);
                        lockOk = DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    }

                    bool dismountOk = DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    bool ejectOk = DeviceIoControl(handle, IOCTL_STORAGE_EJECT_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    CloseHandle(handle);

                    if (ejectOk && lockOk && dismountOk)
                        return new TrayResult { Success = true, Detail = "via raw IOCTL" };

                    if (ejectOk && (!lockOk || !dismountOk))
                    {
                        // The eject call itself reported success, but
                        // the volume wasn't actually locked/dismounted
                        // first -- exactly the failure mode that used
                        // to look identical to a real success. Don't
                        // fall through to MCI (a real eject call did
                        // fire), but be honest that it may not have
                        // physically worked.
                        return new TrayResult
                        {
                            Success = true,
                            Detail = $"via raw IOCTL, but lock={lockOk} dismount={dismountOk} -- the tray may not have actually opened if something else had the volume open"
                        };
                    }
                }
            }
            catch { /* fall through to tier 3 */ }

            try
            {
                mciSendString($"open {letter}: type cdaudio alias dev_{letter}", null, 0, IntPtr.Zero);
                mciSendString($"set dev_{letter} door open", null, 0, IntPtr.Zero);
                mciSendString($"close dev_{letter}", null, 0, IntPtr.Zero);
                return new TrayResult { Success = true, Detail = "via legacy MCI (commands sent, but this tier can't confirm the tray actually moved)" };
            }
            catch { /* all three tiers failed to even execute */ }

            return new TrayResult { Success = false, Detail = "all three eject methods failed to execute" };
        }

        public static TrayResult Close(string driveLetter)
        {
            var letter = driveLetter.ToUpperInvariant().TrimEnd(':', '\\');

            try
            {
                var handle = CreateFile($@"\\.\{letter}:",
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle != IntPtr.Zero && handle.ToInt64() != -1)
                {
                    bool loadOk = DeviceIoControl(handle, IOCTL_STORAGE_LOAD_MEDIA, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
                    CloseHandle(handle);
                    if (loadOk)
                        return new TrayResult { Success = true, Detail = "via raw IOCTL" };
                }
            }
            catch { /* fall through to tier 2 */ }

            try
            {
                mciSendString($"open {letter}: type cdaudio alias dev_{letter}", null, 0, IntPtr.Zero);
                mciSendString($"set dev_{letter} door closed", null, 0, IntPtr.Zero);
                mciSendString($"close dev_{letter}", null, 0, IntPtr.Zero);
                return new TrayResult { Success = true, Detail = "via legacy MCI (commands sent, but this tier can't confirm the tray actually moved)" };
            }
            catch { /* both tiers failed to even execute */ }

            return new TrayResult { Success = false, Detail = "both close methods failed to execute" };
        }
    }
}