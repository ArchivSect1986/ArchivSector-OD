using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ArchivSector_OD
{
    public partial class MetadataSearchWindow : Window
    {
        private readonly TaskCompletionSource<MetadataFetchService.MetadataResult?> tcs = new();
        public Task<MetadataFetchService.MetadataResult?> ResultTask => tcs.Task;

        private readonly AppConfig config;
        private string currentAttributionUrl = "";

        // Properties, not fields -- WPF's {Binding} only works against
        // real C# properties.
        private class ResultRow
        {
            public MetadataFetchService.MetadataResult Result { get; set; } = null!;
            public string TitleWithYear { get; set; } = "";
            public string Detail { get; set; } = "";
            public string ThumbnailUrl { get; set; } = "";
            public string Source { get; set; } = "";
        }

        public MetadataSearchWindow(string initialQuery, DiscCategory category, AppConfig config)
        {
            InitializeComponent();
            this.config = config;

            QueryBox.Text = initialQuery;

            var sources = category == DiscCategory.Game
                ? new[] { "RAWG" }
                : new[] { "TMDB" };
            SourceCombo.ItemsSource = sources;
            SourceCombo.SelectedIndex = 0;

            UpdateAttribution(sources[0]);

            Loaded += (_, _) => _ = RunSearch();
        }

        private void QueryBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) _ = RunSearch();
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e) => await RunSearch();

        private void SourceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var source = SourceCombo.SelectedItem as string ?? "";
            UpdateAttribution(source);
            if (IsLoaded) _ = RunSearch();
        }

        // Attribution required by each provider's terms of use --
        // RAWG requires crediting RAWG.io, TMDB requires the exact
        // "not endorsed or certified" wording.
        private void UpdateAttribution(string source)
        {
            string prefix, linkText, url;
            switch (source)
            {
                case "RAWG":
                    prefix = "Game data & artwork from ";
                    linkText = "RAWG.io";
                    url = "https://rawg.io";
                    break;
                case "TMDB":
                    prefix = "This product uses the TMDB API but is not endorsed or certified by ";
                    linkText = "TMDB";
                    url = "https://www.themoviedb.org";
                    break;
                default:
                    prefix = "";
                    linkText = "";
                    url = "";
                    break;
            }

            AttributionPrefixRun.Text = prefix;
            AttributionLinkRun.Text = linkText;
            currentAttributionUrl = url;
        }

        private void AttributionLink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentAttributionUrl)) return;
            try
            {
                Process.Start(new ProcessStartInfo(currentAttributionUrl) { UseShellExecute = true });
            }
            catch { /* best-effort -- no browser available or blocked, not worth surfacing an error for */ }
        }

        private async Task RunSearch()
        {
            var query = QueryBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            var source = SourceCombo.SelectedItem as string ?? "";
            UpdateAttribution(source);
            StatusText.Text = "Searching…";
            UseButton.IsEnabled = false;
            ResultsList.ItemsSource = null;

            try
            {
                var key = source == "RAWG" ? config.RawgApiKey : config.TmdbApiKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    StatusText.Text = $"No {source} API key set -- add a free one in Settings.";
                    return;
                }

                List<MetadataFetchService.MetadataResult> results = source switch
                {
                    "RAWG" => await MetadataFetchService.SearchRawg(query, config.RawgApiKey),
                    "TMDB" => await MetadataFetchService.SearchTmdb(query, config.TmdbApiKey),
                    _ => new List<MetadataFetchService.MetadataResult>(),
                };

                var rows = results.Select(r => new ResultRow
                {
                    Result = r,
                    TitleWithYear = string.IsNullOrEmpty(r.Year) ? r.Title : $"{r.Title} ({r.Year})",
                    Detail = r.Detail,
                    ThumbnailUrl = r.ThumbnailUrl,
                    Source = r.Source,
                }).ToList();

                ResultsList.ItemsSource = rows;
                StatusText.Text = rows.Count == 0 ? "No results found." : $"{rows.Count} result(s).";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Search failed: {ex.Message}";
            }
        }

        private void ResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UseButton.IsEnabled = ResultsList.SelectedItem is not null;
            if (ResultsList.SelectedItem is ResultRow row)
                UpdateAttribution(row.Source);
        }

        private void UseButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = (ResultsList.SelectedItem as ResultRow)?.Result;
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