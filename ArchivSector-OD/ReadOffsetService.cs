using System;
using System.Threading.Tasks;
using ArchivSector_OD.Burning;

namespace ArchivSector_OD
{
    // Read offsets for music CDs (v4.0 beta 4), per drive model, set without
    // anyone typing a number:
    //  1. one already saved for the model (set in Read disc, looked up or
    //     learned earlier) is used;
    //  2. otherwise AccurateRip's drive list is asked once, and what it
    //     lists for the model is saved;
    //  3. a drive the list doesn't have learns its offset from an AccurateRip
    //     check of a CD it has just read (AccurateRipPanel, using LastRead).
    // Kept in the settings (AppConfig.ReadOffsets).
    public static class ReadOffsetService
    {
        /// <summary>The CD Read disc read last: its .cue, the drive model and the offset it was read with.</summary>
        public static (string Cue, string Model, int Applied)? LastRead;

        public static int? Saved(string model)
        {
            if (model.Length == 0) return null;
            try { return ConfigService.Load().ReadOffsets.TryGetValue(model, out var off) ? off : null; }
            catch { return null; }
        }

        public static void Save(string model, int offset)
        {
            if (model.Length == 0) return;
            try
            {
                var cfg = ConfigService.Load();
                cfg.ReadOffsets[model] = offset;
                ConfigService.Save(cfg);
            }
            catch { }
        }

        /// <summary>The offset to read with and where it came from (for the screen).</summary>
        public static async Task<(int Offset, string Note)> GetAsync(string model, Action<string>? log = null)
        {
            if (model.Length == 0) return (0, "");
            var saved = Saved(model);
            if (saved is int s) return (s, "set for this drive");
            var listed = await AccurateRip.DriveOffsetAsync(model);
            if (listed is int l)
            {
                Save(model, l);
                log?.Invoke($"Read offset: AccurateRip lists {l:+0;-0} samples for the {model}; saved for this drive");
                return (l, "from AccurateRip's drive list");
            }
            return (0, "not in AccurateRip's drive list: read a music CD and check it with AccurateRip, and the offset is learned");
        }
    }
}
