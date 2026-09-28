using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI.Panels;

public sealed partial class SettingsWindow : Window
{
    private readonly ProfileStore profile;
    private readonly LayoutSession layout;
    private readonly Action apply;
    private readonly ContentControl body;
    private readonly Dictionary<string, Button> navigation = [];
    private string section = "Appearance";
    public SettingsWindow(ProfileStore profile, LayoutSession layout, Action apply)
    {
        this.profile = profile; this.layout = layout; this.apply = apply;
        Name = "SettingsWindow";
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("SettingsBody")!;
        Opened += (_, _) => { if (Owner is Window owner) Height = Math.Max(MinHeight, owner.Bounds.Height * .8); };
        var close = this.FindControl<Button>("SettingsClose")!;
        close.Content = Ui.Icon("close");
        close.Click += (_, _) => Close();
        close.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
        close.Resources["ButtonBackgroundPressed"] = Ui.Hover;
        foreach (var item in new[] { ("Appearance", "palette"), ("Line width", "fileText"), ("Layout", "layout"), ("Projects", "folderTab") })
        {
            var button = this.FindControl<Button>("Settings_" + item.Item1.Replace(' ', '_'))!;
            button.Content = Ui.Row(item.Item2, item.Item1);
            ((StackPanel)button.Content!).Spacing = 8;
            button.Click += (_, _) => ShowSection(item.Item1);
            navigation[item.Item1] = button;
            button.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
            button.Resources["ButtonBackgroundPressed"] = Ui.Hover;
        }
        var frame = this.FindControl<Border>("SettingsFrame")!;
        frame.SizeChanged += (_, args) => frame.Clip = new RectangleGeometry(new Rect(args.NewSize), 8, 8);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        ShowSection(section);
    }

    public void ShowSection(string name)
    {
        section = name;
        foreach (var entry in navigation)
        {
            var active = entry.Key == name;
            entry.Value.Background = active ? new SolidColorBrush(Color.FromArgb(26, Ui.Accent.Color.R, Ui.Accent.Color.G, Ui.Accent.Color.B)) : Ui.Elevated;
            foreach (var text in ((StackPanel)entry.Value.Content!).Children.OfType<TextBlock>()) text.Foreground = active ? Ui.Accent : Ui.Muted;
            ((Border)((StackPanel)entry.Value.Content!).Children[0]).Background = active ? Ui.Accent : Ui.Muted;
        }
        body.Content = name switch { "Line width" => LineWidth(), "Layout" => LayoutSettings(), "Projects" => ProjectSettings(), _ => Appearance() };
    }

    private void Save()
    {
        profile.Save(); apply();
        RequestedThemeVariant = (Owner as Window)?.RequestedThemeVariant;
    }

    private Control Page(string key)
    {
        var page = ((IDataTemplate)Resources[key]!).Build(null)!;
        foreach (var label in page.GetLogicalDescendants().OfType<TextBlock>().Where(text => text.Classes.Contains("settings-label")))
            label.FontSize = Ui.FontSize;
        return page;
    }

    private static T PageControl<T>(Control page, string name) where T : Control =>
        page.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private Control Appearance()
    {
        var panel = Page("AppearancePage");
        var themes = PageControl<StackPanel>(panel, "ThemeChoices");
        foreach (var theme in new[] { "dark", "light", "system" })
        {
            var button = Ui.Button(char.ToUpperInvariant(theme[0]) + theme[1..], () =>
            { profile.Data.Preferences.Theme = theme; Save(); ShowSection(section); });
            button.Padding = new(12, 8); button.Height = 36;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var active = profile.Data.Preferences.Theme == theme;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(row, Ui.Text(char.ToUpperInvariant(theme[0]) + theme[1..], active ? Ui.TextBrush : Ui.Muted));
            if (active) Ui.Place(row, Ui.Icon("check", Ui.Accent), 0, 1);
            button.Content = row;
            button.Background = active ? new SolidColorBrush(Color.FromArgb(26, Ui.Accent.Color.R, Ui.Accent.Color.G, Ui.Accent.Color.B)) : Ui.Elevated;
            button.BorderBrush = active ? Ui.PrimaryMuted : Ui.BorderBrush;
            button.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
            button.Resources["ButtonBackgroundPressed"] = Ui.Hover;
            button.Name = "Theme_" + theme;
            AutomationProperties.SetName(button, char.ToUpperInvariant(theme[0]) + theme[1..]);
            themes.Children.Add(button);
        }
        var size = PageControl<NumericUpDown>(panel, "InterfaceSize");
        size.Value = (decimal)profile.Data.Preferences.FontSize;
        size.ValueChanged += (_, _) => { if (size.Value is not null) { profile.Data.Preferences.FontSize = (double)size.Value; Save(); } };
        return panel;
    }

