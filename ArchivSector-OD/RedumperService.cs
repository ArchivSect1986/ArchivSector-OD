using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ArchivSector_OD
{
    // Ported from the Python app's find_redumper_exe() and
    // _redumper_worker(). Unlike MakeMKV, ImgBurn, or mkvmerge,
    // Redumper doesn't ship an installer or register a standard
    // Windows install location -- it's typically just a portable exe
    // someone drops wherever they like. This only checks a Settings
    // override first, then PATH, then the one conventional default
    // the Python app already assumed (C:\redumper\redumper.exe).
    //
    // Unlike MakeMKV's backup mode, Redumper DOES reliably report
    // live percentage progress through its own stdout (confirmed by
    // the Python app's own regex-based parsing of it), so this reads
    // output line-by-line as it streams instead of the folder-size-
    // polling workaround MakeMkvService needs.
    public static class RedumperService
    {
        private const string DefaultPath = @"C:\redumper\redumper.exe";

        public static string? FindExecutable(string? overridePath = null)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            foreach (var name in new[] { "redumper.exe", "redumper" })
            {
                var found = FindOnPath(name);
                if (found is not null) return found;
            }
            if (File.Exists(DefaultPath)) return DefaultPath;
            return null;
        }

        private static string? FindOnPath(string exeName)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* malformed PATH entry -- skip it */ }
            }
            return null;
        }

        public class DumpResult
        {
            public bool Success;
            public string? IsoPath;
            public string? ErrorMessage;
        }

        public static DumpResult RunDump(
            string exePath,
            string driveLetter,
            string outputFolder,
            string isoName,
            Action<int>? onProgress = null,
            Action<Process>? onProcessStarted = null)
        {
            try
            {
                Directory.CreateDirectory(outputFolder);
                var isoFile = Path.Combine(outputFolder, $"{isoName}.iso");

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = outputFolder,
                };
                psi.ArgumentList.Add("dump");
                psi.ArgumentList.Add($"--drive={driveLetter.TrimEnd(':', '\\')}:");
                psi.ArgumentList.Add($"--image-name={isoName}");
                psi.ArgumentList.Add("--overwrite");

                var stderrLines = new List<string>();
                var stdoutTail = new List<string>();
                var percentRegex = new Regex(@"(\d{1,3})\s*%");

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (stdoutTail)
                    {
                        stdoutTail.Add(e.Data);
                        if (stdoutTail.Count > 25) stdoutTail.RemoveAt(0);
                    }
                    var m = percentRegex.Match(e.Data);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
                        onProgress?.Invoke(Math.Clamp(pct, 0, 100));
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (stderrLines) stderrLines.Add(e.Data);
                };

                if (!proc.Start())
                    return new DumpResult { ErrorMessage = "Failed to start redumper." };

                onProcessStarted?.Invoke(proc);

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                bool success = proc.ExitCode == 0 && File.Exists(isoFile);

                if (!success)
                {
                    string captured;
                    lock (stderrLines) { captured = string.Join("\n", stderrLines); }
                    if (string.IsNullOrWhiteSpace(captured))
                        lock (stdoutTail) { captured = string.Join("\n", stdoutTail); }

                    return new DumpResult
                    {
                        ErrorMessage = string.IsNullOrWhiteSpace(captured)
                            ? "Redumper failed (no output captured)."
                            : captured.Length > 300 ? captured.Substring(captured.Length - 300) : captured
                    };
                }

                return new DumpResult { Success = true, IsoPath = isoFile };
            }
            catch (Exception ex)
            {
                return new DumpResult { ErrorMessage = $"Redumper dump failed: {ex.Message}" };
            }
        }
    }
}