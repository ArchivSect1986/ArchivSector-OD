using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ArchivSector_OD
{
    // Which drive bays are busy (ripping or computing checksums), so an
    // update never closes the app in the middle of a rip. DriveBayCard
    // reports its state here; UpdateInstallService checks it.
    public static class RipActivity
    {
        private static readonly ConcurrentDictionary<string, bool> Busy = new();

        public static void Set(string driveLetter, string what, bool busy)
        {
            var key = $"{driveLetter}:{what}";
            if (busy) Busy[key] = true;
            else Busy.TryRemove(key, out _);
        }

        public static void ClearDrive(string driveLetter)
        {
            foreach (var key in Busy.Keys)
                if (key.StartsWith($"{driveLetter}:", StringComparison.OrdinalIgnoreCase))
                    Busy.TryRemove(key, out _);
        }

        public static bool AnyBusy => !Busy.IsEmpty;
    }

    // "Update Now": downloads the new release's .zip from GitHub, checks
    // it, unpacks it, then hands over to a small helper script that waits
    // for the app to close, copies the new files over the app folder and
    // starts the app again.
    //
    // Safety:
    //  - refuses while any bay is ripping or computing checksums;
    //  - checks the download against GitHub's SHA-256 when there is one,
    //    and that the zip really contains ArchivSector-OD.exe;
    //  - the helper backs up the current app folder first and puts it
    //    back if copying the new files fails;
    //  - only files that come with the app are replaced -- anything else
    //    in the folder is left alone. Settings, history and artwork live
    //    in %AppData%\ArchivSector-OD and aren't touched at all.
    //
    // When the app folder can't be written to (e.g. under Program Files)
    // or this is a Debug build run from Visual Studio, it falls back to
    // opening the download page instead.
    public static class UpdateInstallService
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ArchivSector-OD-Updater");
            return client;
        }

        private static string AppFolder => AppContext.BaseDirectory.TrimEnd('\\', '/');
        private static string ExeName => Path.GetFileName(Environment.ProcessPath ?? "ArchivSector-OD.exe");

        // Offered at startup and from Settings. Asks first, then runs the
        // whole update. Returns without doing anything if the user says no.
        public static async Task OfferAndRunAsync(UpdateCheckService.UpdateResult result, Window? owner)
        {
            var answer = Ask(owner,
                $"{result.Message}\n\nUpdate now? The app will close, install the new version and reopen " +
                "by itself. Your settings, history and artwork are kept.\n\n" +
                "(You can turn off this check in Settings.)",
                "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;

            await RunAsync(result, owner);
        }

        public static async Task RunAsync(UpdateCheckService.UpdateResult result, Window? owner)
        {
            if (RipActivity.AnyBusy)
            {
                Ask(owner,
                    "A rip (or its checksum) is still running. Update once it's finished -- " +
                    "use Check for Updates in Settings.",
                    "Update Later", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CanUpdateInPlace(out var reason) || string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                if (string.IsNullOrWhiteSpace(result.DownloadUrl))
                    reason = "This release has no .zip download attached.";
                OfferDownloadPage(owner, result, $"Can't update automatically: {reason}");
                return;
            }

            var progress = new UpdateProgressWindow(result.LatestVersion) { Owner = owner };
            progress.Show();

            string workDir = Path.Combine(Path.GetTempPath(), "ArchivSector-OD-update", result.LatestVersion);
            try
            {
                if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
                Directory.CreateDirectory(workDir);

                // 1. Download.
                var zipPath = Path.Combine(workDir, "update.zip");
                await DownloadAsync(result.DownloadUrl, zipPath, result.AssetSize, progress);

                // 2. Check it.
                progress.SetStatus("Checking the download…", null);
                if (!string.IsNullOrEmpty(result.Sha256))
                {
                    var actual = await Task.Run(() => Sha256Of(zipPath));
                    if (!actual.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The download didn't match GitHub's checksum (it may be corrupted). Nothing was changed.");
                }

                // 3. Unpack, and find the folder holding the exe (the zip
                //    usually has one top-level folder like ArchivSector-OD-v2.4).
                progress.SetStatus("Unpacking…", null);
                var extractDir = Path.Combine(workDir, "files");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));
                var newAppDir = FindAppFolder(extractDir)
                    ?? throw new InvalidOperationException($"The download doesn't contain {ExeName}. Nothing was changed.");

                // 4. Hand over to the helper script, then close.
                progress.SetStatus("Restarting to finish the update…", null);
                var backupDir = Path.Combine(workDir, "backup");
                var logPath = Path.Combine(workDir, "update-log.txt");
                var scriptPath = Path.Combine(workDir, "apply-update.cmd");
                // UTF-8 without a BOM (a BOM would break "@echo off"); the
                // script switches the console to UTF-8 so paths with accents work.
                File.WriteAllText(scriptPath, BuildScript(newAppDir, AppFolder, backupDir, Path.Combine(AppFolder, ExeName), logPath), new UTF8Encoding(false));

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{scriptPath}\"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workDir,
                });

                await Task.Delay(300);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                progress.Close();
                try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); } catch { }
                OfferDownloadPage(owner, result, $"The update didn't finish: {ex.Message}");
            }
        }

        // Can the running app replace its own files?
        private static bool CanUpdateInPlace(out string reason)
        {
#if DEBUG
            reason = "this is a Debug build running from Visual Studio.";
            return false;
#else
            try
            {
                var probe = Path.Combine(AppFolder, $".update-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                reason = "";
                return true;
            }
            catch
            {
                reason = $"the app's folder ({AppFolder}) can't be written to. Move the app folder somewhere like your Desktop or Documents, or update by hand.";
                return false;
            }
#endif
        }

        private static async Task DownloadAsync(string url, string target, long expectedSize, UpdateProgressWindow progress)
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? expectedSize;

            await using var input = await resp.Content.ReadAsStreamAsync();
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            int lastPct = -1;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        progress.SetStatus($"Downloading… {done / 1048576.0:F1} / {total / 1048576.0:F1} MB", pct);
                    }
                }
            }
        }

        private static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string? FindAppFolder(string root)
        {
            if (File.Exists(Path.Combine(root, ExeName))) return root;
            return Directory.EnumerateFiles(root, ExeName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(d => d is not null)
                .OrderBy(d => d!.Length)
                .FirstOrDefault();
        }

        // The helper runs after the app has closed (robocopy retries the
        // files the closing app still has open). Steps: back up the app
        // folder, copy the new files in, restore the backup if that
        // failed, then start the app again. robocopy exit codes 0-7 mean
        // success; 8 and up mean something couldn't be copied.
        private static string BuildScript(string source, string target, string backup, string exe, string log)
        {
            return string.Join("\r\n", new[]
            {
                "@echo off",
                "chcp 65001 >nul",
                "setlocal",
                $"set \"SRC={source}\"",
                $"set \"DST={target}\"",
                $"set \"BAK={backup}\"",
                $"set \"EXE={exe}\"",
                $"set \"LOG={log}\"",
                "ping -n 3 127.0.0.1 >nul",
                "echo Backing up >\"%LOG%\"",
                "robocopy \"%DST%\" \"%BAK%\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\"",
                "echo Installing >>\"%LOG%\"",
                "robocopy \"%SRC%\" \"%DST%\" /E /R:60 /W:1 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\"",
                "if %ERRORLEVEL% GEQ 8 (",
                "  echo Install failed, restoring backup >>\"%LOG%\"",
                "  robocopy \"%BAK%\" \"%DST%\" /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP >>\"%LOG%\"",
                ") else (",
                "  echo Done >>\"%LOG%\"",
                ")",
                "start \"\" \"%EXE%\"",
                "endlocal",
                "",
            });
        }

        private static void OfferDownloadPage(Window? owner, UpdateCheckService.UpdateResult result, string message)
        {
            var open = Ask(owner, $"{message}\n\nOpen the download page instead?",
                "Update", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (open != MessageBoxResult.Yes) return;
            try { Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true }); }
            catch { /* no browser available */ }
        }

        private static MessageBoxResult Ask(Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage icon) =>
            owner is not null
                ? MessageBox.Show(owner, text, title, buttons, icon)
                : MessageBox.Show(text, title, buttons, icon);
    }

    // Small progress window shown while the update downloads. Built in
    // code (no XAML file needed) and opened with Show(), never
    // ShowDialog(), like every other window in this app.
    public class UpdateProgressWindow : Window
    {
        private readonly TextBlock statusText;
        private readonly ProgressBar bar;

        public UpdateProgressWindow(string version)
        {
            Title = $"Updating to v{version}";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x1b, 0x2d));

            statusText = new TextBlock
            {
                Text = "Starting download…",
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            };
            bar = new ProgressBar { Height = 16, Minimum = 0, Maximum = 100, IsIndeterminate = true };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(statusText);
            panel.Children.Add(bar);
            Content = panel;
        }

        public void SetStatus(string text, int? percent)
        {
            Dispatcher.Invoke(() =>
            {
                statusText.Text = text;
                if (percent is int p)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Clamp(p, 0, 100);
                }
                else
                {
                    bar.IsIndeterminate = true;
                }
            });
        }
    }
}
