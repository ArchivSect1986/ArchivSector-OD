using System.Diagnostics;
using System.IO;

namespace ArchivSector_OD
{
    // Ported from create_desktop_shortcut() -- simplified from the
    // Python app's two-branch version (frozen exe vs plain script)
    // since a compiled .NET app is always "frozen" in that sense;
    // there's no script-mode equivalent to branch on here. Creates a
    // real .lnk via a throwaway VBScript run through cscript -- the
    // standard dependency-free way to create a proper Windows
    // shortcut without a COM interop package, and it naturally picks
    // up the .exe's own embedded icon.
    public static class ShortcutService
    {
        public class Result
        {
            public bool Success;
            public string? Path;
            public string? ErrorMessage;
        }

        public static Result CreateDesktopShortcut()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return new Result { ErrorMessage = "Could not determine this app's own executable path." };

                var linkPath = Path.Combine(desktop, "ArchivSector-OD.lnk");
                var configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ArchivSector-OD");
                Directory.CreateDirectory(configDir);
                var vbsPath = Path.Combine(configDir, "_create_shortcut.vbs");

                var vbsContent =
                    "Set oWS = WScript.CreateObject(\"WScript.Shell\")\n" +
                    $"Set oLink = oWS.CreateShortcut(\"{linkPath}\")\n" +
                    $"oLink.TargetPath = \"{exePath}\"\n" +
                    $"oLink.WorkingDirectory = \"{Path.GetDirectoryName(exePath)}\"\n" +
                    $"oLink.IconLocation = \"{exePath}, 0\"\n" +
                    "oLink.Save\n";
                File.WriteAllText(vbsPath, vbsContent);

                var psi = new ProcessStartInfo
                {
                    FileName = "cscript",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("//nologo");
                psi.ArgumentList.Add(vbsPath);

                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);

                try { File.Delete(vbsPath); } catch { }

                if (proc is not null && proc.ExitCode == 0 && File.Exists(linkPath))
                    return new Result { Success = true, Path = linkPath };

                return new Result { ErrorMessage = "cscript did not report success -- shortcut may not have been created." };
            }
            catch (Exception ex)
            {
                return new Result { ErrorMessage = $"Could not create shortcut: {ex.Message}" };
            }
        }
    }
}