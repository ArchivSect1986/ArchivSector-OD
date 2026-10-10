using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ArchivSector_OD
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    // Crash catcher: any error nothing else handled is written to
    // %AppData%\ArchivSector-OD\crash.log (newest at the bottom) with the
    // full details needed to find and fix it. Errors on the main window's
    // thread are shown in a message and the app keeps running instead of
    // closing; errors on background threads can't be recovered from, so
    // they're logged just before Windows closes the app.
    public partial class App : Application
    {
        public static string CrashLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrash("UI thread", e.Exception);
            e.Handled = true; // keep the app open
            try
            {
                MessageBox.Show(
                    $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
                    "ArchivSector-OD will keep running. The details were saved to:\n" + CrashLogPath,
                    "ArchivSector-OD — Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* can't even show a message -- the log still has it */ }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrash(e.IsTerminating ? "background thread (app closed)" : "background thread",
                       e.ExceptionObject as Exception);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrash("background task", e.Exception);
            e.SetObserved(); // don't let a forgotten task close the app
        }

        public static void WriteCrash(string where, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);

                // Keep the log from growing forever: start fresh past 1 MB.
                if (File.Exists(CrashLogPath) && new FileInfo(CrashLogPath).Length > 1_000_000)
                    File.Delete(CrashLogPath);

                var sb = new StringBuilder();
                sb.AppendLine("========================================================================");
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  ArchivSector-OD v{UpdateCheckService.CurrentVersionDisplay}  ({where})");
                sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}");
                sb.AppendLine(ex?.ToString() ?? "(no exception details)");
                sb.AppendLine();
                File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
            }
            catch { /* logging must never cause a second crash */ }
        }
    }
}
