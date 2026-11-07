using System.IO;
using System.Linq;
using System.Text;

namespace ArchivSector_OD
{
    // Ported from the Python app's append_history_record() -- same CSV
    // columns in the same order ("Timestamp", "Drive Letter",
    // "Drive Model", "Title", "System", "Serial ID", "ISO File Path",
    // "Capacity", "SHA1 Hash", "MD5 Hash", "AACS Version",
    // "Redump Match"), so a file produced by either version stays
    // consistent and opens cleanly in Excel.
    //
    // History files written by v2.0 lacked the "Serial ID" column and
    // called the path column "Output Path". UpgradeOldHeader() converts
    // such a file in place the first time a new record is written:
    // it inserts an empty Serial ID value into every existing row and
    // renames the header, so old and new rows stay lined up.
    public class HistoryEntry
    {
        public string Timestamp = "";
        public string DriveLetter = "";
        public string DriveModel = "";
        public string Title = "";
        public string System = "";
        public string Serial = "";
        public string OutputPath = "";
        public string Capacity = "";
        public string Sha1 = "";
        public string Md5 = "";
        public string AacsVersion = "";
        public string RedumpMatch = "";
    }

    public static class HistoryService
    {
        private static readonly string[] Header =
        {
            "Timestamp", "Drive Letter", "Drive Model", "Title",
            "System", "Serial ID", "ISO File Path", "Capacity",
            "SHA1 Hash", "MD5 Hash", "AACS Version", "Redump Match",
        };

        private static string HistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "history.csv");

        public static void Append(HistoryEntry entry)
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryPath)!;
                Directory.CreateDirectory(dir);

                UpgradeOldHeader();
                bool exists = File.Exists(HistoryPath);

                using var writer = new StreamWriter(HistoryPath, append: true, Encoding.UTF8);
                if (!exists)
                    writer.WriteLine(string.Join(",", Header.Select(CsvField)));

                writer.WriteLine(string.Join(",", new[]
                {
                    entry.Timestamp, entry.DriveLetter, TidyDriveModel(entry.DriveModel), entry.Title,
                    entry.System, entry.Serial, entry.OutputPath, entry.Capacity,
                    entry.Sha1, entry.Md5, entry.AacsVersion, entry.RedumpMatch,
                }.Select(CsvField)));
            }
            catch
            {
                // Best-effort -- a missed history entry should never
                // interrupt or fail a rip.
            }
        }

        /// <summary>
        /// A drive's name as the rest of the app shows it (beta 5): the
        /// drive's own padding squeezed out, and a lone letter some drives
        /// add after the model (ASUS "DRW-24F1ST   d", a hardware revision)
        /// left off. "ASUS DRW-24F1ST   d" becomes "ASUS DRW-24F1ST".
        /// The same rule as the Burn tab's drive names (BurnerDrive.Name).
        /// </summary>
        public static string TidyDriveModel(string model)
        {
            var parts = (model ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count > 2 && parts[^1].Length == 1) parts.RemoveAt(parts.Count - 1);
            return string.Join(' ', parts);
        }

        // Converts a v2.0-format history file (no "Serial ID" column)
        // to the current layout. Does nothing if the file is missing,
        // already current, or not recognizably a v2.0 file.
        private static void UpgradeOldHeader()
        {
            if (!File.Exists(HistoryPath)) return;

            var lines = File.ReadAllLines(HistoryPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (lines.Count == 0) return;

            var header = ParseCsvLine(lines[0]).ToList();
            if (header.Contains("Serial ID")) return;

            int systemIdx = header.IndexOf("System");
            if (systemIdx < 0) return; // not a file we recognize -- leave it alone

            // Keep a copy of the original, just in case.
            try { File.Copy(HistoryPath, HistoryPath + ".v2.0-backup", overwrite: false); }
            catch { /* backup already exists or couldn't be made -- carry on */ }

            var output = new List<string>();
            header.Insert(systemIdx + 1, "Serial ID");
            int pathIdx = header.IndexOf("Output Path");
            if (pathIdx >= 0) header[pathIdx] = "ISO File Path";
            output.Add(string.Join(",", header.Select(CsvField)));

            foreach (var line in lines.Skip(1))
            {
                var row = ParseCsvLine(line).ToList();
                if (row.Count > systemIdx) row.Insert(systemIdx + 1, "");
                output.Add(string.Join(",", row.Select(CsvField)));
            }

            File.WriteAllLines(HistoryPath, output, Encoding.UTF8);
        }

        // Used when a finished game dump is renamed from its drive bay:
        // points the matching record(s) at the new path and title.
        public static void UpdateRenamed(string oldPath, string newPath, string newTitle)
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;

                var lines = File.ReadAllLines(HistoryPath)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
                if (lines.Count < 2) return;

                var header = ParseCsvLine(lines[0]).ToList();
                int pathIdx = header.IndexOf("ISO File Path");
                if (pathIdx < 0) pathIdx = header.IndexOf("Output Path");
                int titleIdx = header.IndexOf("Title");
                if (pathIdx < 0) return;

                bool changed = false;
                for (int i = 1; i < lines.Count; i++)
                {
                    var row = ParseCsvLine(lines[i]);
                    if (pathIdx >= row.Length) continue;
                    if (!string.Equals(row[pathIdx], oldPath, StringComparison.OrdinalIgnoreCase)) continue;

                    row[pathIdx] = newPath;
                    if (titleIdx >= 0 && titleIdx < row.Length) row[titleIdx] = newTitle;
                    lines[i] = string.Join(",", row.Select(CsvField));
                    changed = true;
                }

                if (changed)
                    File.WriteAllLines(HistoryPath, lines, Encoding.UTF8);
            }
            catch
            {
                // Best-effort -- the files were still renamed; only the
                // history record keeps the old path.
            }
        }

        public static List<string[]> ReadAll()
        {
            var rows = new List<string[]>();
            try
            {
                if (!File.Exists(HistoryPath)) return rows;
                foreach (var line in File.ReadAllLines(HistoryPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    rows.Add(ParseCsvLine(line));
                }
            }
            catch
            {
                // Best-effort -- an unreadable history file shouldn't
                // crash the viewer, it just shows nothing.
            }
            return rows;
        }

        private static string CsvField(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private static string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else if (ch == '"') inQuotes = false;
                    else current.Append(ch);
                }
                else
                {
                    if (ch == '"') inQuotes = true;
                    else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
                    else current.Append(ch);
                }
            }
            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
