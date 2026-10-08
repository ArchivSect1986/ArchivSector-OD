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
    // as the dump. The .scram is kept, like Redumper itself does.
    public static class RedumperCdService
    {
        // Returns null when this wasn't a CD dump (no .scram), so the
        // original failure stands. Otherwise returns the split result.
        public static RedumperService.DumpResult? FinishCdDump(
            string exePath,
            string outputFolder,
            string imageName,
            Action<Process>? onProcessStarted = null)
        {
            var scram = Path.Combine(outputFolder, $"{imageName}.scram");
            if (!File.Exists(scram)) return null;

            var tail = new List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = outputFolder,
                };
                psi.ArgumentList.Add("split");
                psi.ArgumentList.Add($"--image-name={imageName}");
                psi.ArgumentList.Add("--overwrite");

                using var proc = new Process { StartInfo = psi };
                void Keep(string? line)
                {
                    if (line is null) return;
                    lock (tail)
                    {
                        tail.Add(line);
                        if (tail.Count > 25) tail.RemoveAt(0);
                    }
                }
                proc.OutputDataReceived += (_, e) => Keep(e.Data);
                proc.ErrorDataReceived += (_, e) => Keep(e.Data);

                if (!proc.Start())
                    return new RedumperService.DumpResult { ErrorMessage = "Couldn't start redumper for the CD split step." };
                onProcessStarted?.Invoke(proc);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                var cue = Path.Combine(outputFolder, $"{imageName}.cue");
                var bins = Directory.GetFiles(outputFolder, $"{imageName}*.bin");
                if (proc.ExitCode == 0 && File.Exists(cue) && bins.Length > 0)
                {
                    // The data track (largest .bin) is what gets hashed and
                    // checked against Redump; for most game CDs it's the
                    // only one.
                    var main = bins.OrderByDescending(b => new FileInfo(b).Length).First();
                    return new RedumperService.DumpResult { Success = true, IsoPath = main };
                }

                string captured;
                lock (tail) captured = string.Join("\n", tail);
                return new RedumperService.DumpResult
                {
                    ErrorMessage = string.IsNullOrWhiteSpace(captured)
                        ? "CD dump read fine, but Redumper's split step didn't produce a .bin/.cue."
                        : $"CD split step failed: {(captured.Length > 300 ? captured.Substring(0, 300) : captured)}",
                };
            }
            catch (Exception ex)
            {
                return new RedumperService.DumpResult { ErrorMessage = $"CD split step failed: {ex.Message}" };
            }
        }
    }
}
