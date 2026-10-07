using System.IO;
using System.Text.RegularExpressions;

namespace ArchivSector_OD
{
    // Ported from the Python app's read_full_disc_info_worker --
    // classification trimmed to what's needed here. Marker checks and
    // their order match the Python app: game-specific markers are
    // checked before BDMV/VIDEO_TS, since some game discs would
    // otherwise be misclassified as a movie disc. $SystemUpdate is an
    // addition beyond the Python app's original checks -- Xbox 360
    // discs present a decoy DVD-Video layer (VIDEO_TS/AUDIO_TS) to any
    // unmodified PC drive as copy protection, so default.xex alone
    // (as the Python app checks) misses every Xbox 360 disc read on a
    // normal drive. $SystemUpdate is visible to a normal drive and
    // present on essentially every retail Xbox 360 disc.
    //
    // DetectGameSystem names the console (PlayStation 1/2/3/4, Xbox One, Original
    // Xbox, Xbox 360) for sorting game dumps into per-system folders.
    //
    // ClassifyCapacity ports the Python app's size-ladder for
    // labeling a disc's format from its real reported capacity (not
    // the rounded marketing number) -- same boundaries, same
    // fallback-to-smallest-fitting-tier logic.
    public enum DiscCategory { Unknown, BluRay, Dvd, Game, Audio }

    public static class DiscDetectionService
    {
        public static DiscCategory DetectDiscCategory(string driveLetter)
        {
            var root = $"{driveLetter.TrimEnd(':', '\\')}:\\";
            if (!Directory.Exists(root)) return DiscCategory.Unknown;

            try
            {
                if (File.Exists(Path.Combine(root, "PS3_GAME", "PARAM.SFO"))) return DiscCategory.Game;
                if (File.Exists(Path.Combine(root, "default.xbe"))) return DiscCategory.Game;
                if (File.Exists(Path.Combine(root, "default.xex"))) return DiscCategory.Game;
                if (File.Exists(Path.Combine(root, "SYSTEM.CNF"))) return DiscCategory.Game;
                if (Directory.Exists(Path.Combine(root, "$SystemUpdate"))) return DiscCategory.Game;
                if (IsPs4Disc(root)) return DiscCategory.Game;
                if (IsXboxOneDisc(root)) return DiscCategory.Game;

                if (Directory.Exists(Path.Combine(root, "BDMV"))) return DiscCategory.BluRay;
                if (IsOriginalXboxVideoPartition(root)) return DiscCategory.Game;
                if (Directory.Exists(Path.Combine(root, "VIDEO_TS"))) return DiscCategory.Dvd;
                if (File.Exists(Path.Combine(root, "Track01.cda"))) return DiscCategory.Audio;
            }
            catch
            {
                return DiscCategory.Unknown;
            }

            return DiscCategory.Unknown;
        }

        // Which console a game disc is for, from the same marker files
        // the Python app's read_full_disc_info_worker checked, in the
        // same order (plus $SystemUpdate for Xbox 360, see above).
        // Returns null for game discs it can't identify -- Nintendo
        // discs, for example, have no files Windows can see -- and for
        // every non-game disc.
        public static string? DetectGameSystem(string discRoot)
        {
            try
            {
                if (IsPs4Disc(discRoot)) return "PlayStation 4";
                if (IsXboxOneDisc(discRoot)) return "Xbox One";
                // Only the pressing-timestamp label is specific enough to
                // name the console; a tiny VIDEO_TS alone could also be an
                // Xbox 360 disc, so that case is left for Redumper's log.
                if (HasXboxPressingLabel(discRoot)) return "Original Xbox";
                if (File.Exists(Path.Combine(discRoot, "PS3_GAME", "PARAM.SFO"))) return "PlayStation 3";
                if (File.Exists(Path.Combine(discRoot, "default.xbe"))) return "Original Xbox";
                if (File.Exists(Path.Combine(discRoot, "default.xex")) ||
                    Directory.Exists(Path.Combine(discRoot, "$SystemUpdate"))) return "Xbox 360";

                // PS1 and PS2 both boot from SYSTEM.CNF. "BOOT2 =" is the
                // PS2-only line (PS1 uses plain "BOOT ="), which is how the
                // Python app told them apart too.
                var cnfPath = Path.Combine(discRoot, "SYSTEM.CNF");
                if (File.Exists(cnfPath))
                {
                    var cnf = File.ReadAllText(cnfPath);
                    return cnf.ToUpperInvariant().Contains("BOOT2") ? "PlayStation 2" : "PlayStation 1";
                }
            }
            catch
            {
                // Unreadable disc or file -- treat as unidentified.
            }
            return null;
        }

