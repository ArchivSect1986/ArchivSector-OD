using System.Linq;
using System.Windows;

namespace ArchivSector_OD
{
    public partial class TrackPickerWindow : Window
    {
        public class TrackSelectionResult
        {
            public HashSet<string> ChosenAudioIds { get; set; } = new();
            public HashSet<string> ChosenSubIds { get; set; } = new();
            public string ChosenOutputName { get; set; } = "";
        }

        private readonly TaskCompletionSource<TrackSelectionResult?> tcs = new();
        public Task<TrackSelectionResult?> ResultTask => tcs.Task;

        private readonly string suggestedName;

        private class TrackRow
        {
            public MakeMkvService.TrackInfo Track { get; set; } = null!;
            public string DisplayText { get; set; } = "";
            public bool IsChecked { get; set; } = true;
        }

        private List<TrackRow> audioRows = new();
        private List<TrackRow> subRows = new();

        public TrackPickerWindow(List<MakeMkvService.TrackInfo> tracks, string suggestedName)
        {
            InitializeComponent();
            this.suggestedName = suggestedName;
            OutputNameBox.Text = suggestedName;

            audioRows = tracks.Where(t => t.Type == "Audio")
                .Select(t => new TrackRow { Track = t, DisplayText = BuildDisplay(t), IsChecked = true })
                .ToList();
            subRows = tracks.Where(t => t.Type == "Subtitles")
                .Select(t => new TrackRow { Track = t, DisplayText = BuildDisplay(t), IsChecked = true })
                .ToList();

            AudioList.ItemsSource = audioRows;
            SubList.ItemsSource = subRows;

            NoAudioText.Visibility = audioRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoSubText.Visibility = subRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string BuildDisplay(MakeMkvService.TrackInfo t)
        {
            if (!string.IsNullOrWhiteSpace(t.Descriptor)) return t.Descriptor;
            if (!string.IsNullOrWhiteSpace(t.LangName)) return t.LangName;
            if (!string.IsNullOrWhiteSpace(t.LangCode)) return t.LangCode;
            return "Unknown language";
        }

        private void ExtractButton_Click(object sender, RoutedEventArgs e)
        {
            var chosenName = FileNamingService.SanitizeFilename(
                string.IsNullOrWhiteSpace(OutputNameBox.Text) ? suggestedName : OutputNameBox.Text.Trim());

            var result = new TrackSelectionResult
            {
                ChosenAudioIds = audioRows.Where(r => r.IsChecked).Select(r => r.Track.StreamId).ToHashSet(),
                ChosenSubIds = subRows.Where(r => r.IsChecked).Select(r => r.Track.StreamId).ToHashSet(),
                ChosenOutputName = chosenName,
            };
            tcs.TrySetResult(result);
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