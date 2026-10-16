using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ArchivSector_OD
{
    // v3.3: catches the crashes App.xaml.cs's crash catcher can't see, and
    // writes what it finds to crash.log:
    //
    //   * Screen errors: an error inside one of the app's screens (Razor
    //     components) is reported by Blazor through its logger, not as a
    //     normal .NET crash. CrashLoggerFactory receives those and writes
    //     them to crash.log.
    //   * The screen's browser view (WebView2) crashing or being killed:
    //     MainWindow hooks its ProcessFailed event into ScreenProcessFailed.
    //   * The whole app vanishing without warning (a crash in native code
    //     such as WebView2 or a Windows disc driver, which .NET never
    //     hears about): each running copy leaves a small "session" file
    //     that's deleted when it closes normally. If one is still there at
    //     the next start and that copy isn't running, the app closed
    //     unexpectedly -- crash.log notes it, with any matching entries
    //     Windows itself recorded in Event Viewer (Application log) for
    //     that time. (Stopping the app from Visual Studio also leaves a
    //     session file behind; that shows up as "no crash found in
    //     Windows' records".)
    public static class CrashWatch
    {
        private static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchivSector-OD", "Sessions");

        private static string? sessionFile;

        public static void Start()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var leftovers = Directory.GetFiles(Folder, "session_*.txt");
                int myPid = Environment.ProcessId;
                sessionFile = Path.Combine(Folder, $"session_{myPid}.txt");
                File.WriteAllText(sessionFile, DateTime.Now.ToString("O"));

                foreach (var file in leftovers)
                {
                    if (string.Equals(file, sessionFile, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(file).Replace("session_", ""), out int pid)) continue;
                    if (IsOurProcessRunning(pid)) continue;   // another copy that's still open

                    DateTime started = DateTime.TryParse(File.ReadAllText(file), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : File.GetCreationTime(file);
                    DateTime lastSeen = File.GetLastWriteTime(file);
                    try { File.Delete(file); } catch { }

                    // Reading Windows' event log can take a moment: do it in the background.
                    _ = Task.Run(() => ReportUnexpectedExit(pid, started, lastSeen));
                }

                // Touch the session file now and then, so "last seen" is close to when it stopped.
                var timer = new System.Threading.Timer(_ =>
                {
                    try { if (sessionFile is not null && File.Exists(sessionFile)) File.SetLastWriteTime(sessionFile, DateTime.Now); } catch { }
                }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                GC.KeepAlive(timer);
                keepTimer = timer;
            }
            catch { /* crash watching must never stop the app starting */ }
        }

        private static System.Threading.Timer? keepTimer;

        // Called when the app closes normally.
        public static void Stop()
        {
            try { keepTimer?.Dispose(); } catch { }
            try { if (sessionFile is not null) File.Delete(sessionFile); } catch { }
        }

        private static bool IsOurProcessRunning(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited && p.ProcessName.Contains("ArchivSector", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void ReportUnexpectedExit(int pid, DateTime started, DateTime lastSeen)
        {
            var found = new List<string>();
            try
            {
                using var log = new EventLog("Application");
                var entries = log.Entries;
                DateTime from = started.AddMinutes(-1);
                for (int i = entries.Count - 1; i >= 0 && found.Count < 5; i--)
                {
                    EventLogEntry e;
                    try { e = entries[i]; } catch { continue; }
                    if (e.TimeGenerated < from) break;
                    if (e.EntryType != EventLogEntryType.Error && e.Source != "Windows Error Reporting") continue;
                    string msg = e.Message ?? "";
                    bool ours = msg.Contains("ArchivSector", StringComparison.OrdinalIgnoreCase)
                             || msg.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase);
                    if (!ours) continue;
                    if (msg.Length > 3000) msg = msg[..3000] + " …";
                    found.Add($"[{e.TimeGenerated:yyyy-MM-dd HH:mm:ss}] {e.Source} (event {e.InstanceId & 0xFFFF}):\n{msg}");
                }
            }
            catch (Exception ex)
            {
                found.Add($"(Couldn't read Windows' event log: {ex.Message})");
            }

            string summary =
                $"The previous session (process {pid}, started {started:yyyy-MM-dd HH:mm:ss}, last seen running {lastSeen:yyyy-MM-dd HH:mm:ss}) " +
                "closed without shutting down normally.\n" +
                (found.Count == 0
                    ? "No crash for it was found in Windows' records. If it was stopped from Visual Studio or Task Manager, that's expected.\n"
                    : "What Windows recorded:\n\n" + string.Join("\n\n", found) + "\n");
            App.WriteCrash("previous session ended unexpectedly", new UnexpectedExitException(summary));
            if (found.Count > 0) LogService.Write("App", "The previous session crashed -- details saved to " + App.CrashLogPath);
        }

        // Called by MainWindow when the screen's browser view stops.
        public static void ScreenProcessFailed(string kind, string reason, int exitCode, string? description)
        {
            App.WriteCrash("screen (WebView2) process failed", new UnexpectedExitException(
                $"WebView2 process failure: kind={kind}, reason={reason}, exit code={exitCode} (0x{exitCode:X8})" +
                (string.IsNullOrWhiteSpace(description) ? "" : $", {description}")));
        }

        private sealed class UnexpectedExitException : Exception
        {
            public UnexpectedExitException(string message) : base(message) { }
            public override string ToString() => Message;
        }
    }

    // Receives Blazor's error messages (an exception inside a screen
    // component) and writes them to crash.log. Only errors -- nothing
    // else is logged.
    public sealed class CrashLoggerFactory : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new CrashLogger(categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CrashLogger : ILogger
        {
            private readonly string category;
            public CrashLogger(string category) => this.category = category;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                string text = "";
                try { text = formatter(state, exception); } catch { }
                App.WriteCrash($"screen error ({category})", exception ?? new Exception(text));
                try { LogService.Write("App", "Screen error: " + (exception?.Message ?? text) + " -- details in " + App.CrashLogPath); } catch { }
            }
        }
    }
}