        // PS4 game discs are Blu-rays with a "PS4" folder (system
        // update) next to an "app" folder (the game, in a subfolder
        // named after its serial, e.g. app\CUSA36842) and a "bd" folder.
        // They have no BDMV folder, so without this check they were
        // classified as Unknown and every rip button stayed disabled.
        public static bool IsPs4Disc(string root)
        {
            try
            {
                return Directory.Exists(Path.Combine(root, "PS4"))
                    && (Directory.Exists(Path.Combine(root, "app")) || Directory.Exists(Path.Combine(root, "bd")));
            }
            catch
            {
                return false;
            }
        }

        // Xbox One game discs are Blu-rays with an "MSXC" folder holding
        // the game package (e.g. MSXC\RED2_1.0.0.1_x64__....GameDisc.1)
        // and its Metadata, usually next to a "Licenses" folder. Unlike
        // Original Xbox / Xbox 360 discs, Windows can see these files.
        public static bool IsXboxOneDisc(string root)
        {
            try
            {
                return Directory.Exists(Path.Combine(root, "MSXC"));
            }
            catch
            {
                return false;
            }
        }

        // On a PC drive an Original Xbox disc only shows its small video
        // partition: a VIDEO_TS folder with a "put this disc in your
        // Xbox" clip, so it looked like a DVD movie. Two tells:
        //  - the volume label is a pressing timestamp, month + 11 digits,
        //    e.g. "SEP13011042072" (Sep 13 '01, 10:42:07), which real DVD
        //    movies don't use;
        //  - VIDEO_TS is tiny (that one clip) next to a real movie's GBs.
        // Either one is enough, as long as there's no AUDIO_TS-only or
        // BDMV disc involved (checked by the caller's order).
        private static readonly Regex XboxPressingLabel =
            new(@"^(JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)\d{11}$", RegexOptions.IgnoreCase);
        private const long XboxVideoPartitionMaxBytes = 50L * 1024 * 1024; // 50 MB

        public static bool HasXboxPressingLabel(string root)
        {
            try
            {
                if (!Directory.Exists(Path.Combine(root, "VIDEO_TS"))) return false;
                return XboxPressingLabel.IsMatch(new DriveInfo(root).VolumeLabel?.Trim() ?? "");
            }
            catch
            {
                return false;
            }
        }

        public static bool IsOriginalXboxVideoPartition(string root)
        {
            try
            {
                var videoTs = Path.Combine(root, "VIDEO_TS");
                if (!Directory.Exists(videoTs)) return false;

                string label = "";
                try { label = new DriveInfo(root).VolumeLabel?.Trim() ?? ""; }
                catch { /* no label available -- rely on the size check */ }
                if (XboxPressingLabel.IsMatch(label)) return true;

                long total = 0;
                foreach (var f in Directory.EnumerateFiles(videoTs))
                {
                    total += new FileInfo(f).Length;
                    if (total > XboxVideoPartitionMaxBytes) return false;
                }
                return total > 0;
            }
            catch
            {
                return false;
            }
        }

        public static string ClassifyCapacity(long totalBytes)
        {
            var gb = totalBytes / Math.Pow(1024, 3);
            if (gb > 119.0) return "BD-128 Quad Layer";
            if (gb > 66.0) return "BD-128 Quad Layer";
            if (gb > 50.0) return "BD-100 Triple Layer";
            if (gb > 25.0) return "BD-50 Dual Layer";
            if (gb > 8.5) return "BD-25 Single Layer";
            if (gb > 4.7) return "DVD-9 Dual Layer";
            if (gb > 1.0) return "DVD-5 Single Layer";
            return "CD-ROM";
        }
    }
}