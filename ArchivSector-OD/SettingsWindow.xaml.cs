using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchivSector_OD
{
    // ⚙ Settings (v4.0 beta 5). Sections on the left, one shown at a time;
    // a line of help under each setting; under every program path a ✓ or
    // ⚠ that updates as you type (found, found by itself, or not found).
    // The colours, corners and font come from the theme picked in the 🎨
    // menu (app.css), so the window matches the rest of the app, and
    // change with it when another theme is picked while it's open.
    // Save keeps the window open; closing with unsaved changes asks first.
    public partial class SettingsWindow : Window
    {
        private readonly AppConfig config;
        private bool loading = true, dirty;
        private static string lastPage = "GeneralPage";   // reopened on the section last looked at

        public SettingsWindow(AppConfig config)
        {
            InitializeComponent();
            this.config = config;
            ApplyTheme(config);

            // Read from the build itself (<Version> in the .csproj), so
            // there's only one place to change it for a new release.
            VersionText.Text = $"ArchivSector-OD v{UpdateCheckService.CurrentVersionDisplay}";
            SideVersionText.Text = $"v{UpdateCheckService.CurrentVersionDisplay}";

            ConfigDirText.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ArchivSector-OD");

            OutputFolderBox.Text = config.OutputBaseFolder;
            MakeMkvPathBox.Text = config.MakeMkvPathOverride;
            RedumperPathBox.Text = config.RedumperPathOverride;
            ImgBurnPathBox.Text = config.ImgBurnPathOverride;
            MkvmergePathBox.Text = config.MkvmergePathOverride;
            BurnerMaxPathBox.Text = config.BurnerMaxPath;
            RawgKeyBox.Password = config.RawgApiKey;
            TmdbKeyBox.Password = config.TmdbApiKey;

            AutoCategorizeCheck.IsChecked = config.AutoCategorize;
            AutoEjectCheck.IsChecked = config.AutoEject;
            ToastCheck.IsChecked = config.ToastNotifications;
            SoundCheck.IsChecked = config.SoundAlerts;
            StartupUpdateCheck.IsChecked = config.CheckForUpdatesOnStartup;
            LiveLogCheck.IsChecked = config.LiveLogOpensWithBurnTab;
            DeleteIsoCheck.IsChecked = config.DeleteIsoAfterBurn;
            SeasonalCheck.IsChecked = seasonalAtOpen = config.AutoSeasonalTheme;

            AutoFetchArtCheck.IsChecked = config.AutoFetchMetadataArt;
            BatchQueueCheck.IsChecked = config.BatchQueueMode;
            MultiDriveSyncCheck.IsChecked = config.MultiDriveBatchSync;
            MaxConcurrentBox.Text = config.MaxConcurrentRips.ToString();

            RefreshDatList();
            RefreshStatuses();
            WatchForChanges();
            loading = false;

            // Open on the section looked at last time.
            foreach (ListBoxItem item in Nav.Items)
                if ((string)item.Tag == lastPage) { Nav.SelectedItem = item; break; }
            if (Nav.SelectedItem is null) Nav.SelectedIndex = 0;

            Closing += SettingsWindow_Closing;

            // Follows the theme: pick another in the 🎨 menu while this
            // window is open, and it changes as soon as you click back into it.
            Activated += (_, _) =>
            {
                try
                {
                    var now = WpfTheme.CurrentId();
                    if (now == shownTheme) return;
                    ApplyTheme(ConfigService.Load());
                    RefreshStatuses();
                    SetMessage(MessageText.Text, messageOk);
                }
                catch { }
            };
        }

        private string? shownTheme;
        private bool seasonalAtOpen;   // the 🎨 menu has the same checkbox: only saved from here if changed here
        private bool? messageOk;

        // ---- Sections ----

        private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Nav.SelectedItem is not ListBoxItem item) return;
            string page = (string)item.Tag;
            foreach (var p in new FrameworkElement[] { GeneralPage, AppearancePage, ToolsPage, BurnPage, LookupPage, BatchPage, RedumpPage, AboutPage })
                p.Visibility = p.Name == page ? Visibility.Visible : Visibility.Collapsed;
            lastPage = page;
            PageScroll?.ScrollToTop();
        }

        // ---- Theme ----

        // The theme's colours, corners and font (WpfTheme reads them from
        // app.css). The window keeps its own control styles in the XAML.
        private void ApplyTheme(AppConfig c)
        {
            shownTheme = WpfTheme.ApplyColours(this);
            ThemeNameText.Text = AppShell.ThemeName(shownTheme);
        }

        private Brush Res(string key) => (Brush)Resources[key];

        // ---- ✓ / ⚠ under each path ----

        private void RefreshStatuses()
        {
            // Output folder
            string output = CleanPath(OutputFolderBox.Text);
            if (output.Length == 0)
                SetStatus(OutputFolderStatus, null, "Not set: rips go to " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArchivSector-OD-Test"));
            else if (Directory.Exists(output))
                SetStatus(OutputFolderStatus, true, "✓ Folder found");
            else
                SetStatus(OutputFolderStatus, false, "⚠ This folder doesn't exist yet. Pick it with Browse, or check the spelling.");

            ToolStatus(MakeMkvStatus, MakeMkvPathBox.Text, MakeMkvService.FindExecutable, "Install MakeMKV from makemkv.com, or point to makemkvcon64.exe in its folder.", required: true);
            ToolStatus(RedumperStatus, RedumperPathBox.Text, RedumperService.FindExecutable, "Download Redumper and point to redumper.exe. Only needed for game dumps.", required: false);
            ToolStatus(ImgBurnStatus, ImgBurnPathBox.Text, ImgBurnService.FindExecutable, "Not installed. That's fine unless you need it for the things above.", required: false);
            ToolStatus(MkvmergeStatus, MkvmergePathBox.Text, MkvmergeService.FindExecutable, "Not installed. Install MKVToolNix to use it.", required: false);

            string bm = CleanPath(BurnerMaxPathBox.Text);
            if (bm.Length == 0)
                SetStatus(BurnerMaxStatus, null, "Not set: you're asked where it is the first time an XGD3 game needs it.");
            else if (File.Exists(bm))
                SetStatus(BurnerMaxStatus, true, "✓ Found");
            else
                SetStatus(BurnerMaxStatus, false, "⚠ That file doesn't exist. Pick BurnerMax.exe with Browse.");

            KeyStatus(TmdbStatus, TmdbKeyBox.Password, "No key: movie titles and artwork won't be looked up.");
            KeyStatus(RawgStatus, RawgKeyBox.Password, "No key: game titles and artwork won't be looked up.");
        }

        private void ToolStatus(TextBlock status, string boxText, Func<string?, string?> find, string notFound, bool required)
        {
            string typed = CleanPath(boxText);
            string? auto = null;
            try { auto = find(null); } catch { }
            if (typed.Length == 0)
            {
                if (auto is not null) SetStatus(status, true, "✓ Found by itself: " + auto);
                else SetStatus(status, required ? false : null, (required ? "⚠ Not found. " : "Not found. ") + notFound);
            }
            else if (File.Exists(typed))
                SetStatus(status, true, "✓ Found: this copy will be used");
            else
                SetStatus(status, false, "⚠ That file doesn't exist." + (auto is not null ? " The copy found by itself is used instead: " + auto : " " + notFound));
        }

        private void KeyStatus(TextBlock status, string key, string none)
        {
            if (key.Trim().Length > 0) SetStatus(status, true, "✓ Key entered");
            else SetStatus(status, null, none);
        }

        // ok: true ✓ green, false ⚠ amber, null plain note.
        private void SetStatus(TextBlock status, bool? ok, string text)
        {
            status.Text = text;
            status.Foreground = ok == true ? Res("Success") : ok == false ? Res("Amber") : Res("Muted");
        }

        // ---- Unsaved changes ----

        private void WatchForChanges()
        {
            foreach (var box in new[] { OutputFolderBox, MakeMkvPathBox, RedumperPathBox, ImgBurnPathBox, MkvmergePathBox, BurnerMaxPathBox, MaxConcurrentBox })
                box.TextChanged += (_, _) => Changed();
            foreach (var box in new[] { RawgKeyBox, TmdbKeyBox })
                box.PasswordChanged += (_, _) => Changed();
            foreach (var check in new[] { AutoCategorizeCheck, AutoEjectCheck, ToastCheck, SoundCheck, StartupUpdateCheck, LiveLogCheck, DeleteIsoCheck, SeasonalCheck, AutoFetchArtCheck, BatchQueueCheck, MultiDriveSyncCheck })
            {
                check.Checked += (_, _) => Changed();
                check.Unchecked += (_, _) => Changed();
            }
        }

        private void Changed()
        {
            if (loading) return;
            dirty = true;
            RefreshStatuses();
            SetMessage("Not saved yet.", null);
        }

        private void SetMessage(string text, bool? ok)
        {
            MessageText.Text = text;
            messageOk = ok;
            MessageText.Foreground = ok == true ? Res("Success") : ok == false ? Res("Amber") : Res("Muted");
        }

        private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!dirty) return;
            var answer = MessageBox.Show(this, "Save your changes before closing?", "Settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) e.Cancel = true;
            else if (answer == MessageBoxResult.Yes) Save();
        }

        // ---- Redump ----

        private void RefreshDatList()
        {
            var dats = RedumpDatService.ListImportedDats();
            DatListText.Text = dats.Count == 0
                ? "None yet: game rips show \u201cnot verified\u201d until the list for their system is imported."
                : string.Join("\n", dats);
        }

        private void ImportDatButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import a Redump DAT file",
                Filter = "Redump DAT files (*.dat;*.xml)|*.dat;*.xml|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                if (RedumpDatService.ImportDat(dialog.FileName))
                {
                    RefreshDatList();
                    SetMessage("✓ Imported " + Path.GetFileName(dialog.FileName), true);
                }
                else
                    MessageBox.Show(this, "Couldn't import that file. Is it a Redump .dat (unzipped)?", "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenRedumpSiteButton_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo { FileName = "http://redump.org/downloads/", UseShellExecute = true }); }
            catch { /* best-effort */ }
        }

        // ---- About ----

        // The About card's buttons: each has its web address in Tag.
        private void Link_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string url) return;
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch { SetMessage("Couldn't open the browser: " + url, false); }
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
                MessageBox.Show(this, $"{ConfigDirText.Text}\n\nError: {ex.Message}", "Couldn't open the folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RunDiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            var result = DiagnosticService.Run();

            if (result.InternalWriteError)
            {
                MessageBox.Show(this,
                    $"Couldn't even write the test file:\n{result.ConfigDir}\n\nError: {result.InternalWriteErrorMessage}",
                    "Test: saving failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (result.ExternalError is not null)
            {
                MessageBox.Show(this,
                    $"Wrote the test file successfully (this app sees it: {result.InternalExists}, {result.InternalSize} bytes), but couldn't run the outside check:\n{result.ExternalError}",
                    "Test: inconclusive", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (result.InternalExists && !result.ExternalSeesFile)
            {
                MessageBox.Show(this,
                    "ArchivSector-OD believes it saved the test file, but a separate Command Prompt checking the exact same place can't see it at all.\n\n" +
                    "Something is quietly keeping this app's files away from the real disk: most often a security program's sandbox for apps it doesn't recognise. Allow ArchivSector-OD in that program.\n\n" +
                    $"Test file: {result.TestFile}\n" +
                    $"This app sees it: yes ({result.InternalSize} bytes)\n" +
                    "Command Prompt sees it: no",
                    "Test: files are being hidden", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (result.InternalExists && result.ExternalSeesFile)
            {
                MessageBox.Show(this,
                    $"Saved a test file and a separate Command Prompt confirmed it's really there.\n\n{result.ConfigDir}\n\n" +
                    "Saving files works.",
                    "Test: saving works", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(this,
                    $"This app doesn't believe the file was saved, but no error came up.\n\n{result.ConfigDir}",
                    "Test: unexpected result", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
                // Asks first; on Yes it downloads, installs and restarts.
                await UpdateInstallService.OfferAndRunAsync(result, this);
            }
        }

        private void CreateShortcutButton_Click(object sender, RoutedEventArgs e)
        {
            var result = ShortcutService.CreateDesktopShortcut();
            if (result.Success) SetMessage($"✓ Shortcut created: {result.Path}", true);
            else SetMessage(result.ErrorMessage ?? "The shortcut couldn't be created.", false);
        }

        // ---- Browse buttons ----

        private void BrowseOutputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the output folder" };
            if (Directory.Exists(CleanPath(OutputFolderBox.Text))) dialog.InitialDirectory = CleanPath(OutputFolderBox.Text);
            if (dialog.ShowDialog(this) == true)
                OutputFolderBox.Text = dialog.FolderName;
        }

        private void BrowseMakeMkvButton_Click(object sender, RoutedEventArgs e) =>
            BrowseExe(MakeMkvPathBox, "Where is makemkvcon64.exe (in MakeMKV's folder)?", "MakeMKV (makemkvcon64.exe, makemkvcon.exe)|makemkvcon64.exe;makemkvcon.exe");

        private void BrowseRedumperButton_Click(object sender, RoutedEventArgs e) =>
            BrowseExe(RedumperPathBox, "Where is redumper.exe?", "Redumper (redumper.exe)|redumper.exe");

        private void BrowseImgBurnButton_Click(object sender, RoutedEventArgs e) =>
            BrowseExe(ImgBurnPathBox, "Where is ImgBurn.exe?", "ImgBurn (ImgBurn.exe)|ImgBurn.exe");

        private void BrowseMkvmergeButton_Click(object sender, RoutedEventArgs e) =>
            BrowseExe(MkvmergePathBox, "Where is mkvmerge.exe (in MKVToolNix's folder)?", "mkvmerge (mkvmerge.exe)|mkvmerge.exe");

        private void BrowseBurnerMaxButton_Click(object sender, RoutedEventArgs e) =>
            BrowseExe(BurnerMaxPathBox, "Where is BurnerMax.exe (the BurnerMAX Payload Tool)?", "BurnerMAX (BurnerMax.exe)|BurnerMax.exe");

        // The program's own name first, then any program (a renamed copy).
        private void BrowseExe(TextBox box, string title, string filter)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter + "|Programs (*.exe)|*.exe" };
            string current = CleanPath(box.Text);
            try
            {
                var dir = current.Length > 0 ? Path.GetDirectoryName(current) : null;
                if (dir is not null && Directory.Exists(dir)) dialog.InitialDirectory = dir;
            }
            catch { }
            if (dialog.ShowDialog(this) == true)
                box.Text = dialog.FileName;
        }

        // ---- Save / close ----

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            if (!int.TryParse(MaxConcurrentBox.Text.Trim(), out var max) || max < 1)
            {
                max = Math.Max(1, config.MaxConcurrentRips);
                MaxConcurrentBox.Text = max.ToString();
            }

            // Saved onto a fresh copy of the settings, so anything changed
            // elsewhere while this window was open (the Burn tab remembers
            // BurnerMAX's drives, read offsets...) isn't overwritten. The
            // copy the app passed in gets the same values.
            var fresh = ConfigService.Load();
            Apply(fresh, max);
            Apply(config, max);
            ConfigService.Save(fresh);

            dirty = false;
            SetMessage($"✓ Saved at {DateTime.Now:HH:mm}", true);
        }

        private void Apply(AppConfig c, int max)
        {
            c.OutputBaseFolder = CleanPath(OutputFolderBox.Text);
            c.MakeMkvPathOverride = CleanPath(MakeMkvPathBox.Text);
            c.RedumperPathOverride = CleanPath(RedumperPathBox.Text);
            c.ImgBurnPathOverride = CleanPath(ImgBurnPathBox.Text);
            c.MkvmergePathOverride = CleanPath(MkvmergePathBox.Text);
            c.BurnerMaxPath = CleanPath(BurnerMaxPathBox.Text);
            c.RawgApiKey = RawgKeyBox.Password.Trim();
            c.TmdbApiKey = TmdbKeyBox.Password.Trim();

            c.AutoCategorize = AutoCategorizeCheck.IsChecked ?? true;
            c.AutoEject = AutoEjectCheck.IsChecked ?? true;
            c.ToastNotifications = ToastCheck.IsChecked ?? true;
            c.SoundAlerts = SoundCheck.IsChecked ?? true;
            c.CheckForUpdatesOnStartup = StartupUpdateCheck.IsChecked ?? true;
            c.LiveLogOpensWithBurnTab = LiveLogCheck.IsChecked ?? true;
            c.DeleteIsoAfterBurn = DeleteIsoCheck.IsChecked ?? false;
            bool seasonal = SeasonalCheck.IsChecked ?? true;
            if (seasonal != seasonalAtOpen) c.AutoSeasonalTheme = seasonal;

            c.AutoFetchMetadataArt = AutoFetchArtCheck.IsChecked ?? true;
            c.BatchQueueMode = BatchQueueCheck.IsChecked ?? false;
            c.MultiDriveBatchSync = MultiDriveSyncCheck.IsChecked ?? false;
            c.MaxConcurrentRips = max;
        }

        // Windows' "Copy as path" wraps paths in quotes ("C:\...\tool.exe"),
        // and a quoted path never matches a real file, so the app would
        // quietly ignore it and fall back to auto-detect. Strip them.
        private static string CleanPath(string text) =>
            text.Trim().Trim('"').Trim();

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
