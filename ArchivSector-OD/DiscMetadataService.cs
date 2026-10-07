using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // A PS3 PARAM.SFO starts with the "\0PSF" signature, but a PS4
        // disc's bd\param.sfo has an extra header in front of it. Find
        // the signature and return the bytes from there on, so ParseSfo
        // reads both. Returns the blob unchanged if it's already at the
        // start or isn't found at all.
        private static byte[] SkipToPsfHeader(byte[] blob)
        {
            for (int i = 0; i + 4 <= blob.Length; i++)
            {
                if (blob[i] == 0x00 && blob[i + 1] == (byte)'P' && blob[i + 2] == (byte)'S' && blob[i + 3] == (byte)'F')
                    return i == 0 ? blob : blob[i..];
            }
            return blob;
        }

        // Xbox One discs list their game in MSXC\Metadata\catalog.js, a
        // JSON file: packages[0].titles is a list of { locale, title }.
        // Picks en-US, then "default", then the first one listed. When
        // the game spans several discs (discCount > 1), adds
        // " (Disc N)" so disc 2 doesn't get the same name as disc 1.
        public static string? ExtractXboxOneTitle(string discRoot)
        {
            var catalogPath = Path.Combine(discRoot, "MSXC", "Metadata", "catalog.js");
            if (!File.Exists(catalogPath)) return null;

            try
            {
                var json = ReadTextAnyEncoding(catalogPath).Trim('\0', '﻿', ' ', '\r', '\n', '\t');
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array
                    || packages.GetArrayLength() == 0)
                    return null;
                if (!packages[0].TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array)
                    return null;

                string? enUs = null, fallback = null, first = null;
                foreach (var t in titles.EnumerateArray())
                {
                    var locale = t.TryGetProperty("locale", out var l) ? l.GetString() : null;
                    var title = t.TryGetProperty("title", out var v) ? v.GetString()?.Trim() : null;
                    if (string.IsNullOrWhiteSpace(title)) continue;

                    first ??= title;
                    if (string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase)) enUs = title;
                    else if (string.Equals(locale, "default", StringComparison.OrdinalIgnoreCase)) fallback = title;
                }

                var name = enUs ?? fallback ?? first;
                if (name is null) return null;

                if (root.TryGetProperty("discCount", out var countEl) && countEl.TryGetInt32(out var count) && count > 1
                    && root.TryGetProperty("discNumber", out var numEl) && numEl.TryGetInt32(out var number))
                    name = $"{name} (Disc {number})";

                return name;
            }
            catch
            {
                // Unreadable or unexpected JSON -- caller keeps the volume label.
                return null;
            }
        }

        // catalog.js on Xbox One discs is stored as UTF-16 (two bytes
        // per character), sometimes without the marker bytes that let
        // File.ReadAllText notice. Reads it as UTF-16 when it has that
        // marker or looks like it (every other byte zero), else as
        // UTF-8.
        private static string ReadTextAnyEncoding(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0 && bytes[0] != 0)
                return System.Text.Encoding.Unicode.GetString(bytes);
            if (bytes.Length >= 4 && bytes[0] == 0 && bytes[2] == 0 && bytes[1] != 0)
                return System.Text.Encoding.BigEndianUnicode.GetString(bytes);
            return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }

        public class RealTitleResult
        {
            public string? Title;
            public string? AacsVersion;
            // Game discs only: the console (e.g. "PlayStation 2") and the
            // game's serial number (e.g. "SLUS-20062"), when the disc's
            // own files say what they are.
            public string? GameSystem;
            public string? Serial;
        }

        // Combines the above into one call matching each disc
        // Category's real source of truth: BDMV's own bdmt_eng.xml for
        // Blu-ray, PS3_GAME\PARAM.SFO's TITLE key for PS3 games. DVD
        // and other game platforms have no title source parsed here yet
        // -- falls back to null, letting the caller keep the volume
        // label it already has.
        //
        // For game discs it also reports the console and serial number,
        // ported from read_full_disc_info_worker: PS3 serials come from
        // PARAM.SFO's TITLE_ID ("BLUS30001" -> "BLUS-30001"), PS1/PS2
        // serials from the boot line in SYSTEM.CNF ("SLUS_200.62" ->
        // "SLUS-20062"). Xbox discs have no readable serial, so theirs
        // stays null (the Python app made one up from the volume label;
        // leaving it blank is more honest).
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
                result.GameSystem = DiscDetectionService.DetectGameSystem(discRoot);

                // PS3 keeps PARAM.SFO in PS3_GAME; PS4 discs keep one in
                // "bd" (same file format, also with TITLE and TITLE_ID).
                var sfoPath = Path.Combine(discRoot, "PS3_GAME", "PARAM.SFO");
                if (!File.Exists(sfoPath))
                    sfoPath = Path.Combine(discRoot, "bd", "param.sfo");
                if (File.Exists(sfoPath))
                {
                    try
                    {
                        var blob = SkipToPsfHeader(File.ReadAllBytes(sfoPath));
                        var sfo = ParseSfo(blob);
                        if (sfo.TryGetValue("TITLE", out var titleVal) && !string.IsNullOrWhiteSpace(titleVal.Text))
                            result.Title = titleVal.Text;
                        if (sfo.TryGetValue("TITLE_ID", out var idVal) && !string.IsNullOrWhiteSpace(idVal.Text))
                        {
                            var id = idVal.Text.Trim();
                            var m = Regex.Match(id, @"^([A-Z]{4})(\d{5})$");
                            result.Serial = m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}" : id;
                        }
                    }
                    catch
                    {
                        // Best-effort -- leave Title null, caller keeps the volume label.
                    }
                }

                if (result.GameSystem == "Xbox One")
                    result.Title = ExtractXboxOneTitle(discRoot);

                // PS4 fallback: the game sits in app\<serial>, e.g.
                // app\CUSA36842 -> "CUSA-36842".
                var appDir = Path.Combine(discRoot, "app");
                if (result.Serial is null && result.GameSystem == "PlayStation 4" && Directory.Exists(appDir))
                {
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(appDir))
                        {
                            var m = Regex.Match(Path.GetFileName(dir), @"^([A-Z]{4})(\d{5})$", RegexOptions.IgnoreCase);
                            if (!m.Success) continue;
                            result.Serial = $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}";
                            break;
                        }
                    }
                    catch
                    {
                        // Best-effort -- leave Serial null.
                    }
                }

                var cnfPath = Path.Combine(discRoot, "SYSTEM.CNF");
                if (result.Serial is null && File.Exists(cnfPath))
                {
                    try
                    {
                        var cnf = File.ReadAllText(cnfPath).ToUpperInvariant();
                        var m = Regex.Match(cnf, @"([A-Z]{4})[-_](\d{3})\.?(\d{2})");
                        if (m.Success)
                            result.Serial = $"{m.Groups[1].Value}-{m.Groups[2].Value}{m.Groups[3].Value}";
                    }
                    catch
                    {
                        // Best-effort -- leave Serial null.
                    }
                }
            }

            return result;
        }
    }
}