using System.IO;
using System.Linq;

namespace ArchivSector_OD
{
    // Ported from list_imported_redump_dats() / the Import DAT File
    // button's logic -- copies a chosen DAT/XML file into the app's
    // own folder and lists what's there. Redump has no query API
    // (confirmed on Redump's own forum), so real verification works
    // against DAT files the user downloads themselves and imports
    // here. This only handles storage/listing; actually parsing a
    // DAT file's entries and matching a rip's checksum against them
    // is separate, not-yet-built verification logic.
    public static class RedumpDatService
    {
        private static string DatDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "redump_dats");

        public static List<string> ListImportedDats()
        {
            try
            {
                if (!Directory.Exists(DatDir)) return new List<string>();
                return Directory.GetFiles(DatDir)
                    .Where(f => f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetFileName(f)!)
                    .OrderBy(f => f)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static bool ImportDat(string sourcePath)
        {
            try
            {
                Directory.CreateDirectory(DatDir);
                var dest = Path.Combine(DatDir, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, dest, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}