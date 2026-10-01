using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ArchivSector_OD
{
    // Ported from the Python app's _run_preflight_check() and
    // _check_makemkv_license_status(). Runs right before a rip starts,
    // catching a category of failures that otherwise show up as
    // confusing instant deaths deep inside a worker with no real
    // explanation. Tool-existence itself isn't re-checked here --
    // MakeMkvService/RedumperService's own FindExecutable already
    // handles that with its own clear error message before this runs.
    //
    // The license check is deliberately a broad, keyword-based
    // heuristic rather than matching a specific message code -- an
    // honest "might be a problem" is safer than pretending to know
    // MakeMKV's precise expired-key wording. A failed or inconclusive
    // check never blocks a rip on its own.
    //
    // unattended=true replaces both confirmation dialogs below with
    // silent logging instead -- matching the Python app's own
    // unattended preflight behavior: nobody's necessarily there to
    // click a dialog for an auto-started rip, so low disk space and a
    // possible license issue are logged as warnings and the rip
    // proceeds, rather than sitting blocked waiting for a click that
    // will never come.
    public static class PreflightService
    {
        public enum LicenseStatus { Ok, ExpiredOrInvalid, CheckFailed }

        public static LicenseStatus CheckMakeMkvLicense(string makeMkvExePath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = makeMkvExePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-r");
                psi.ArgumentList.Add("info");
                psi.ArgumentList.Add("disc:9999");

                using var proc = Process.Start(psi);
                if (proc is null) return LicenseStatus.CheckFailed;

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(8000);

                var output = (stdout + stderr).ToLowerInvariant();
                string[] redFlags =
                {
                    "key has expired", "key is expired", "expired key", "invalid key",
                    "please enter a valid key", "trial period", "evaluation period has expired",
                    "application version is too old", "enter a registration key",
                };
                return redFlags.Any(f => output.Contains(f)) ? LicenseStatus.ExpiredOrInvalid : LicenseStatus.Ok;
            }
            catch
            {
                return LicenseStatus.CheckFailed;
            }
        }

        public class PreflightResult
        {
            public bool Ok;
            public string? BlockedReason;
        }

        public static PreflightResult Check(bool mediaPresent, string outputBaseFolder, string? makeMkvExeForLicenseCheck, Action<string>? log = null, bool unattended = false)
        {
            if (!mediaPresent)
            {
                var msg = "This drive doesn't currently report a disc inserted.";
                log?.Invoke($"[PREFLIGHT FAILED] {msg}");
                return new PreflightResult { Ok = false, BlockedReason = msg };
            }

            try
            {
                var root = Path.GetPathRoot(string.IsNullOrWhiteSpace(outputBaseFolder)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    : outputBaseFolder);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    long freeBytes = drive.AvailableFreeSpace;
                    if (freeBytes < 1_000_000_000L)
                    {
                        double freeGb = freeBytes / 1_000_000_000.0;
                        if (unattended)
                        {
                            log?.Invoke($"[PREFLIGHT] Only about {freeGb:F1}GB free at {outputBaseFolder} -- unattended, proceeding anyway.");
                        }
                        else
                        {
                            var choice = System.Windows.MessageBox.Show(
                                $"Only about {freeGb:F1}GB free at your output location:\n{outputBaseFolder}\n\nThis likely isn't enough for a full disc backup. Continue anyway?",
                                "Low Disk Space", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                            if (choice != System.Windows.MessageBoxResult.Yes)
                            {
                                log?.Invoke($"[PREFLIGHT] Only about {freeGb:F1}GB free -- cancelled by user.");
                                return new PreflightResult { Ok = false, BlockedReason = "Cancelled due to low disk space" };
                            }
                            log?.Invoke($"[PREFLIGHT] Only about {freeGb:F1}GB free -- proceeding anyway per user confirmation.");
                        }
                    }
                }
            }
            catch
            {
                // Best-effort -- never block a rip just because the
                // space check itself failed.
            }

            if (makeMkvExeForLicenseCheck is not null)
            {
                var status = CheckMakeMkvLicense(makeMkvExeForLicenseCheck);
                if (status == LicenseStatus.ExpiredOrInvalid)
                {
                    if (unattended)
                    {
                        log?.Invoke("[PREFLIGHT] Possible MakeMKV license issue detected -- unattended, proceeding anyway.");
                    }
                    else
                    {
                        var choice = System.Windows.MessageBox.Show(
                            "MakeMKV's own output mentioned something about an expired, missing, or invalid key/trial status. This can cause a rip to fail partway through with a confusing, generic error.\n\nYou may want to check/renew your MakeMKV beta key at makemkv.com before continuing.\n\nContinue anyway?",
                            "MakeMKV License Warning", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                        if (choice != System.Windows.MessageBoxResult.Yes)
                        {
                            log?.Invoke("[PREFLIGHT] Possible MakeMKV license issue -- cancelled by user.");
                            return new PreflightResult { Ok = false, BlockedReason = "Cancelled due to possible license issue" };
                        }
                        log?.Invoke("[PREFLIGHT] Possible MakeMKV license issue -- proceeding anyway per user confirmation.");
                    }
                }
                // CheckFailed (inconclusive) and Ok both fall through --
                // never block a rip on a heuristic that couldn't be confirmed.
            }

            return new PreflightResult { Ok = true };
        }
    }
}