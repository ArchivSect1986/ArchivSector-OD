using System;
using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArchivSector_OD
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // v4.0 beta 4: no white flash at start-up. Before the page loads,
            // the window and the web view show the saved theme's own
            // background colour instead of WebView2's default white.
            var bg = StartBackground();
            Background = new System.Windows.Media.SolidColorBrush(bg);
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
            };
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            blazorWebView.Services = services.BuildServiceProvider();
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