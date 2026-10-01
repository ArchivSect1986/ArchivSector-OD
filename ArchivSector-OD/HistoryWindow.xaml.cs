using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ArchivSector_OD
{
    // Ported from the Python app's open_history(): a live filter box,
    // an "N of M records" count, a stats summary (computed over the
    // ENTIRE history, not the filtered view, same as Python), Export
    // Filtered to CSV, Open CSV File, Search Redump.org, and Clear
    // History with an explicit confirmation.
    //
    // Deliberate deviation: Python filters on Title / System / Serial ID
    // / Redump Match. This app's history CSV has no Serial ID column,
    // so Drive Model takes its place.
    public partial class HistoryWindow : Window
    {
        // Same path HistoryService writes to. Duplicated here so
        // HistoryService itself doesn't need to change.
        private static string HistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArchivSector-OD", "history.csv");

        private static readonly string[] SearchFields = { "Title", "System", "Drive Model", "Redump Match" };
        private static readonly Regex CapacityRegex = new(@"([\d.]+)\s*GB", RegexOptions.IgnoreCase);

        private List<string> header = new();
        private List<string[]> allRows = new();
        private List<string[]> currentFiltered = new();

        public HistoryWindow()
        {
            InitializeComponent();
            LoadHistory();
        }

        private void LoadHistory()
        {
            var rows = HistoryService.ReadAll();
            header = rows.Count > 0 ? rows[0].ToList() : new List<string>();
            allRows = rows.Skip(1).ToList();

            ComputeStats(allRows);
            ApplyFilter();
        }

        private string Field(string[] row, string name)
        {
            int idx = header.IndexOf(name);
            return idx >= 0 && idx < row.Length ? row[idx] : "";
        }

        // ---- Stats (ported from compute_stats) ----

        private void ComputeStats(List<string[]> rows)
        {
            if (rows.Count == 0)
            {
                StatsTotalsText.Text = "No archival records logged yet.";
                StatsBreakdownText.Text = "";
                return;
            }

            int total = rows.Count;
            int matched = 0, noMatch = 0, notApplicable = 0, notChecked = 0;
            double totalGb = 0;
            var systemCounts = new Dictionary<string, int>();

            foreach (var r in rows)
            {
                var rd = Field(r, "Redump Match").Trim();
                if (rd == "" || rd.Equals("Not checked", StringComparison.OrdinalIgnoreCase)
                    || rd.StartsWith("No DAT", StringComparison.OrdinalIgnoreCase))
                    notChecked++;
                else if (rd.StartsWith("No match", StringComparison.OrdinalIgnoreCase))
                    noMatch++;
                else if (rd.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
                    notApplicable++;
                else
                    matched++;

                var m = CapacityRegex.Match(Field(r, "Capacity"));
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb))
                    totalGb += gb;

                var sys = Field(r, "System").Trim();
                if (sys == "") sys = "Unknown";
                systemCounts[sys] = systemCounts.TryGetValue(sys, out var c) ? c + 1 : 1;
            }

            int checkable = matched + noMatch;
            string matchRate = checkable > 0 ? $"{(double)matched / checkable * 100:F0}%" : "N/A";

            StatsTotalsText.Text =
                $"Total Rips: {total}    |    Redump Match Rate: {matchRate} " +
                $"({matched} matched / {noMatch} no match / {notApplicable} n/a / {notChecked} not checked)    |    " +
                $"Total Archived: ~{totalGb:F1} GB";

            var breakdown = string.Join("  ·  ",
                systemCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}: {kv.Value}"));
            StatsBreakdownText.Text = $"By System:  {breakdown}";
        }

        // ---- Filter + render ----

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            var query = (FilterBox.Text ?? "").Trim().ToLowerInvariant();
            currentFiltered = query.Length == 0
                ? allRows
                : allRows.Where(r => SearchFields.Any(f => Field(r, f).ToLowerInvariant().Contains(query))).ToList();

            CountText.Text = $"{currentFiltered.Count} of {allRows.Count} record(s)";
            Render(currentFiltered);
        }

        private void Render(List<string[]> rows)
        {
            var table = new DataTable();

            if (header.Count == 0 || (allRows.Count == 0))
            {
                table.Columns.Add("Info");
                var r = table.NewRow();
                r["Info"] = "No archival records logged yet.";
                table.Rows.Add(r);
            }
            else if (rows.Count == 0)
            {
                table.Columns.Add("Info");
                var r = table.NewRow();
                r["Info"] = "No records match this filter.";
                table.Rows.Add(r);
            }
            else
            {
                foreach (var col in header)
                    table.Columns.Add(col);

                // Newest first, same as before.
                for (int i = rows.Count - 1; i >= 0; i--)
                {
                    var r = table.NewRow();
                    for (int c = 0; c < rows[i].Length && c < table.Columns.Count; c++)
                        r[c] = rows[i][c];
                    table.Rows.Add(r);
                }
            }

            HistoryGrid.ItemsSource = table.DefaultView;
        }

        // ---- Buttons ----

        private void RedumpButton_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("http://redump.org/discs/") { UseShellExecute = true }); }
            catch { /* no browser available -- nothing useful to report */ }
        }

        private void OpenCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(HistoryPath))
            {
                MessageBox.Show(this, "No history file exists yet.", "Open CSV File",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{HistoryPath}\"") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open the history file:\n{ex.Message}", "Open CSV File",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (currentFiltered.Count == 0)
            {
                MessageBox.Show(this, "There are no records in the current view to export.", "Nothing to Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "Export Filtered History to CSV",
                DefaultExt = ".csv",
                FileName = "ArchivSector_History_Export.csv",
                Filter = "CSV files (*.csv)|*.csv",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                using var writer = new StreamWriter(dlg.FileName, append: false, Encoding.UTF8);
                writer.WriteLine(string.Join(",", header.Select(CsvField)));
                foreach (var r in currentFiltered)
                    writer.WriteLine(string.Join(",", header.Select((_, i) => CsvField(i < r.Length ? r[i] : ""))));

                MessageBox.Show(this, $"Exported {currentFiltered.Count} record(s) to:\n{dlg.FileName}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't write the export file:\n{ex.Message}", "Export Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(HistoryPath) || allRows.Count == 0)
            {
                MessageBox.Show(this, "The archival history is already empty.", "Nothing to Clear",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"This will permanently delete all {allRows.Count} record(s) from the archival history log:\n\n" +
                $"{HistoryPath}\n\n" +
                "This cannot be undone. Consider using \"Export Filtered to CSV...\" first " +
                "if you want to keep a copy.\n\nClear the history now?",
                "Clear Archival History", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                File.Delete(HistoryPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't delete the history file:\n{ex.Message}", "Clear Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            FilterBox.Text = "";
            LoadHistory();
            MessageBox.Show(this, "The archival history log has been cleared.", "History Cleared",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private static string CsvField(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}