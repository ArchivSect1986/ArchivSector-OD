using System.Windows;

namespace ArchivSector_OD
{
    public partial class TitlePickerWindow : Window
    {
        // The titles ticked, longest first (v4.0 beta 4: several, for a disc with
        // more than one film); null if cancelled.
        private readonly TaskCompletionSource<List<MakeMkvService.TitleInfo>?> tcs = new();
        public Task<List<MakeMkvService.TitleInfo>?> ResultTask => tcs.Task;

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
            // Beta 5: the theme picked in the 🎨 menu, followed while open.
            WpfTheme.Follow(this, WpfTheme.Apply(this));

            // Beta 6: a title MakeMKV gives no name (every title on a DVD) is
            // called "Title 1", "Title 2"... in the list's order, and the first
            // one, the longest, is marked as likely the film, as the text above
            // the list explains. It isn't given the film's own name: on a disc
            // with several long cuts, that could name the wrong one.
            var rows = titles.Select((t, i) => new TitleRow
            {
                Title = t,
                Name = (string.IsNullOrWhiteSpace(t.Name) ? $"Title {i + 1}" : t.Name.Trim())
                       + (i == 0 ? " · likely the film" : ""),
                Duration = FormatDuration(t.Seconds),
                SizeGb = $"{t.SizeBytes / (1024.0 * 1024 * 1024):F2} GB"
            }).ToList();

            TitleList.ItemsSource = rows;
            if (rows.Count > 0) TitleList.SelectedItems.Add(rows[0]);   // the longest, ticked to start with
            UpdateButton();
        }

        private void TitleList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateButton();

        private void UpdateButton()
        {
            int n = TitleList.SelectedItems.Count;
            ExtractButton.Content = n <= 1 ? "Extract 1 Title" : $"Extract {n} Titles";
            ExtractButton.IsEnabled = n > 0;
        }

        private static string FormatDuration(int totalSeconds)
        {
            var span = TimeSpan.FromSeconds(totalSeconds);
            return span.Hours > 0 ? $"{span.Hours}h {span.Minutes}m" : $"{span.Minutes}m {span.Seconds}s";
        }

        private void ExtractButton_Click(object sender, RoutedEventArgs e)
        {
            // In the list's order (longest first), not the order they were ticked.
            var picked = TitleList.Items.OfType<TitleRow>().Where(r => TitleList.SelectedItems.Contains(r)).Select(r => r.Title).ToList();
            if (picked.Count == 0) return;
            tcs.TrySetResult(picked);
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