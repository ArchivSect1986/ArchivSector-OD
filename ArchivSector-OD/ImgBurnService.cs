using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace ArchivSector_OD
{
    // Ported from the Python app's find_imgburn_exe() and its two
    // roles: _imgburn_worker() (BD Full Backup's optional second
    // stage, converting MakeMKV's decrypted folder into a single-file
    // ISO) and the ImgBurn branch inside _dvd_iso_worker() (a fallback
    // for DVD's raw ISO dump when Redumper isn't available). Both are
    // automatic whenever ImgBurn is found -- no separate toggle, same
    // as MakeMKV/Redumper/mkvmerge detection elsewhere in this app.
    //
    // FindExecutable checks the Windows Registry's "App Paths" keys
    // first -- ImgBurn's own installer registers itself there, which
    // is a more authoritative source than guessing a fixed folder,
    // and is how the Python app's own lookup is ordered.
    public static class ImgBurnService
    {
        public static string? FindExecutable(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            foreach (var (hive, subKey) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ImgBurn.exe"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\ImgBurn.exe"),
                (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ImgBurn.exe"),
            })
            {
                try
                {
                    using var key = hive.OpenSubKey(subKey);
                    var path = key?.GetValue(null) as string;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        return path;
                }
                catch { /* registry access can legitimately fail -- keep trying other sources */ }
            }

            var onPath = FindOnPath("ImgBurn.exe") ?? FindOnPath("ImgBurn");
            if (onPath is not null) return onPath;

            foreach (var candidate in new[]
            {
                @"C:\Program Files (x86)\ImgBurn\ImgBurn.exe",
                @"C:\Program Files\ImgBurn\ImgBurn.exe",
            })
            {
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string? FindOnPath(string exeName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry -- skip it */ }
            }
            return null;
        }

        public class ImgBurnResult
        {
            public bool Success;
            public string? IsoPath;
            public string? ErrorMessage;
        }

        // Role 1: converts a folder (a finished MakeMKV decrypted
        // backup) into one single-file ISO. Volume label is truncated
        // to 32 characters -- ImgBurn's own limit.
        public static ImgBurnResult BuildSingleIso(string exePath, string sourceFolder, string outputFolder, string isoName)
        {
            var isoPath = Path.Combine(outputFolder, $"{isoName}.iso");
            try
            {
                Directory.CreateDirectory(outputFolder);
                var volumeLabel = isoName.Length > 32 ? isoName.Substring(0, 32) : isoName;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in new[]
                {
                    "/MODE", "BUILD", "/BUILDMODE", "IMAGEFILE",
                    "/SRC", sourceFolder, "/DEST", isoPath,
                    "/FILESYSTEM", "UDF", "/UDFREVISION", "2.50",
                    "/VOLUMELABEL", volumeLabel,
                    "/START", "/CLOSESUCCESS", "/NOSAVESETTINGS", "/NOIMAGEDETAILS",
                })
                    psi.ArgumentList.Add(arg);

                using var proc = Process.Start(psi);
                if (proc is null) return new ImgBurnResult { ErrorMessage = "Failed to start ImgBurn." };

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(7200000); // 2 hours, matching the Python app

                bool success = File.Exists(isoPath) && new FileInfo(isoPath).Length > 0;
                if (!success)
                {
                    var captured = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    return new ImgBurnResult
                    {
                        ErrorMessage = string.IsNullOrWhiteSpace(captured)
                            ? "ImgBurn did not produce a valid ISO."
                            : captured.Length > 300 ? captured.Substring(0, 200) : captured
                    };
                }

                return new ImgBurnResult { Success = true, IsoPath = isoPath };
            }
            catch (Exception ex)
            {
                return new ImgBurnResult { ErrorMessage = $"ImgBurn build failed: {ex.Message}" };
            }
        }

        // Role 2: a fallback DVD raw-sector dump when Redumper isn't
        // available -- ImgBurn's own direct disc-to-ISO read mode.
        public static ImgBurnResult ReadDiscToIso(string exePath, string driveLetter, string outputFolder, string isoName)
        {
            var isoPath = Path.Combine(outputFolder, $"{isoName}.iso");
            try
            {
                Directory.CreateDirectory(outputFolder);

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in new[]
                {
                    "/MODE", "READ",
                    "/SRC", $"{driveLetter.TrimEnd(':', '\\')}:", "/DEST", isoPath,
                    "/START", "/CLOSESUCCESS", "/NOSAVESETTINGS", "/NOIMAGEDETAILS",
                })
                    psi.ArgumentList.Add(arg);

                using var proc = Process.Start(psi);
                if (proc is null) return new ImgBurnResult { ErrorMessage = "Failed to start ImgBurn." };

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(3600000); // 1 hour, matching the Python app

                bool success = File.Exists(isoPath) && new FileInfo(isoPath).Length > 0;
                if (!success)
                {
                    var captured = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
                    return new ImgBurnResult
                    {
                        ErrorMessage = string.IsNullOrWhiteSpace(captured)
                            ? "ImgBurn failed to read the DVD."
                            : captured.Length > 300 ? captured.Substring(0, 200) : captured
                    };
                }

                return new ImgBurnResult { Success = true, IsoPath = isoPath };
            }
            catch (Exception ex)
            {
                return new ImgBurnResult { ErrorMessage = $"ImgBurn read failed: {ex.Message}" };
            }
        }
    }
}