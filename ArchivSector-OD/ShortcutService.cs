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
    //
    // v3.4: also finds shortcuts that open an older copy of the app and
    // points them at the running one (see FindStaleShortcuts).
    public static class ShortcutService
    {
        public class Result
        {
            public bool Success;
            public string? Path;
            public string? ErrorMessage;
        }

        // ------------------------------------------------------------------
        // v3.4: shortcuts that open an older copy of the app
        // ------------------------------------------------------------------
        // The updater replaces the files of the copy that's running. If a
        // Desktop / Start Menu / taskbar shortcut points to a different,
        // older copy (say an old folder in Downloads), that copy never gets
        // updated and the shortcut keeps opening the old version. These
        // find such shortcuts and point them at the running copy.
        public sealed class StaleShortcut
        {
            public string LinkPath = "";
            public string Target = "";
            public string Where = "";            // "Desktop", "Start Menu", "Taskbar"
            public string? TargetVersion;        // null if the target is gone
        }

        private const string AppExeName = "ArchivSector-OD.exe";

        // Debug builds (run from Visual Studio) never change shortcuts.
#if DEBUG
        public static readonly bool IsDebugBuild = true;
#else
        public static readonly bool IsDebugBuild = false;
#endif

        public static List<StaleShortcut> FindStaleShortcuts()
        {
            var found = new List<StaleShortcut>();
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return found;
            var mine = Ver(UpdateCheckService.CurrentVersion);
            var ignored = new HashSet<string>(ConfigService.Load().IgnoredShortcuts ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            RunSta(() =>
            {
                object? shellObj = null;
                try
                {
                    shellObj = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                    dynamic shell = shellObj!;
                    foreach (var (dir, where, recursive) in ShortcutFolders())
                    {
                        IEnumerable<string> links;
                        try
                        {
                            if (!Directory.Exists(dir)) continue;
                            links = Directory.EnumerateFiles(dir, "*.lnk", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                        }
                        catch { continue; }

                        foreach (var link in links)
                        {
                            string target;
                            try { target = (string)shell.CreateShortcut(link).TargetPath ?? ""; }
                            catch { continue; }
                            if (!string.Equals(Path.GetFileName(target), AppExeName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (SamePath(target, exe)) continue;
                            if (ignored.Contains(link + "|" + target)) continue;

                            string? theirs = null;
                            if (File.Exists(target))
                            {
                                var v = FileVersion(target);
                                if (v is null || v >= mine) continue;   // same or newer copy: leave it alone
                                theirs = $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
                            }
                            found.Add(new StaleShortcut { LinkPath = link, Target = target, Where = where, TargetVersion = theirs });
                        }
                    }
                }
                catch { /* WScript.Shell unavailable: nothing to check */ }
                finally
                {
                    if (shellObj is not null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shellObj); } catch { }
                }
            });
            return found;
        }

        // Points each shortcut at the running copy. Returns the ones that
        // couldn't be changed (all-users shortcuts need administrator rights).
        public static List<(StaleShortcut Shortcut, string Error)> Repoint(IEnumerable<StaleShortcut> shortcuts)
        {
            var failed = new List<(StaleShortcut, string)>();
            var exe = Environment.ProcessPath ?? "";
            var dir = Path.GetDirectoryName(exe) ?? "";
            RunSta(() =>
            {
                object? shellObj = null;
                try
                {
                    shellObj = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                    dynamic shell = shellObj!;
                    foreach (var sc in shortcuts)
                    {
                        try
                        {
                            dynamic link = shell.CreateShortcut(sc.LinkPath);
                            link.TargetPath = exe;
                            link.WorkingDirectory = dir;
                            link.IconLocation = exe + ", 0";
                            link.Save();
                        }
                        catch (Exception ex) { failed.Add((sc, ex is UnauthorizedAccessException ? "needs administrator rights" : ex.Message)); }
                    }
                }
                catch (Exception ex)
                {
                    foreach (var sc in shortcuts) failed.Add((sc, ex.Message));
                }
                finally
                {
                    if (shellObj is not null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shellObj); } catch { }
                }
            });
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
            return failed;
        }

        // "No" remembers the pairing, so the same shortcut isn't asked about again.
        public static void Ignore(IEnumerable<StaleShortcut> shortcuts)
        {
            try
            {
                var config = ConfigService.Load();
                config.IgnoredShortcuts ??= new List<string>();
                foreach (var sc in shortcuts)
                {
                    var key = sc.LinkPath + "|" + sc.Target;
                    if (!config.IgnoredShortcuts.Contains(key, StringComparer.OrdinalIgnoreCase)) config.IgnoredShortcuts.Add(key);
                }
                ConfigService.Save(config);
            }
            catch { }
        }

        private static IEnumerable<(string Dir, string Where, bool Recursive)> ShortcutFolders()
        {
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop", false);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Desktop (all users)", false);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Start Menu", true);
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Start Menu (all users)", true);
            yield return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"), "Taskbar", false);
        }

        private static Version Ver(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

        private static Version? FileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
            catch { return null; }
        }

        private static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static void RunSta(Action work)
        {
            var th = new Thread(() => { try { work(); } catch { } });
            th.SetApartmentState(ApartmentState.STA);
            th.IsBackground = true;
            th.Start();
            th.Join();
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

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