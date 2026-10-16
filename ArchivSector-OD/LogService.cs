using System.IO;
using System.Text;

namespace ArchivSector_OD
{
    // Saved telemetry: everything a drive bay shows in "Process Telemetry"
    // (and TargetFill's results) is also written to a log file, so it can be
    // read after the app is closed. One file per day in
    // %AppData%\ArchivSector-OD\Logs, each line tagged with where it came
    // from ("E:", "TargetFill"). Logs older than 30 days are removed
    // automatically. Writing never throws -- a full disk or a locked file
    // just means that line isn't saved.
    public static class LogService
    {
        private static readonly object Gate = new();
        private static bool cleaned;
        private const int KeepDays = 30;

        public static string LogFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchivSector-OD", "Logs");

        public static string TodayLogPath => Path.Combine(LogFolder, $"ArchivSector-OD_{DateTime.Now:yyyy-MM-dd}.log");

        public static void Write(string source, string text)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] [{source}] {text}{Environment.NewLine}";
                lock (Gate)
                {
                    Directory.CreateDirectory(LogFolder);
                    if (!cleaned) { cleaned = true; RemoveOldLogs(); }
                    File.AppendAllText(TodayLogPath, line, Encoding.UTF8);
                }
            }
            catch { /* logging must never get in the way */ }
        }

        private static void RemoveOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-KeepDays);
                foreach (var f in Directory.GetFiles(LogFolder, "ArchivSector-OD_*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        // Opens today's log in the default text editor (or the Logs folder
        // if nothing has been logged today yet).
        public static void OpenToday()
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                var target = File.Exists(TodayLogPath) ? TodayLogPath : LogFolder;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = target, UseShellExecute = true });
            }
            catch { }
        }
    }
}
