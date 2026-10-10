using System.IO;
using System.Text.Json;

namespace ArchivSector_OD
{
    // The app's persisted config. Load() fails safe to defaults on
    // any error (missing file, corrupt JSON, permissions) so a broken
    // config file can never prevent the app from starting.
    public class AppConfig
    {
        public string Theme { get; set; } = "midnight";
        public string OutputBaseFolder { get; set; } = "";
        public string MakeMkvPathOverride { get; set; } = "";
        public string RedumperPathOverride { get; set; } = "";
        public string ImgBurnPathOverride { get; set; } = "";
        public string MkvmergePathOverride { get; set; } = "";
        public string RawgApiKey { get; set; } = "";
        public string TmdbApiKey { get; set; } = "";

        public bool AutoCategorize { get; set; } = true;
        public bool AutoEject { get; set; } = true;
        public bool ToastNotifications { get; set; } = true;

        // Ported from the Python app's "sound_alerts" (on by default):
        // a system beep when a rip completes successfully.
        public bool SoundAlerts { get; set; } = true;

        // Ask GitHub for a newer release each time the app starts, and
        // offer to install it (Settings -> "Check for updates when the
        // app starts").
        public bool CheckForUpdatesOnStartup { get; set; } = true;

        public bool AutoFetchMetadataArt { get; set; } = true;
        public bool BatchQueueMode { get; set; } = false;
        public bool MultiDriveBatchSync { get; set; } = false;
        public int MaxConcurrentRips { get; set; } = 1;

        // Header-level toggle, not a Settings preference -- the kind
        // of thing flipped on right before leaving a disc-swapping
        // session unattended, off when back. Persists across restarts
        // like everything else here, but lives in AppShell's header,
        // matching where the Python app puts its own equivalent.
        public bool UnattendedAutoStart { get; set; } = false;

        public Dictionary<string, string> BayLabels { get; set; } = new();

        public HashSet<string> ConfirmedOmniDriveLetters { get; set; } = new();
    }

    // The TargetFill tab's choices, remembered between runs like
    // TargetFill Pro's own config.json. Kept in their own file
    // (%AppData%\ArchivSector-OD\targetfill.json) so saving them never
    // races with Settings or the drive bays saving config.json.
    public class TargetFillSettings
    {
        public string Media { get; set; } = "Blu-ray BD-25";   // preset label, or "Custom"/"Custom USB"
        public double CustomOpticalGb { get; set; } = 32;
        public double CustomUsbGb { get; set; } = 64;
        public int Placement { get; set; } = 0;        // 0 Dual-Part Split, 1 Single: End, 2 Single: Start
        public int SplitPercent { get; set; } = 50;
        public bool Physical { get; set; } = false;
        public string VolumeLabel { get; set; } = "";
        public bool ExpertMode { get; set; } = false;
        public bool Manifest { get; set; } = true;
        public bool Verifier { get; set; } = true;
        public bool Catalog { get; set; } = true;
        public bool Preflight { get; set; } = true;
        public bool LbaReport { get; set; } = true;
        public bool AutoSpan { get; set; } = false;
        public bool Parity { get; set; } = false;
        public bool Noise { get; set; } = false;
        public bool Stealth { get; set; } = false;
        public bool Sound { get; set; } = true;        // "Audio Feedback Cues"

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "targetfill.json");

        public static TargetFillSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<TargetFillSettings>(File.ReadAllText(FilePath));
                    if (s is not null) return s;
                }
            }
            catch { /* corrupt -- use defaults */ }
            return new TargetFillSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best-effort */ }
        }
    }

    public static class ConfigService
    {
        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg is not null) return cfg;
                }
            }
            catch
            {
                // Corrupt or unreadable config -- fail safe to defaults
                // rather than block the app from starting.
            }
            return new AppConfig();
        }

        public static void Save(AppConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath)!;
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // Best-effort -- a failed save shouldn't crash
                // whatever the user was doing when it happened.
            }
        }
    }
}