    private Control LineWidth()
    {
        var panel = Page("LineWidthPage");
        var bounded = PageControl<CheckBox>(panel, "BoundPreviewWidth");
        bounded.IsChecked = profile.Data.Preferences.BoundPreviewWidth;
        bounded.IsCheckedChanged += (_, _) => { profile.Data.Preferences.BoundPreviewWidth = bounded.IsChecked == true; Save(); };
        var width = PageControl<NumericUpDown>(panel, "PreviewWidth");
        width.Value = (decimal)profile.Data.Preferences.PreviewWidth;
        width.ValueChanged += (_, _) => { if (width.Value is not null) { profile.Data.Preferences.PreviewWidth = (double)width.Value; Save(); } };
        return panel;
    }

    private Control LayoutSettings()
    {
        var panel = Page("LayoutPage");
        var presets = PageControl<StackPanel>(panel, "PresetChoices");
        foreach (var name in new[] { "balanced", "focus", "review" }.Concat(profile.Data.Preferences.CustomPresets.Keys))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            Ui.Place(row, Ui.Button(char.ToUpperInvariant(name[0]) + name[1..], () =>
            {
                layout.ApplyPreset(profile.Data.Preferences.CustomPresets.TryGetValue(name, out var custom) ? custom : DockState.Preset(name));
                profile.Data.Layout = layout.State; Save();
            }, "layout"));
            var makeDefault = Ui.Button(profile.Data.Preferences.DefaultPreset == name ? "Default" : "Set default", () =>
            { profile.Data.Preferences.DefaultPreset = name; Save(); ShowSection(section); });
            Ui.Place(row, makeDefault, 0, 1);
            if (profile.Data.Preferences.CustomPresets.ContainsKey(name))
                Ui.Place(row, Ui.IconButton("trash", "Delete preset", () =>
                {
                    profile.Data.Preferences.CustomPresets.Remove(name);
                    if (profile.Data.Preferences.DefaultPreset == name) profile.Data.Preferences.DefaultPreset = "balanced";
                    Save(); ShowSection(section);
                }), 0, 2);
            presets.Children.Add(row);
        }
        var input = PageControl<TextBox>(panel, "PresetName");
        PageControl<ContentControl>(panel, "SavePreset").Content = Ui.Button("Save current", () =>
        {
            var name = input.Text?.Trim() ?? "";
            if (name.Length == 0 || new[] { "balanced", "focus", "review" }.Contains(name)) return;
            var frame = layout.State.Copy(); frame.Workspaces.Clear(); frame.ActiveWorkspace = "";
            profile.Data.Preferences.CustomPresets[name] = frame; Save(); ShowSection(section);
        });
        var limits = PageControl<StackPanel>(panel, "GroupLimits");
        limits.Children.Add(Limit("Per side", layout.State.SideLimit, value => layout.Geometry(state => state.SideLimit = value)));
        limits.Children.Add(Limit("Bottom", layout.State.BottomLimit, value => layout.Geometry(state => state.BottomLimit = value)));
        var alignment = PageControl<ComboBox>(panel, "BottomAlignment");
        alignment.ItemsSource = new[] { "center", "center-left", "center-right", "full" };
        alignment.SelectedItem = layout.State.BottomAlignment;
        alignment.SelectionChanged += (_, _) => { layout.Geometry(state => state.BottomAlignment = alignment.SelectedItem as string ?? "center"); Save(); };
        PageControl<ContentControl>(panel, "ResetFrame").Content = Ui.Button("Reset frame", () =>
        {
            var name = profile.Data.Preferences.DefaultPreset;
            layout.ApplyPreset(profile.Data.Preferences.CustomPresets.GetValueOrDefault(name) ?? DockState.Preset(name)); Save();
        }, "refresh");
        return panel;
    }

    private Control Limit(string label, int value, Action<int> change)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Ui.Text(label, size: 12));
        var input = new NumericUpDown { Minimum = 1, Maximum = 32, Value = value, Width = 150 };
        input.ValueChanged += (_, _) => { if (input.Value is not null) { change((int)input.Value); Save(); } };
        panel.Children.Add(input); return panel;
    }

    private Control ProjectSettings()
    {
        var panel = Page("ProjectsPage");
        var hidden = PageControl<CheckBox>(panel, "ShowHiddenFiles");
        hidden.IsChecked = profile.Data.Preferences.ShowHiddenFiles;
        hidden.IsCheckedChanged += (_, _) => { profile.Data.Preferences.ShowHiddenFiles = hidden.IsChecked == true; Save(); };
        var recent = PageControl<StackPanel>(panel, "RecentProjects");
        foreach (var path in profile.Data.Projects)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(row, Ui.Text(path, size: 12));
            Ui.Place(row, Ui.IconButton("close", "Remove from recent projects", () =>
            { profile.Data.Projects.Remove(path); Save(); ShowSection(section); }), 0, 1);
            recent.Children.Add(row);
        }
        return panel;
    }
}
