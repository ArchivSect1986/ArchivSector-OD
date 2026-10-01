using System.Diagnostics;
using System.Linq;
using System.Text.Json;

namespace ArchivSector_OD
{
    // Ported from the Python app's get_all_drive_models_and_firmware()
    // and check_omnidrive_model_compatibility() -- a single PowerShell
    // Get-CimInstance query against Win32_CDROMDrive. Run once at app
    // startup rather than on the 2-second drive-poll timer: it's a
    // real WMI/CIM round trip, not a cheap local call like DriveInfo,
    // so polling it that often would slow everything else down for no
    // benefit -- a drive's model and firmware don't change while the
    // app is running. This means a drive hot-plugged after launch
    // won't show hardware info until restart -- a known, reasonable
    // scope limit rather than an oversight.
    //
    // CheckOmniDriveCompatibility checks a drive's model string
    // against the known list of LibreDrive-compatible burners --
    // LG/Pioneer/ASUS units built on the MediaTek MT1959 chipset that
    // MakeMKV's own LibreDrive raw UHD Blu-ray decryption relies on.
    // Being on this list means the MODEL is known-compatible, not
    // that this specific unit has actually been flashed with
    // LibreDrive firmware -- that still has to be done and confirmed
    // separately, which is why the UI pairs this with a persisted,
    // user-set confirmation checkbox rather than treating a model
    // match as proof by itself.
    public class DriveHardwareInfo
    {
        public string Caption = "";
        public string Firmware = "";
    }

    public static class DriveHardwareService
    {
        private static readonly string[] OmniDriveCompatibleModelTokens =
        {
            "BC-12D2HT", "BC-12B1ST", "BW-16D1HT", "BW-16D1X-U",
            "UH12NS40", "CH12NS40", "WH14NS40", "BH14NS40", "BH16NS40",
            "WH16NS40", "BH16NS50", "BH14NS50", "BH16NS55", "BH14NS58",
            "BH16NS58", "WH16NS58", "BH16NS60", "WH16NS60", "BH-A10AME",
            "BH40N", "BH50N", "BE16NU50",
            "BU40N", "BU50N", "BP71N", "WP50NB40", "BP50NB40", "BP55EB40",
            "BP60NB10", "MD-8107-U3", "SBW-06D5H-U", "SBW-06D2X-U",
            "BRUHD-PU3-BK", "BRXL-PT6U2V", "BRXL-PTV6U3", "BRXL-PUS6U3B",
            "43888", "43889", "43890",
        };

        // Returns null when the model string is empty/unknown (not
        // "incompatible" -- there's simply nothing to check), true if
        // it matches a known-compatible model, false otherwise.
        public static bool? CheckOmniDriveCompatibility(string? modelString)
        {
            if (string.IsNullOrWhiteSpace(modelString)) return null;
            var upper = modelString.ToUpperInvariant();
            return OmniDriveCompatibleModelTokens.Any(token => upper.Contains(token));
        }

        public static Dictionary<string, DriveHardwareInfo> GetAllDriveModelsAndFirmware()
        {
            var result = new Dictionary<string, DriveHardwareInfo>();
            try
            {
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
                psi.ArgumentList.Add("Get-CimInstance Win32_CDROMDrive | Select-Object Drive,Caption,RevisionLevel,MfrAssignedRevisionLevel | ConvertTo-Json");

                using var proc = Process.Start(psi);
                if (proc is null) return result;
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(4000);

                if (string.IsNullOrWhiteSpace(stdout)) return result;

                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                var items = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray().ToList()
                    : new List<JsonElement> { root };

                foreach (var entry in items)
                {
                    var driveRaw = entry.TryGetProperty("Drive", out var d) ? (d.GetString() ?? "") : "";
                    var letter = driveRaw.TrimEnd(':', '\\').ToUpperInvariant();
                    if (string.IsNullOrEmpty(letter)) continue;

                    var caption = entry.TryGetProperty("Caption", out var c) ? (c.GetString() ?? "") : "";
                    var fw = entry.TryGetProperty("RevisionLevel", out var r) ? (r.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(fw) && entry.TryGetProperty("MfrAssignedRevisionLevel", out var m))
                        fw = m.GetString() ?? "";

                    result[letter] = new DriveHardwareInfo { Caption = caption, Firmware = fw.Trim() };
                }
            }
            catch
            {
                // Best-effort -- an app without drive-hardware info is
                // still fully usable, so failures here are silent.
            }
            return result;
        }
    }
}