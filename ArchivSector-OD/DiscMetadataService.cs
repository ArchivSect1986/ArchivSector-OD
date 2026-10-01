using System.IO;
using System.Xml.Linq;

namespace ArchivSector_OD
{
    // Ported from the Python app's extract_bdmt_movie_title(),
    // extract_aacs_mkb_version(), and parse_sfo(). All three are
    // real-format parsers, not naive byte-searching: ParseSfo in
    // particular follows the PSF binary format's actual key-table /
    // data-table structure, since PARAM.SFO also has a TITLE_ID key
    // whose name contains "TITLE" as a substring -- a literal text
    // search for "TITLE" would false-match that, and even a genuine
    // match tells you nothing about where the VALUE actually lives,
    // since keys and values are stored in entirely separate regions
    // of the file.
    public static class DiscMetadataService
    {
        // BDMV discs store their real title in this XML file, in an
        // element whose tag contains "discTitle" (namespaced, so a
        // substring check rather than an exact tag match, matching
        // the Python app's own approach).
        public static string? ExtractBdmtMovieTitle(string discRoot)
        {
            var metaPath = Path.Combine(discRoot, "BDMV", "META", "DL", "bdmt_eng.xml");
            if (!File.Exists(metaPath)) return null;

            try
            {
                var doc = XDocument.Load(metaPath);
                foreach (var elem in doc.Descendants())
                {
                    if (elem.Name.LocalName.Contains("discTitle") && !string.IsNullOrWhiteSpace(elem.Value))
                    {
                        var clean = elem.Value.Trim();
                        if (clean.Length > 0) return clean;
                    }
                }
            }
            catch
            {
                // Malformed/unexpected XML -- fall through to null,
                // caller falls back to the volume label.
            }
            return null;
        }

        // AACS\MKB_RO.inf's Media Key Block version sits as a 4-byte
        // big-endian integer at byte offset 8. A value outside 0-200
        // is treated as implausible (probably not really a version
        // number) and reported as just "AACS Protected" instead of a
        // specific, likely-wrong version.
        public static string? ExtractAacsMkbVersion(string discRoot)
        {
            var mkbPath = Path.Combine(discRoot, "AACS", "MKB_RO.inf");
            if (!File.Exists(mkbPath)) return null;

            try
            {
                using var fs = File.OpenRead(mkbPath);
                if (fs.Length < 12) return "AACS";

                fs.Seek(8, SeekOrigin.Begin);
                var header = new byte[4];
                int read = fs.Read(header, 0, 4);
                if (read == 4)
                {
                    uint mkbVer = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
                    if (mkbVer > 0 && mkbVer < 200)
                        return $"AACS v{mkbVer}";
                }
                return "AACS Protected";
            }
            catch
            {
                return "AACS";
            }
        }

        public class SfoValue
        {
            public string? Text;
            public int? Number;
            public override string ToString() => Text ?? Number?.ToString() ?? "";
        }

        // PSF binary format: a fixed 20-byte header (magic, version,
        // key-table offset, data-table offset, entry count), followed
        // by a 16-byte index entry per key (key_offset, data_format,
        // data_len, data_max_len, data_offset), with the actual key
        // names and values living in two SEPARATE regions the index
        // points into.
        public static Dictionary<string, SfoValue> ParseSfo(byte[] blob)
        {
            var result = new Dictionary<string, SfoValue>();
            if (blob.Length < 20 || blob[0] != 0x00 || blob[1] != (byte)'P' || blob[2] != (byte)'S' || blob[3] != (byte)'F')
                return result;

            try
            {
                uint keyTableStart = BitConverter.ToUInt32(blob, 8);
                uint dataTableStart = BitConverter.ToUInt32(blob, 12);
                uint entryCount = BitConverter.ToUInt32(blob, 16);

                for (uint i = 0; i < entryCount; i++)
                {
                    long entryOffset = 20 + i * 16;
                    if (entryOffset + 16 > blob.Length) break;

                    ushort keyOffset = BitConverter.ToUInt16(blob, (int)entryOffset);
                    ushort dataFmt = BitConverter.ToUInt16(blob, (int)entryOffset + 2);
                    uint dataLen = BitConverter.ToUInt32(blob, (int)entryOffset + 4);
                    // dataMaxLen at +8 is unused here, matching the Python port.
                    uint dataOffset = BitConverter.ToUInt32(blob, (int)entryOffset + 12);

                    long keyStart = keyTableStart + keyOffset;
                    if (keyStart >= blob.Length) continue;
                    long keyEnd = keyStart;
                    while (keyEnd < blob.Length && blob[keyEnd] != 0) keyEnd++;
                    var keyName = System.Text.Encoding.UTF8.GetString(blob, (int)keyStart, (int)(keyEnd - keyStart));

                    long valStart = dataTableStart + dataOffset;
                    if (valStart >= blob.Length) continue;
                    int valLen = (int)Math.Min(dataLen, blob.Length - valStart);
                    var valBytes = new byte[valLen];
                    Array.Copy(blob, valStart, valBytes, 0, valLen);

                    if (string.IsNullOrEmpty(keyName)) continue;

                    if (dataFmt == 0x0404) // int32
                    {
                        var padded = new byte[4];
                        Array.Copy(valBytes, padded, Math.Min(4, valBytes.Length));
                        result[keyName] = new SfoValue { Number = BitConverter.ToInt32(padded, 0) };
                    }
                    else // UTF-8 string, possibly null-terminated
                    {
                        int nullIdx = Array.IndexOf(valBytes, (byte)0);
                        var strBytes = nullIdx >= 0 ? valBytes[..nullIdx] : valBytes;
                        result[keyName] = new SfoValue { Text = System.Text.Encoding.UTF8.GetString(strBytes).Trim() };
                    }
                }
            }
            catch
            {
                // Truncated/malformed SFO -- return whatever was
                // successfully parsed before the failure.
            }

            return result;
        }

        public class RealTitleResult
        {
            public string? Title;
            public string? AacsVersion;
        }

        // Combines the above into one call matching each disc
        // Category's real source of truth: BDMV's own bdmt_eng.xml for
        // Blu-ray, PS3_GAME\PARAM.SFO's TITLE key for PS3 games. DVD
        // and other game platforms have no equivalent parsed here yet
        // -- falls back to null, letting the caller keep the volume
        // label it already has.
        public static RealTitleResult GetRealTitle(string discRoot, DiscCategory category)
        {
            var result = new RealTitleResult();

            if (category == DiscCategory.BluRay)
            {
                result.Title = ExtractBdmtMovieTitle(discRoot);
                result.AacsVersion = ExtractAacsMkbVersion(discRoot);
            }
            else if (category == DiscCategory.Game)
            {
                var sfoPath = Path.Combine(discRoot, "PS3_GAME", "PARAM.SFO");
                if (File.Exists(sfoPath))
                {
                    try
                    {
                        var blob = File.ReadAllBytes(sfoPath);
                        var sfo = ParseSfo(blob);
                        if (sfo.TryGetValue("TITLE", out var titleVal) && !string.IsNullOrWhiteSpace(titleVal.Text))
                            result.Title = titleVal.Text;
                    }
                    catch
                    {
                        // Best-effort -- leave Title null, caller keeps the volume label.
                    }
                }
            }

            return result;
        }
    }
}