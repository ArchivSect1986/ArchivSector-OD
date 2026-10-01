using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ArchivSector_OD
{
    public partial class IsoVerifierWindow : Window
    {
        private string mode = ""; // "file" or "folder"
        private string selectedPath = "";
        private List<string> folderFiles = new();

        public IsoVerifierWindow()
        {
            InitializeComponent();
        }

        private void ResetResultViews()
        {
            StatusText.Text = "";
            ResultText.Text = "";
            ResultsBox.Visibility = Visibility.Collapsed;
            ResultsBox.Clear();
        }

        private void SelectFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select ISO File",
                Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            selectedPath = dialog.FileName;
            mode = "file";
            PathText.Text = selectedPath;
            ResetResultViews();
            VerifyButton.IsEnabled = true;
        }

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select Folder to Verify" };
            if (dialog.ShowDialog() != true) return;

            var folder = dialog.FolderName;
            List<string> found;
            try
            {
                found = Directory.EnumerateFiles(folder, "*.iso", SearchOption.AllDirectories).ToList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't scan that folder.\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            selectedPath = folder;
            mode = "folder";
            folderFiles = found;
            PathText.Text = $"{folder}\n({found.Count} ISO file(s) found)";
            ResetResultViews();
            VerifyButton.IsEnabled = found.Count > 0;
        }

        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            VerifyButton.IsEnabled = false;
            SelectFileButton.IsEnabled = false;
            SelectFolderButton.IsEnabled = false;
            ResetResultViews();

            if (mode == "file")
                await VerifyFile();
            else if (mode == "folder")
                await VerifyFolder();

            VerifyButton.IsEnabled = true;
            SelectFileButton.IsEnabled = true;
            SelectFolderButton.IsEnabled = true;
        }

        private async Task VerifyFile()
        {
            if (!File.Exists(selectedPath)) return;
            StatusText.Text = "Hashing file -- this can take a while for large ISOs...";

            (string? sha1, string? md5) result;
            try
            {
                result = await Task.Run(() => VerificationService.ComputeChecksums(selectedPath));
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                ResultText.Text = $"Error: {ex.Message}";
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                return;
            }

            if (result.sha1 is null)
            {
                StatusText.Text = "";
                ResultText.Text = "Couldn't compute checksums -- file not found?";
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
                return;
            }

            StatusText.Text = $"SHA1: {result.sha1}\nMD5: {result.md5}";

            var match = await Task.Run(() => VerificationService.VerifyAgainstRedump(result.sha1));
            if (match is not null)
            {
                ResultText.Text = $"✓ Verified: {match.Name}\n(matched against {match.SourceDat})";
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xb9, 0x81));
            }
            else if (!VerificationService.HasAnyDatImported())
            {
                ResultText.Text = "No DAT files imported -- see Settings to add one.";
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b));
            }
            else
            {
                ResultText.Text = "No match found in imported DAT files.\n(doesn't necessarily mean the dump is bad)";
                ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b));
            }
        }

        private async Task VerifyFolder()
        {
            ResultsBox.Visibility = Visibility.Visible;
            int total = folderFiles.Count;
            int matched = 0, unmatched = 0, errored = 0;

            for (int i = 0; i < total; i++)
            {
                var path = folderFiles[i];
                StatusText.Text = $"Verifying {i + 1} of {total}...";
                var name = Path.GetFileName(path);

                string line;
                try
                {
                    var (sha1, _) = await Task.Run(() => VerificationService.ComputeChecksums(path));
                    var match = sha1 is not null ? await Task.Run(() => VerificationService.VerifyAgainstRedump(sha1)) : null;
                    if (match is not null)
                    {
                        matched++;
                        line = $"[MATCH] {name} -> {match.Name}\n";
                    }
                    else
                    {
                        unmatched++;
                        line = $"[NO MATCH] {name}\n";
                    }
                }
                catch (Exception ex)
                {
                    errored++;
                    line = $"[ERROR] {name}: {ex.Message}\n";
                }

                ResultsBox.AppendText(line);
                ResultsBox.ScrollToEnd();
            }

            StatusText.Text = "";
            ResultText.Text = $"Done: {matched} matched, {unmatched} no match, {errored} error(s) out of {total} file(s).";
            ResultText.Foreground = (unmatched == 0 && errored == 0)
                ? new SolidColorBrush(Color.FromRgb(0x10, 0xb9, 0x81))
                : new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b));
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}