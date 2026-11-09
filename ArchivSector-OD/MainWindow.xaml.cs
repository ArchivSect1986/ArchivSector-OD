using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace ArchivSector_OD
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            // v4.0 beta 4: no white flash at start-up. Before the page loads,
            // the window and the web view show the saved theme's own
            // background colour instead of WebView2's default white.
            // Beta 5 (.NET 10): the BlazorWebView now uses WebView2's
            // composition control, which can show one white frame before its
            // first picture. So WebView2 is also told the colour through its
            // environment setting (read when it starts), and the web view
            // stays see-through, showing the window's theme colour, until
            // the page has loaded.
            var bg = StartBackground();
            try { Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", $"FF{bg.R:X2}{bg.G:X2}{bg.B:X2}"); }
            catch { /* cosmetic */ }

            InitializeComponent();

            Background = new System.Windows.Media.SolidColorBrush(bg);
            blazorWebView.Opacity = 0;
            void ShowPage() { if (blazorWebView.Opacity < 1) blazorWebView.Opacity = 1; }
            // In case the page never says it has loaded: show it anyway.
            var showAnyway = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            showAnyway.Tick += (_, _) => { showAnyway.Stop(); ShowPage(); };
            showAnyway.Start();
            blazorWebView.BlazorWebViewInitializing += (_, _) =>
            {
                try { blazorWebView.WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B); }
                catch { /* cosmetic */ }
            };

            // Eager config-folder creation, matching the Python app's own
            // startup behavior: it creates %AppData%\ArchivSector-OD up
            // front and shows a visible error if that fails, instead of
            // only discovering a permissions problem silently the first
            // time Settings tries to save.
            try
            {
                var configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ArchivSector-OD");
                Directory.CreateDirectory(configDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"ArchivSector-OD could not create its configuration folder:\n\n{ex.Message}\n\n" +
                    "Settings, History, and cached artwork may not save correctly. " +
                    "Check that you have write access to your AppData folder.",
                    "ArchivSector-OD — Startup Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            var services = new ServiceCollection();
            services.AddWpfBlazorWebView();

            // v3.3: errors inside a screen go to crash.log (CrashWatch.cs).
            services.AddSingleton<ILoggerFactory>(new CrashLoggerFactory());
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

            // v3.3: if the screen's browser view (WebView2) crashes or is
            // killed, record why.
            blazorWebView.BlazorWebViewInitialized += (_, e) =>
            {
                try
                {
                    e.WebView.CoreWebView2.ProcessFailed += (_, f) =>
                        CrashWatch.ScreenProcessFailed(f.ProcessFailedKind.ToString(), f.Reason.ToString(), f.ExitCode, f.ProcessDescription);
                }
                catch { }
                // v4.0 beta 4: files dropped from Explorer onto a drop zone
                // (index.html sends them with postMessageWithAdditionalObjects).
                try { e.WebView.CoreWebView2.WebMessageReceived += OnWebMessage; }
                catch { }
                // Beta 5: the page has loaded, so the web view can be seen.
                try { e.WebView.CoreWebView2.NavigationCompleted += (_, _) => Dispatcher.BeginInvoke(new Action(ShowPage)); }
                catch { ShowPage(); }
            };
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            blazorWebView.Services = services.BuildServiceProvider();
        }

        // Beta 5 (.NET 10): Windows can show the new window for a moment,
        // white, before WPF has drawn anything in it. The window is kept
        // hidden from the screen (DWM "cloak") until its first picture is
        // drawn, or for 3 seconds at most, so it appears already themed.
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_CLOAK = 13;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd;
            try { hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; }
            catch { return; }
            if (!SetCloak(hwnd, true)) return;
            bool shown = false;
            void Uncloak() { if (shown) return; shown = true; SetCloak(hwnd, false); }
            ContentRendered += (_, _) => Uncloak();
            var latest = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            latest.Tick += (_, _) => { latest.Stop(); Uncloak(); };
            latest.Start();
        }

        private static bool SetCloak(IntPtr hwnd, bool on)
        {
            try { int v = on ? 1 : 0; return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)) == 0; }
            catch { return false; }
        }

        // "archivsector-drop:<zone>" with the dropped files attached: their
        // paths go to the panel that owns the zone (FileDrop). Every other
        // message is Blazor's own and is left alone.
        private static void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            const string Prefix = "archivsector-drop:";
            string? msg;
            try { msg = e.TryGetWebMessageAsString(); } catch { return; }
            if (msg is null || !msg.StartsWith(Prefix, StringComparison.Ordinal)) return;
            var paths = new List<string>();
            try
            {
                if (e.AdditionalObjects is not null)
                    foreach (var o in e.AdditionalObjects)
                        if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path)) paths.Add(f.Path);
            }
            catch { return; }
            FileDrop.Raise(msg.Substring(Prefix.Length), paths);
        }

        // Each theme's page background (--bg-root in app.css), for the moment
        // before the page is there; anything unknown gets Midnight's.
        private static System.Windows.Media.Color StartBackground()
        {
            string id = "midnight";
            // The theme the app will actually start in (a holiday theme in its week, too).
            try { id = AppShell.PickStartTheme(ConfigService.Load()); } catch { }
            string hex = id switch
            {
                "nova" => "#070d1a", "console" => "#0c0e11", "daylight" => "#f2f3ef", "rack" => "#18191b",
                "library" => "#161412", "swiss" => "#ffffff", "table" => "#f4f4f4", "neon" => "#100820",
                "catalog" => "#dfe0d8", "kiosk" => "#000000", "halloween" => "#120a05", "remembrance" => "#111111",
                "christmas" => "#0d1f16", "canada" => "#fbf4f4", "july4" => "#0b1733",
                _ => "#0a1022",
            };
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }
    }
}