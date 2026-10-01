using System;
using System.IO;
using System.Windows;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace ArchivSector_OD
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

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
#if DEBUG
            services.AddBlazorWebViewDeveloperTools();
#endif
            blazorWebView.Services = services.BuildServiceProvider();
        }
    }
}