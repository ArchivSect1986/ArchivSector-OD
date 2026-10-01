using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace ArchivSector_OD
{
    // Ported from the Python app's compute_iso_checksums(),
    // parse_redump_dat(), and verify_against_redump(). Redump has no
    // query-by-hash API (confirmed on Redump's own forum) -- the only
    // supported way to check a dump is against a DAT file the user
    // downloads from Redump themselves and imports (RedumpDatService
    // handles storing those). This only reads the standard Logiqx XML
    // format Redump actually publishes:
    // <datafile><game name="..."><rom name="..." size="..." sha1="..." /></game></datafile>
    //
    // Deliberately only wired into Redumper's raw-sector .iso output
    // (Game Disc and DVD Raw Sector ISO), not MakeMKV's Full Backup or
    // Movie-Only paths: Redump's hashes are of the raw disc image, and
    // MakeMKV's decrypted/re-muxed output is a structurally different
    // file that could never match one, regardless of whether the dump
    // is good -- checking it there would only ever report a false
    // "no match" with no real information in it.
    public static class VerificationService
    {
        public static (string? sha1, string? md5) ComputeChecksums(string filePath)
        {
            if (!File.Exists(filePath)) return (null, null);

            using var sha1Alg = SHA1.Create();
            using var md5Alg = MD5.Create();
            using (var stream = File.OpenRead(filePath))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha1Alg.TransformBlock(buffer, 0, read, null, 0);
                    md5Alg.TransformBlock(buffer, 0, read, null, 0);
                }
                sha1Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                md5Alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }

            var sha1Hex = Convert.ToHexString(sha1Alg.Hash!).ToLowerInvariant();
            var md5Hex = Convert.ToHexString(md5Alg.Hash!).ToLowerInvariant();

            try
            {
                var baseName = Path.GetFileName(filePath);
                var dirName = Path.GetDirectoryName(filePath) ?? "";
                File.WriteAllText(Path.Combine(dirName, $"{baseName}.sha1"), $"{sha1Hex} *{baseName}\n");
                File.WriteAllText(Path.Combine(dirName, $"{baseName}.md5"), $"{md5Hex} *{baseName}\n");
            }
            catch
            {
                // Sidecar files are a convenience, not essential --
                // the hashes themselves are still returned either way.
            }

            return (sha1Hex, md5Hex);
        }

        public class DatEntry
        {
            public string Name = "";
            public string Size = "";
            public string Crc = "";
            public string Md5 = "";
            public string Sha1 = "";
            public string SourceDat = "";
        }

        public static List<DatEntry> ParseDat(string path)
        {
            var entries = new List<DatEntry>();
            try
            {
                var doc = XDocument.Load(path);
                foreach (var game in doc.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                {
                    var name = game.Attribute("name")?.Value ?? "";
                    foreach (var rom in game.Elements("rom"))
                    {
                        entries.Add(new DatEntry
                        {
                            Name = name,
                            Size = rom.Attribute("size")?.Value ?? "",
                            Crc = (rom.Attribute("crc")?.Value ?? "").ToLowerInvariant(),
                            Md5 = (rom.Attribute("md5")?.Value ?? "").ToLowerInvariant(),
                            Sha1 = (rom.Attribute("sha1")?.Value ?? "").ToLowerInvariant(),
                        });
                    }
                }
            }
            catch
            {
                // Malformed/unparseable DAT -- treat as zero entries
                // rather than crash the caller.
            }
            return entries;
        }

        private static string DatDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "redump_dats");

        // Returns a match with SourceDat filled in, or null -- null
        // means either no match or no DAT files imported yet, which
        // the caller should distinguish and report differently (a
        // missing DAT is not the same thing as a bad dump).
        public static DatEntry? VerifyAgainstRedump(string sha1Hex)
        {
            if (string.IsNullOrWhiteSpace(sha1Hex) || !Directory.Exists(DatDir)) return null;
            var target = sha1Hex.ToLowerInvariant().Trim();

            foreach (var file in Directory.GetFiles(DatDir))
            {
                if (!file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                 && !file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var entry in ParseDat(file))
                {
                    if (!string.IsNullOrEmpty(entry.Sha1) && entry.Sha1 == target)
                    {
                        entry.SourceDat = Path.GetFileName(file);
                        return entry;
                    }
                }
            }
            return null;
        }

        public static bool HasAnyDatImported()
        {
            if (!Directory.Exists(DatDir)) return false;
            return Directory.GetFiles(DatDir).Any(f =>
                f.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        }
    }
}