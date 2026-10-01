using System.Diagnostics;
using System.IO;

namespace ArchivSector_OD
{
    // Ported from run_write_diagnostic() -- writes a small test file,
    // then checks from a genuinely SEPARATE process (a plain cmd.exe
    // instance) whether that file is actually visible outside this
    // app. Normally it would be, but some security software silently
    // sandboxes an unrecognized app's writes into a virtual, isolated
    // location -- the app itself sees the write as successful (it can
    // even read the file back), while nothing else on the machine can
    // see it at all. Checking from a genuinely separate process is
    // what catches that, rather than the app just trusting its own
    // view of the filesystem.
    public static class DiagnosticService
    {
        public class DiagnosticResult
        {
            public bool InternalWriteError;
            public string? InternalWriteErrorMessage;
            public bool InternalExists;
            public long InternalSize;
            public bool ExternalSeesFile;
            public string? ExternalError;
            public string ConfigDir = "";
            public string TestFile = "";
        }

        public static DiagnosticResult Run()
        {
            var result = new DiagnosticResult();
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchivSector-OD");
            result.ConfigDir = configDir;
            var testFile = Path.Combine(configDir, "_write_test.tmp");
            result.TestFile = testFile;

            try
            {
                Directory.CreateDirectory(configDir);
                File.WriteAllText(testFile, "diagnostic test " + DateTime.Now);
                result.InternalExists = File.Exists(testFile);
                if (result.InternalExists)
                    result.InternalSize = new FileInfo(testFile).Length;
            }
            catch (Exception ex)
            {
                result.InternalWriteError = true;
                result.InternalWriteErrorMessage = ex.Message;
                return result;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add($"if exist \"{testFile}\" (echo YES) else (echo NO)");

                using var proc = Process.Start(psi);
                string output = proc?.StandardOutput.ReadToEnd() ?? "";
                proc?.WaitForExit(5000);
                result.ExternalSeesFile = output.Trim().Equals("YES", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                result.ExternalError = ex.Message;
            }
            finally
            {
                try { File.Delete(testFile); } catch { }
            }

            return result;
        }
    }
}