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
