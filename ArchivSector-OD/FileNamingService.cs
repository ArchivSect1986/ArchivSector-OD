using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArchivSector_OD
{
    // Ported from the Python app's sanitize_filename(), _format_iso_name(),
    // and _check_duplicate() -- a real gap found on a fresh audit pass:
    // this app was naming every output DriveLetter_Tag_Timestamp, never
    // by the disc's actual title, even after real title extraction
    // (DiscMetadataService) existed to make that possible.
    public static class FileNamingService
    {
        public static string SanitizeFilename(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Archived_Disc";
            var cleaned = new string(name.Where(c => !"\\/*?:\"<>|".Contains(c)).ToArray());
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
            return string.IsNullOrEmpty(cleaned) ? "Archived_Disc" : cleaned;
        }

        // Simplified from the Python app's configurable naming_pattern
        // ("{Title}" by default -- Serial/System aren't consistently
        // available here) to just: use the real title when one is
        // known, sanitized for filesystem safety. Falls back to the
        // caller's DriveLetter_Tag_Timestamp scheme when no usable
        // title exists -- a small, deliberate deviation from the
        // Python app's own "Archived_Disc" fallback, which would let
        // genuinely different untitled discs collide into one name;
        // this keeps them distinct while still naming known discs
        // properly.
        public static string BuildOutputName(string title, string fallbackName)
        {
            if (string.IsNullOrWhiteSpace(title) || title == "UNLABELED")
                return fallbackName;
            return SanitizeFilename(title);
        }

        // Checks both a same-named folder and a same-named .iso file
        // at the target location -- covers every output shape this
        // app produces (MakeMKV/Movie-Only folders, Redumper/ImgBurn/
        // ImgBurn-build single ISOs). unattended=true skips the
        // confirmation dialog and proceeds with overwriting, matching
        // PreflightService's own unattended behavior.
        public static bool CheckDuplicateOrConfirm(string targetDir, string baseName, bool unattended, Action<string>? log = null)
        {
            var folderPath = Path.Combine(targetDir, baseName);
            var isoPath = Path.Combine(targetDir, $"{baseName}.iso");
            bool exists = Directory.Exists(folderPath) || File.Exists(isoPath);
            if (!exists) return true;

            var existingPath = Directory.Exists(folderPath) ? folderPath : isoPath;

            if (unattended)
            {
                log?.Invoke($"[DUPLICATE] Output already exists at {existingPath} -- unattended, proceeding to overwrite.");
            }
            else
            {
                var choice = System.Windows.MessageBox.Show(
                    $"An output already exists at:\n{existingPath}\n\nDo you wish to overwrite and re-rip this disc?",
                    "Duplicate Disc Detected", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

                if (choice != System.Windows.MessageBoxResult.Yes)
                {
                    log?.Invoke($"[DUPLICATE] Cancelled by user -- existing output at {existingPath} left untouched.");
                    return false;
                }
                log?.Invoke($"[DUPLICATE] Overwriting existing output at {existingPath} per user confirmation.");
            }

            try
            {
                if (Directory.Exists(folderPath)) Directory.Delete(folderPath, recursive: true);
                if (File.Exists(isoPath)) File.Delete(isoPath);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARNING] Couldn't fully clear the old output before re-ripping: {ex.Message}");
            }
            return true;
        }
    }
}