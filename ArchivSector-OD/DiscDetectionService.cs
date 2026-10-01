using System.IO;

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

                if (Directory.Exists(Path.Combine(root, "BDMV"))) return DiscCategory.BluRay;
                if (Directory.Exists(Path.Combine(root, "VIDEO_TS"))) return DiscCategory.Dvd;
                if (File.Exists(Path.Combine(root, "Track01.cda"))) return DiscCategory.Audio;
            }
            catch
            {
                return DiscCategory.Unknown;
            }

            return DiscCategory.Unknown;
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