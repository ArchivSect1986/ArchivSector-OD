using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ArchivSector_OD
{
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig config;

        public SettingsWindow(AppConfig config)
        {
            InitializeComponent();
            this.config = config;

            // Read from the build itself (<Version> in the .csproj), so
            // there's only one place to change it for a new release.
            VersionText.Text = $"ArchivSector-OD — v{UpdateCheckService.CurrentVersionDisplay}";

            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchivSector-OD");
            ConfigDirText.Text = configDir;

            OutputFolderBox.Text = config.OutputBaseFolder;
            MakeMkvPathBox.Text = config.MakeMkvPathOverride;
            RedumperPathBox.Text = config.RedumperPathOverride;
            ImgBurnPathBox.Text = config.ImgBurnPathOverride;
            MkvmergePathBox.Text = config.MkvmergePathOverride;
            RawgKeyBox.Password = config.RawgApiKey;
            TmdbKeyBox.Password = config.TmdbApiKey;

            AutoCategorizeCheck.IsChecked = config.AutoCategorize;
            AutoEjectCheck.IsChecked = config.AutoEject;
            ToastCheck.IsChecked = config.ToastNotifications;
            SoundCheck.IsChecked = config.SoundAlerts;

            AutoFetchArtCheck.IsChecked = config.AutoFetchMetadataArt;
            BatchQueueCheck.IsChecked = config.BatchQueueMode;
            MultiDriveSyncCheck.IsChecked = config.MultiDriveBatchSync;
            MaxConcurrentBox.Text = config.MaxConcurrentRips.ToString();

            RefreshDatList();
        }

        private void RefreshDatList()
        {
            var dats = RedumpDatService.ListImportedDats();
            DatListText.Text = dats.Count == 0
                ? "No DAT files imported yet — rips will show \u201cnot verified\u201d until you add one."
                : string.Join("\n", dats);
        }

        private void ImportDatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Redump DAT File",
                Filter = "Redump DAT files (*.dat;*.xml)|*.dat;*.xml|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                if (RedumpDatService.ImportDat(dialog.FileName))
                    RefreshDatList();
                else
                    MessageBox.Show("Couldn't import that file.", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenRedumpSiteButton_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "http://redump.org/downloads/", UseShellExecute = true }); }
            catch { /* best-effort */ }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(ConfigDirText.Text);
                Process.Start(new ProcessStartInfo { FileName = ConfigDirText.Text, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ConfigDirText.Text}\n\nError: {ex.Message}", "Couldn't Open Folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RunDiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            var result = DiagnosticService.Run();

            if (result.InternalWriteError)
            {
                MessageBox.Show(
                    $"Couldn't even write the test file:\n{result.ConfigDir}\n\nError: {result.InternalWriteErrorMessage}",
                    "Diagnostic: Write Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (result.ExternalError is not null)
            {
                MessageBox.Show(
                    $"Wrote the test file successfully (this app sees it: {result.InternalExists}, {result.InternalSize} bytes), but couldn't run the external check:\n{result.ExternalError}",
                    "Diagnostic: Inconclusive", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.InternalExists && !result.ExternalSeesFile)
            {
                MessageBox.Show(
                    "This app's own process believes it successfully wrote a file — but an independent Command Prompt process checking the exact same path cannot see it at all.\n\n" +
                    "This is conclusive evidence that something is silently isolating this app's writes from the real filesystem — most commonly a security product's sandboxing/virtualization feature for unrecognized applications.\n\n" +
                    $"Test file: {result.TestFile}\n" +
                    $"This app sees it: Yes ({result.InternalSize} bytes)\n" +
                    "External process sees it: No",
                    "Diagnostic: Write Virtualization Detected", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (result.InternalExists && result.ExternalSeesFile)
            {
                MessageBox.Show(
                    $"Wrote a test file and an independent Command Prompt process confirmed it's really there.\n\n{result.ConfigDir}\n\n" +
                    "File writes are working correctly.",
                    "Diagnostic: Writes Are Working", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    $"This app doesn't believe the file was written, but no error was raised.\n\n{result.ConfigDir}",
                    "Diagnostic: Unexpected Result", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select Output Base Folder" };
            if (dialog.ShowDialog() == true)
                OutputFolderBox.Text = dialog.FolderName;
        }

        private void BrowseImgBurnButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Executable (*.exe)|*.exe" };
            if (dialog.ShowDialog() == true)
                ImgBurnPathBox.Text = dialog.FileName;
        }

        private void BrowseMkvmergeButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Executable (*.exe)|*.exe" };
            if (dialog.ShowDialog() == true)
                MkvmergePathBox.Text = dialog.FileName;
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text = "Checking for updates…";

            var result = await UpdateCheckService.CheckAsync();

            CheckUpdatesButton.IsEnabled = true;
            UpdateStatusText.Text = result.Message;

            if (result.Status == UpdateCheckService.UpdateStatus.UpdateAvailable)
            {
                var open = MessageBox.Show(this,
                    $"{result.Message}\n\nOpen the download page?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true }); }
                    catch { /* no browser available -- the status line still shows the result */ }
                }
            }
        }

        private void CreateShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            var result = ShortcutService.CreateDesktopShortcut();
            MessageText.Text = result.Success ? $"Shortcut created: {result.Path}" : result.ErrorMessage;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            config.OutputBaseFolder = OutputFolderBox.Text.Trim();
            config.MakeMkvPathOverride = MakeMkvPathBox.Text.Trim();
            config.RedumperPathOverride = RedumperPathBox.Text.Trim();
            config.ImgBurnPathOverride = ImgBurnPathBox.Text.Trim();
            config.MkvmergePathOverride = MkvmergePathBox.Text.Trim();
            config.RawgApiKey = RawgKeyBox.Password.Trim();
            config.TmdbApiKey = TmdbKeyBox.Password.Trim();

            config.AutoCategorize = AutoCategorizeCheck.IsChecked ?? true;
            config.AutoEject = AutoEjectCheck.IsChecked ?? true;
            config.ToastNotifications = ToastCheck.IsChecked ?? true;
            config.SoundAlerts = SoundCheck.IsChecked ?? true;

            config.AutoFetchMetadataArt = AutoFetchArtCheck.IsChecked ?? true;
            config.BatchQueueMode = BatchQueueCheck.IsChecked ?? false;
            config.MultiDriveBatchSync = MultiDriveSyncCheck.IsChecked ?? false;
            config.MaxConcurrentRips = int.TryParse(MaxConcurrentBox.Text, out var n) ? Math.Max(1, n) : 1;

            ConfigService.Save(config);
            MessageText.Text = "Saved.";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}