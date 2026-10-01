using System.Diagnostics;
using System.IO;

namespace ArchivSector_OD
{
    // Ported from the Python app's show_toast_notification() --
    // native Windows 10/11 toast via the built-in WinRT APIs through
    // PowerShell, borrowing PowerShell's own registered AppId (a
    // well-established technique for apps without their own app
    // registration). Best-effort only: a missed notification should
    // never interrupt or fail a rip, so all errors are silently
    // swallowed, matching the Python app's own behavior exactly.
    public static class NotificationService
    {
        public static void Show(string title, string message)
        {
            try
            {
                var script = """
param(
    [string]$ToastTitle,
    [string]$ToastBody
)
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
$SafeTitle = [System.Security.SecurityElement]::Escape($ToastTitle)
$SafeBody = [System.Security.SecurityElement]::Escape($ToastBody)
$template = @"
<toast>
    <visual>
        <binding template="ToastGeneric">
            <text>$SafeTitle</text>
            <text>$SafeBody</text>
        </binding>
    </visual>
</toast>
"@
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
$AppId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($AppId).Show($toast)
""";
                var configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ArchivSector-OD");
                Directory.CreateDirectory(configDir);
                var scriptPath = Path.Combine(configDir, "_toast_notify.ps1");
                File.WriteAllText(scriptPath, script);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(scriptPath);
                psi.ArgumentList.Add("-ToastTitle");
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add("-ToastBody");
                psi.ArgumentList.Add(message);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);
            }
            catch
            {
                // Best-effort only -- a missed notification should
                // never interrupt or fail a rip.
            }
        }
    }
}