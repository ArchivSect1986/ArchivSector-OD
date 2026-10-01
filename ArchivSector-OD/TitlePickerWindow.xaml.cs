using System.Windows;

namespace ArchivSector_OD
{
    public partial class TitlePickerWindow : Window
    {
        private readonly TaskCompletionSource<MakeMkvService.TitleInfo?> tcs = new();
        public Task<MakeMkvService.TitleInfo?> ResultTask => tcs.Task;

        private class TitleRow
        {
            public MakeMkvService.TitleInfo Title { get; set; } = null!;
            public string Name { get; set; } = "";
            public string Duration { get; set; } = "";
            public string SizeGb { get; set; } = "";
        }

        public TitlePickerWindow(List<MakeMkvService.TitleInfo> titles)
        {
            InitializeComponent();

            var rows = titles.Select(t => new TitleRow
            {
                Title = t,
                Name = string.IsNullOrWhiteSpace(t.Name) ? "(unnamed title)" : t.Name,
                Duration = FormatDuration(t.Seconds),
                SizeGb = $"{t.SizeBytes / (1024.0 * 1024 * 1024):F2} GB"
            }).ToList();

            TitleList.ItemsSource = rows;
            if (rows.Count > 0) TitleList.SelectedIndex = 0;
        }

        private static string FormatDuration(int totalSeconds)
        {
            var span = TimeSpan.FromSeconds(totalSeconds);
            return span.Hours > 0 ? $"{span.Hours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
        }

        private void ExtractButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = (TitleList.SelectedItem as TitleRow)?.Title;
            tcs.TrySetResult(selected);
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            tcs.TrySetResult(null);
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            tcs.TrySetResult(null);
            base.OnClosed(e);
        }
    }
}