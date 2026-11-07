using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace ArchivSector_OD
{
    // The app's theme for its separate windows (v4.0 beta 5): Settings,
    // History, and any other window that calls Apply. The main window is a
    // web page styled by wwwroot/css/app.css; these windows are WPF, so the
    // colours are read from that same file ([data-theme="…"] { --bg-root: …; })
    // and put in the window's resources under short names:
    //   Bg, Panel, PanelAlt, Edge, Text, TextSec, Muted, Accent, OnAccent,
    //   Success, OnSuccess, Danger, OnDanger, Amber (brushes), Radius (corners).
    // XAML uses them with {DynamicResource Bg}, so a theme change shows at once.
    // Apply also adds a look for the standard controls (buttons, text boxes,
    // check boxes, drop-downs, tables) built from those colours, plus three button
    // styles: AccentButton, SaveButton and DangerButton.
    // Follow re-applies the theme when the window is clicked back into after
    // another theme was picked in the 🎨 menu.
    public static class WpfTheme
    {
        // Midnight, used until (or if) app.css can't be read.
        private static readonly (string Key, string Css, string Default)[] Colours =
        {
            ("Bg", "bg-root", "#0a1022"), ("Panel", "bg-panel", "#121a30"), ("PanelAlt", "bg-panel-alt", "#19233d"),
            ("Edge", "border", "#283456"), ("Text", "text-primary", "#e5e9f5"), ("TextSec", "text-secondary", "#a7b0c6"),
            ("Muted", "text-muted", "#8c96b0"), ("Accent", "accent", "#38bdf8"), ("OnAccent", "on-accent", "#03121c"),
            ("Success", "success", "#10b981"), ("OnSuccess", "on-success", "#042114"), ("Danger", "danger", "#ef4444"),
            ("OnDanger", "on-danger", "#1f0505"), ("Amber", "amber", "#f59e0b"),
        };

        /// <summary>The theme the app is showing (a holiday theme in its week, too).</summary>
        public static string CurrentId()
        {
            try { return AppShell.PickStartTheme(ConfigService.Load()); }
            catch { return "midnight"; }
        }

        /// <summary>
        /// Puts the current theme's colours, corner size and font in the
        /// window's resources. Returns the theme's id.
        /// </summary>
        public static string ApplyColours(Window w)
        {
            string id = CurrentId();
            var vars = ReadVars(id);
            foreach (var (key, css, fallback) in Colours)
            {
                var brush = ToBrush(vars.TryGetValue(css, out var v) ? v : fallback) ?? ToBrush(fallback)!;
                w.Resources[key] = brush;
            }
            double radius = 6;
            if (vars.TryGetValue("radius-sm", out var r)
                && double.TryParse(r.Replace("px", "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                radius = parsed;
            w.Resources["Radius"] = new CornerRadius(radius);

            // The first font in the list (WPF doesn't know "system-ui" or "sans-serif").
            string font = "Segoe UI";
            if (vars.TryGetValue("font-body", out var f))
            {
                string first = f.Split(',')[0].Trim().Trim('"', '\'');
                if (first.Length > 0) font = first;
            }
            try { w.FontFamily = new FontFamily(font + ", Segoe UI"); } catch { }
            w.SetResourceReference(Window.BackgroundProperty, "Bg");
            return id;
        }

        /// <summary>The theme's colours plus the shared look for standard controls.</summary>
        public static string Apply(Window w)
        {
            if (!w.Resources.MergedDictionaries.Any(d => d.Contains("DangerButton")))
            {
                try { w.Resources.MergedDictionaries.Add(Styles()); }
                catch { /* the window keeps the plain Windows look */ }
            }
            return ApplyColours(w);
        }

        /// <summary>
        /// Re-applies the theme when the window is activated after another
        /// theme was picked. after runs once the colours have changed.
        /// </summary>
        public static void Follow(Window w, string shownId, Action? after = null)
        {
            string shown = shownId;
            w.Activated += (_, _) =>
            {
                try
                {
                    string now = CurrentId();
                    if (now == shown) return;
                    shown = ApplyColours(w);
                    after?.Invoke();
                }
                catch { }
            };
        }

        // ---- app.css ----

        private static Dictionary<string, string> ReadVars(string id)
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = FindAppCss();
                if (path is null) return vars;
                string css = File.ReadAllText(path);
                int at = css.IndexOf($"[data-theme=\"{id}\"]", StringComparison.Ordinal);
                if (at < 0) return vars;
                int open = css.IndexOf('{', at), close = open < 0 ? -1 : css.IndexOf('}', open);
                if (open < 0 || close < 0) return vars;
                foreach (Match m in Regex.Matches(css.Substring(open + 1, close - open - 1), @"--([\w-]+)\s*:\s*([^;]+);"))
                    vars[m.Groups[1].Value] = m.Groups[2].Value.Trim();
            }
            catch { }
            return vars;
        }

        // app.css sits in wwwroot next to the .exe in a published build. Run
        // from Visual Studio, the page is served from the project's own
        // wwwroot instead (it isn't copied next to the .exe), so the folders
        // above the .exe are looked in too.
        private static string? FindAppCss()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int up = 0; dir is not null && up < 6; up++, dir = dir.Parent)
                {
                    var css = Path.Combine(dir.FullName, "wwwroot", "css", "app.css");
                    if (File.Exists(css)) return css;
                }
            }
            catch { }
            return null;
        }

        private static Brush? ToBrush(string value)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        // ---- The look for standard controls ----

        private static ResourceDictionary Styles() => (ResourceDictionary)XamlReader.Parse(StylesXaml);

        private const string StylesXaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Style TargetType="TextBlock">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
    </Style>

    <Style TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource PanelAlt}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Edge}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="Padding" Value="12,5"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{DynamicResource Radius}" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="Box" Property="Opacity" Value="0.8"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="Box" Property="Opacity" Value="0.5"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="AccentButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Background" Value="{DynamicResource Accent}"/>
        <Setter Property="Foreground" Value="{DynamicResource OnAccent}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Accent}"/>
    </Style>
    <Style x:Key="SaveButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Background" Value="{DynamicResource Success}"/>
        <Setter Property="Foreground" Value="{DynamicResource OnSuccess}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Success}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
    <Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
        <Setter Property="Background" Value="{DynamicResource Danger}"/>
        <Setter Property="Foreground" Value="{DynamicResource OnDanger}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Danger}"/>
    </Style>

    <Style TargetType="TextBox">
        <Setter Property="Background" Value="{DynamicResource Bg}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Edge}"/>
        <Setter Property="CaretBrush" Value="{DynamicResource Text}"/>
        <Setter Property="SelectionBrush" Value="{DynamicResource Accent}"/>
        <Setter Property="Padding" Value="6,4"/>
        <Setter Property="VerticalContentAlignment" Value="Center"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="{DynamicResource Radius}">
                        <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True">
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="CheckBox">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Cursor" Value="Hand"/>
    </Style>

    <!-- Drop-downs: the box and the list that opens, in the theme's colours. -->
    <Style TargetType="ComboBox">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="MinHeight" Value="28"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBox">
                    <Grid>
                        <ToggleButton x:Name="Toggle" Focusable="False" ClickMode="Press"
                                      IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                            <ToggleButton.Template>
                                <ControlTemplate TargetType="ToggleButton">
                                    <Border x:Name="Box" Background="{DynamicResource Bg}" BorderBrush="{DynamicResource Edge}" BorderThickness="1" CornerRadius="{DynamicResource Radius}">
                                        <TextBlock Text="▾" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0" Foreground="{DynamicResource Muted}"/>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property="IsMouseOver" Value="True">
                                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                                        </Trigger>
                                        <Trigger Property="IsChecked" Value="True">
                                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter IsHitTestVisible="False" Margin="10,4,26,4" VerticalAlignment="Center" HorizontalAlignment="Left"
                                          Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                          ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
                        <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}"
                               AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                            <Border Margin="0,3,0,0" Padding="0,4"
                                    MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                    MaxHeight="{TemplateBinding MaxDropDownHeight}"
                                    Background="{DynamicResource Panel}" BorderBrush="{DynamicResource Edge}" BorderThickness="1" CornerRadius="{DynamicResource Radius}">
                                <ScrollViewer VerticalScrollBarVisibility="Auto">
                                    <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                                </ScrollViewer>
                            </Border>
                        </Popup>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="ComboBoxItem">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ComboBoxItem">
                    <Border x:Name="Box" Background="Transparent" Padding="10,6">
                        <ContentPresenter/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHighlighted" Value="True">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource PanelAlt}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
                            <Setter Property="FontWeight" Value="SemiBold"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style TargetType="ScrollBar">
        <Setter Property="Background" Value="{DynamicResource Panel}"/>
    </Style>

    <!-- A list row: a line under each, the theme's highlight when hovered,
         and an accent bar on the left when picked. Lists ask for it with
         ItemContainerStyle="{DynamicResource ThemeListItem}". -->
    <Style x:Key="ThemeListItem" TargetType="ListBoxItem">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="Padding" Value="8,7"/>
        <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="ListBoxItem">
                    <Border x:Name="Box" Background="Transparent" BorderBrush="{DynamicResource Edge}" BorderThickness="0,0,0,1">
                        <DockPanel>
                            <Border x:Name="Bar" DockPanel.Dock="Left" Width="3" Background="Transparent"/>
                            <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
                        </DockPanel>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource PanelAlt}"/>
                        </Trigger>
                        <Trigger Property="IsSelected" Value="True">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource PanelAlt}"/>
                            <Setter TargetName="Bar" Property="Background" Value="{DynamicResource Accent}"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Tables -->
    <Style TargetType="DataGrid">
        <Setter Property="Background" Value="{DynamicResource Panel}"/>
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="BorderBrush" Value="{DynamicResource Edge}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="RowBackground" Value="{DynamicResource Panel}"/>
        <Setter Property="AlternatingRowBackground" Value="{DynamicResource PanelAlt}"/>
        <Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource Edge}"/>
        <Setter Property="VerticalGridLinesBrush" Value="{DynamicResource Edge}"/>
        <Setter Property="GridLinesVisibility" Value="Vertical"/>
        <Setter Property="HeadersVisibility" Value="Column"/>
        <Setter Property="RowHeight" Value="26"/>
    </Style>
    <Style TargetType="DataGridColumnHeader">
        <Setter Property="Foreground" Value="{DynamicResource Text}"/>
        <Setter Property="FontWeight" Value="SemiBold"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="DataGridColumnHeader">
                    <Grid>
                        <Border Background="{DynamicResource PanelAlt}" BorderBrush="{DynamicResource Edge}" BorderThickness="0,0,1,1" Padding="8,6">
                            <DockPanel>
                                <TextBlock x:Name="Arrow" DockPanel.Dock="Right" Margin="6,0,0,0" Foreground="{DynamicResource Accent}" VerticalAlignment="Center"/>
                                <ContentPresenter VerticalAlignment="Center"/>
                            </DockPanel>
                        </Border>
                        <Thumb x:Name="PART_RightHeaderGripper" HorizontalAlignment="Right" Width="6" Cursor="SizeWE">
                            <Thumb.Template>
                                <ControlTemplate TargetType="Thumb">
                                    <Border Background="Transparent"/>
                                </ControlTemplate>
                            </Thumb.Template>
                        </Thumb>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="SortDirection" Value="Ascending">
                            <Setter TargetName="Arrow" Property="Text" Value="▲"/>
                        </Trigger>
                        <Trigger Property="SortDirection" Value="Descending">
                            <Setter TargetName="Arrow" Property="Text" Value="▼"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="DataGridCell">
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="Padding" Value="6,0"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="DataGridCell">
                    <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}">
                        <ContentPresenter VerticalAlignment="Center"/>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property="IsSelected" Value="True">
                <Setter Property="Background" Value="{DynamicResource Accent}"/>
                <Setter Property="Foreground" Value="{DynamicResource OnAccent}"/>
            </Trigger>
        </Style.Triggers>
    </Style>
</ResourceDictionary>
""";
    }
}
