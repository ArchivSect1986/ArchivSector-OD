using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ArchivSector_OD
{
    // ⧉ The live log in its own window (v4.0 beta 5), opened from the Burn
    // tab's 📜 Live log with "⧉ Pop out". It shows the same lines
    // (LogService's last 1,000) as they happen, so a burn can be watched
    // from the Drive Bays tab, over another program, or on a second
    // monitor. One window at a time: Pop out again brings it to the front.
    // Built in code (no XAML file); colours match the app's dark look.
    public sealed class LiveLogWindow : Window
    {
        private static LiveLogWindow? open;

        /// <summary>Opens the live log window, or brings it to the front if it's already open.</summary>
        public static void ShowWindow()
        {
            if (open is not null)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            open = new LiveLogWindow();
            var owner = Application.Current?.MainWindow;
            if (owner is not null && owner.IsVisible) open.Owner = owner;
            open.Show();
        }

        // One line in the list.
        public sealed class Row
        {
            public long Id { get; init; }
            public string Text { get; init; } = "";
            public string Full { get; init; } = "";
            public Brush Colour { get; init; } = Brushes.White;
        }

        private static readonly Brush Back = Frozen("#0f1626"), Panel = Frozen("#141b2d"), Edge = Frozen("#2a3550"),
            Plain = Frozen("#e5e9f5"), Muted = Frozen("#8b93a8"), Red = Frozen("#ef4444"), Amber = Frozen("#f59e0b"), Green = Frozen("#10b981");
        private const int ShowAtMost = 1000;

        private readonly ObservableCollection<Row> rows = new();
        private readonly ListBox list;
        private readonly ComboBox driveBox;
        private readonly TextBlock status;
        private readonly DispatcherTimer timer;
        private bool dirty = true, rebuild = true;
        private long lastId, clearedThrough;
        private string? filter;            // a drive ("E:") or null for all

        private LiveLogWindow()
        {
            Title = "ArchivSector-OD - Live log";
            Width = 980; Height = 360; MinWidth = 520; MinHeight = 180;
            Background = Back;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try
            {
                var icon = Application.Current?.MainWindow?.Icon;
                if (icon is not null) Icon = icon;
            }
            catch { }

            // Top bar: drive filter, always on top, copy, open log, clear.
            var bar = new DockPanel { Background = Panel, LastChildFill = false };
            var title = new TextBlock { Text = "📜 Live log", Foreground = Plain, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(10, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            driveBox = new ComboBox { Width = 120, Margin = new Thickness(0, 6, 8, 6), VerticalAlignment = VerticalAlignment.Center };
            driveBox.Items.Add("All drives");
            driveBox.SelectedIndex = 0;
            driveBox.SelectionChanged += (_, _) =>
            {
                var pick = driveBox.SelectedItem as string;
                filter = pick is null || pick == "All drives" ? null : pick;
                rebuild = dirty = true;
            };
            var onTop = new CheckBox { Content = "Always on top", Foreground = Plain, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
            onTop.Checked += (_, _) => Topmost = true;
            onTop.Unchecked += (_, _) => Topmost = false;
            DockPanel.SetDock(title, Dock.Left); bar.Children.Add(title);
            DockPanel.SetDock(driveBox, Dock.Left); bar.Children.Add(driveBox);
            DockPanel.SetDock(onTop, Dock.Left); bar.Children.Add(onTop);
            var clear = MakeButton("🗑 Clear", "Empties this window only. The log file keeps everything.", (_, _) => { clearedThrough = lastId; rebuild = dirty = true; });
            var openLog = MakeButton("📄 Open log", "Opens today's log file, in easy-to-read blocks", (_, _) => LogService.OpenToday());
            var copy = MakeButton("📋 Copy", "Copies the lines shown (with the drive filter) to the clipboard", (_, _) => CopyShown());
            foreach (var b in new[] { clear, openLog, copy }) { DockPanel.SetDock(b, Dock.Right); bar.Children.Add(b); }

            // The lines. A template keeps the list virtualized (only the
            // lines on screen are drawn), so a long session stays quick.
            list = new ListBox
            {
                ItemsSource = rows,
                Background = Back,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                ItemTemplate = (DataTemplate)XamlReader.Parse(
                    "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                    "<TextBlock Text=\"{Binding Text}\" Foreground=\"{Binding Colour}\" ToolTip=\"{Binding Full}\" TextTrimming=\"CharacterEllipsis\"/>" +
                    "</DataTemplate>"),
            };
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.KeyDown += (_, e) => { if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CopyShown(); e.Handled = true; } };

            status = new TextBlock { Foreground = Muted, FontSize = 11, Margin = new Thickness(10, 3, 10, 4) };

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
            var line = new Border { Height = 1, Background = Edge };
            DockPanel.SetDock(line, Dock.Top); root.Children.Add(line);
            DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
            root.Children.Add(list);
            Content = root;

            // New lines are gathered and drawn at most four times a second.
            LogService.LiveChanged += OnLive;
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
            timer.Start();
            Closed += (_, _) =>
            {
                timer.Stop();
                LogService.LiveChanged -= OnLive;
                open = null;
            };
            Refresh();
        }

        private void OnLive() => dirty = true;   // any thread; the timer does the drawing

        private void Refresh()
        {
            if (!dirty) return;
            dirty = false;
            var all = LogService.LiveLines();

            // Drives seen so far, for the filter.
            foreach (var src in all.Select(l => l.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                if (!driveBox.Items.Contains(src)) driveBox.Items.Add(src);

            // Was the list at the bottom? Then it follows the new lines.
            var sv = FindScroll(list);
            bool atBottom = sv is null || sv.VerticalOffset >= sv.ScrollableHeight - 1;

            if (rebuild)
            {
                rows.Clear();
                lastId = clearedThrough;
                rebuild = false;
            }
            foreach (var l in all)
            {
                if (l.Id <= lastId) continue;
                lastId = l.Id;
                if (filter is not null && !string.Equals(l.Source, filter, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(MakeRow(l));
            }
            while (rows.Count > ShowAtMost) rows.RemoveAt(0);

            status.Text = $"{rows.Count:N0} line(s) shown{(filter is null ? "" : " for " + filter)} · the log file keeps everything · hover a line for all of it";
            if (atBottom && rows.Count > 0) list.ScrollIntoView(rows[^1]);
        }

        private static Row MakeRow(LogService.LiveLine l)
        {
            var parts = l.Text.Replace("\r", "").Split('\n');
            string first = parts[0] + (parts.Length > 1 ? $"   (+{parts.Length - 1} lines, hover to see)" : "");
            return new Row
            {
                Id = l.Id,
                Text = $"{l.Level} {l.Time:HH:mm:ss}  {l.Source,-6} {first}",
                Full = l.Text.Replace("\r", ""),
                Colour = l.Level == 'E' ? Red
                    : l.Level == 'W' ? Amber
                    : l.Text.Contains("every sector matches", StringComparison.OrdinalIgnoreCase)
                      || l.Text.Contains("verified", StringComparison.OrdinalIgnoreCase) && l.Text.Contains("match", StringComparison.OrdinalIgnoreCase)
                      ? Green
                    : Plain,
            };
        }

        private void CopyShown()
        {
            var sb = new StringBuilder();
            foreach (var r in rows) sb.AppendLine(r.Full.Contains('\n') ? r.Text.Split("   (+")[0] + Environment.NewLine + r.Full : r.Text);
            try { Clipboard.SetText(sb.Length == 0 ? " " : sb.ToString()); status.Text = "✓ Copied " + rows.Count.ToString("N0") + " line(s)"; }
            catch { status.Text = "The clipboard is busy: try Copy again."; }
        }

        private Button MakeButton(string text, string tip, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, ToolTip = tip,
                Margin = new Thickness(0, 6, 8, 6), Padding = new Thickness(10, 2, 10, 2),
                Background = Back, Foreground = Plain, BorderBrush = Edge,
            };
            b.Click += click;
            return b;
        }

        private static ScrollViewer? FindScroll(DependencyObject d)
        {
            if (d is ScrollViewer s) return s;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var found = FindScroll(VisualTreeHelper.GetChild(d, i));
                if (found is not null) return found;
            }
            return null;
        }

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
