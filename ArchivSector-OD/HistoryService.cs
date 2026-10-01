using System.IO;
using System.Linq;
using System.Text;

namespace ArchivSector_OD
{
    // Ported from the Python app's append_history_record() -- same
    // CSV columns, so a file produced by either version stays
    // consistent and opens cleanly in Excel. Sha1/Md5/RedumpMatch are
    // now filled in for real (previously always written blank) --
    // populated by RunRedumperDump in DriveBayCard.razor after a
    // successful Redumper dump, via VerificationService. AACS Version
    // stays blank -- that's disc-metadata extraction that isn't built.
    public class HistoryEntry
    {
        public string Timestamp = "";
        public string DriveLetter = "";
        public string DriveModel = "";
        public string Title = "";
        public string System = "";
        public string OutputPath = "";
        public string Capacity = "";
        public string Sha1 = "";
        public string Md5 = "";
        public string RedumpMatch = "";
    }

    public static class HistoryService
    {
        private static string HistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "history.csv");

        public static void Append(HistoryEntry entry)
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryPath)!;
                Directory.CreateDirectory(dir);
                bool exists = File.Exists(HistoryPath);

                using var writer = new StreamWriter(HistoryPath, append: true, Encoding.UTF8);
                if (!exists)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        "Timestamp", "Drive Letter", "Drive Model", "Title",
                        "System", "Output Path", "Capacity",
                        "SHA1 Hash", "MD5 Hash", "AACS Version", "Redump Match",
                    }.Select(CsvField)));
                }

                writer.WriteLine(string.Join(",", new[]
                {
                    entry.Timestamp, entry.DriveLetter, entry.DriveModel, entry.Title,
                    entry.System, entry.OutputPath, entry.Capacity,
                    entry.Sha1, entry.Md5, "", entry.RedumpMatch,
                }.Select(CsvField)));
            }
            catch
            {
                // Best-effort -- a missed history entry should never
                // interrupt or fail a rip.
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