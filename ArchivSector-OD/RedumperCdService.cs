using System.Diagnostics;
using System.IO;

namespace ArchivSector_OD
{
    // Finishes a CD dump. For DVDs and Blu-rays, Redumper's "dump" step
    // writes the .iso directly. For CDs it only writes the raw read
    // (<name>.scram plus .state/.subcode/.toc/.fulltoc), and a second
    // step, "split", turns that into the usable image: <name>.cue plus
    // <name>.bin (or "<name> (Track N).bin" for multi-track discs).
    //
    // RunDump looks for an .iso, so it reports every CD dump as failed
    // even when the read was perfect. RunRedumperDump calls this when
    // that happens: if a .scram is there, run split and treat the .bin
    // as the dump.
    //
    // If split refuses because the read had errors ("data errors
    // detected"), this runs Redumper's "refine" step -- it re-reads only
    // the damaged sectors -- and then tries split once more. That's the
    // same dump -> refine -> split order Redumper's own full mode uses.
    //
    // Once the .bin/.cue are made, the .scram and .state (together about
    // the size of the disc) are deleted automatically. On failure they're
    // kept, so the disc doesn't have to be read again from scratch.
    public static class RedumperCdService
    {
        // Returns null when this wasn't a CD dump (no .scram), so the
        // original failure stands. Otherwise returns the split result.
        public static RedumperService.DumpResult? FinishCdDump(
            string exePath,
            string driveLetter,
            string outputFolder,
            string imageName,
            Action<string>? onStatus = null,
            Action<Process>? onProcessStarted = null)
        {
            var scram = Path.Combine(outputFolder, $"{imageName}.scram");
            if (!File.Exists(scram)) return null;

            try
            {
                var (exit, output) = RunRedumper(exePath, outputFolder, onProcessStarted,
                    "split", $"--image-name={imageName}", "--overwrite");
                var result = CheckSplit(outputFolder, imageName, exit);
                if (result is not null) return result;

                // Split refused because of read errors: re-read the bad
                // sectors with refine, then split again.
                if (output.Contains("data errors detected", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("errors detected", StringComparison.OrdinalIgnoreCase))
                {
                    onStatus?.Invoke("The CD read had errors -- re-reading the damaged sectors (Redumper refine)…");
                    RunRedumper(exePath, outputFolder, onProcessStarted,
                        "refine", $"--drive={driveLetter.TrimEnd(':', '\\')}:", $"--image-name={imageName}");

                    onStatus?.Invoke("Re-read finished -- building .bin/.cue again (Redumper split)…");
                    (exit, output) = RunRedumper(exePath, outputFolder, onProcessStarted,
                        "split", $"--image-name={imageName}", "--overwrite");
                    result = CheckSplit(outputFolder, imageName, exit);
                    if (result is not null) return result;

                    return new RedumperService.DumpResult
                    {
                        ErrorMessage = "The CD still has unreadable sectors after re-reading them. " +
                                       "Clean the disc (or try another drive) and dump it again. " +
                                       $"Redumper said: {LastError(output)}",
                    };
                }

                return new RedumperService.DumpResult
                {
                    ErrorMessage = string.IsNullOrWhiteSpace(output)
                        ? "The CD was read, but Redumper's split step didn't produce a .bin/.cue."
                        : $"CD split step failed: {LastError(output)}",
                };
            }
            catch (Exception ex)
            {
                return new RedumperService.DumpResult { ErrorMessage = $"CD split step failed: {ex.Message}" };
            }
        }

        // Success = split made the .cue and at least one .bin. Then the
        // big intermediate files are removed. Returns null if not done.
        private static RedumperService.DumpResult? CheckSplit(string outputFolder, string imageName, int exitCode)
        {
            var cue = Path.Combine(outputFolder, $"{imageName}.cue");
            var bins = Directory.GetFiles(outputFolder, $"{imageName}*.bin");
            if (exitCode != 0 || !File.Exists(cue) || bins.Length == 0) return null;

            // The data track (largest .bin) is what gets hashed and
            // checked against Redump; for most CDs it's the only one.
            var main = bins.OrderByDescending(b => new FileInfo(b).Length).First();

            // The raw read (.scram) and .state are only needed to rebuild
            // the image, and together they're about the size of the disc.
            // The small files (.log, .toc, .fulltoc, .subcode) stay --
            // they're what's used to verify or submit the dump.
            foreach (var ext in new[] { ".scram", ".state" })
            {
                try { File.Delete(Path.Combine(outputFolder, $"{imageName}{ext}")); }
                catch { /* in use or read-only -- leave it, not worth failing over */ }
            }

            return new RedumperService.DumpResult { Success = true, IsoPath = main };
        }

        // Runs one Redumper step in the dump folder and returns its exit
        // code and everything it printed.
        private static (int ExitCode, string Output) RunRedumper(
            string exePath, string workDir, Action<Process>? onProcessStarted, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            var lines = new List<string>();
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lines) lines.Add(e.Data); };

            if (!proc.Start()) return (-1, "Couldn't start redumper.");
            onProcessStarted?.Invoke(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();

            lock (lines) return (proc.ExitCode, string.Join("\n", lines));
        }

        // The useful part of a failure: Redumper's last "error:" line,
        // else the last non-empty line (not the first 300 characters,
        // which used to cut the actual reason off).
        private static string LastError(string output)
        {
            var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var err = lines.LastOrDefault(l => l.StartsWith("error", StringComparison.OrdinalIgnoreCase));
            var msg = err ?? lines.LastOrDefault() ?? "(no output)";
            return msg.Length > 300 ? msg.Substring(msg.Length - 300) : msg;
        }
    }
}
