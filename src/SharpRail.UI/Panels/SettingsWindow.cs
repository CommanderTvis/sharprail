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
    private readonly Func<CancellationToken, Task<GitHubStatus>> gitHub;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ContentControl body;
    private readonly Dictionary<string, Button> navigation = [];
    private string section = "Appearance";
    public SettingsWindow(ProfileStore profile, LayoutSession layout, Action apply,
        Func<CancellationToken, Task<GitHubStatus>>? gitHub = null)
    {
        this.profile = profile; this.layout = layout; this.apply = apply;
        this.gitHub = gitHub ?? GitHubProbe.CheckAsync;
        Closed += (_, _) => { lifetime.Cancel(); Ui.ThemeChanged -= SystemThemeChanged; };
        Ui.ThemeChanged += SystemThemeChanged;
        Name = "SettingsWindow";
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("SettingsBody")!;
        Opened += (_, _) => { if (Owner is Window owner) Height = Math.Max(MinHeight, owner.Bounds.Height * .8); };
        var close = this.FindControl<Button>("SettingsClose")!;
        close.Content = Ui.Icon("close");
        close.Click += (_, _) => Close();
        close.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
        close.Resources["ButtonBackgroundPressed"] = Ui.Hover;
        foreach (var item in new[] { ("Appearance", "palette"), ("Line width", "fileText"), ("Layout", "layout"), ("Projects", "folderTab"), ("GitHub", "gitBranch") })
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
            entry.Value.Background = active ? Ui.PrimarySubtle : Ui.Elevated;
            foreach (var text in ((StackPanel)entry.Value.Content!).Children.OfType<TextBlock>()) text.Foreground = active ? Ui.Accent : Ui.Muted;
            ((Border)((StackPanel)entry.Value.Content!).Children[0]).Background = active ? Ui.Accent : Ui.Muted;
        }
        body.Content = name switch { "Line width" => LineWidth(), "Layout" => LayoutSettings(), "Projects" => ProjectSettings(), "GitHub" => GitHubSettings(), _ => Appearance() };
    }

    /// <summary>The device's appearance can change while Settings is open; keep the current system resolution accurate.</summary>
    private void SystemThemeChanged()
    {
        if (section == "Appearance" && profile.Data.Preferences.ThemeMode == "system") ShowSection(section);
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
        var preferences = profile.Data.Preferences;
        var system = preferences.ThemeMode == "system";
        var pair = preferences.SystemThemePair ?? Themes.DerivePair(preferences.Theme);
        var modes = PageControl<StackPanel>(panel, "ThemeModeChoices");
        foreach (var (mode, label, description) in new[]
        {
            ("fixed", "Fixed", "Use one theme everywhere."),
            ("system", "Match system", "Follow this device’s light or dark setting.")
        })
            modes.Children.Add(Choice("ThemeMode_" + mode, label, description, preferences.ThemeMode == mode, () =>
            {
                if (preferences.ThemeMode == mode) return;
                preferences.ThemeMode = mode;
                if (mode == "system") preferences.SystemThemePair = pair;
                Save(); ShowSection(section);
            }));
        var themes = PageControl<StackPanel>(panel, "ThemeChoices");
        themes.IsVisible = !system;
        var fixedTheme = Themes.Resolve(preferences.Theme);
        foreach (var theme in Themes.All)
        {
            var choice = Choice("Theme_" + theme.Id, theme.Label, null, theme == fixedTheme, () =>
            { preferences.Theme = theme.Id; preferences.ThemeMode = "fixed"; Save(); ShowSection(section); });
            choice.Tag = theme;
            themes.Children.Add(choice);
        }
        PageControl<StackPanel>(panel, "SystemThemes").IsVisible = system;
        if (system)
        {
            var systemPreferences = new Preferences { Theme = preferences.Theme, ThemeMode = "system", SystemThemePair = pair };
            var current = PageControl<StackPanel>(panel, "SystemThemeCurrentValue");
            var appearance = Themes.SystemAppearance;
            current.Children.Add(Ui.Text(appearance == "light" ? "Light" : "Dark", Ui.TextBrush));
            current.Children.Add(Ui.Icon("arrowRight", Ui.Muted, 14));
            current.Children.Add(Ui.Icon("palette", Ui.Muted, 14));
            current.Children.Add(Ui.Text(Themes.Resolve(systemPreferences, appearance).Theme.Label, Ui.TextBrush));
            foreach (var slot in new[] { "light", "dark" })
            {
                var resolution = Themes.Resolve(systemPreferences, slot);
                SystemThemeSelector(panel, slot, resolution, id =>
                {
                    var next = new SystemThemePair { Light = slot == "light" ? id : pair.Light, Dark = slot == "dark" ? id : pair.Dark };
                    if (next.Light == pair.Light && next.Dark == pair.Dark) return;
                    preferences.SystemThemePair = next; Save(); ShowSection(section);
                });
            }
        }
        var size = PageControl<NumericUpDown>(panel, "InterfaceSize");
        size.Value = (decimal)profile.Data.Preferences.FontSize;
        size.ValueChanged += (_, _) => { if (size.Value is not null) { profile.Data.Preferences.FontSize = (double)size.Value; Save(); } };
        return panel;
    }

    private static Button Choice(string name, string label, string? description, bool active, Action select)
    {
        var button = Ui.Button(label, select);
        button.Name = name;
        button.Padding = new(12, 8); button.MinHeight = 36;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        Ui.Place(row, Ui.Text(label, active || description is not null ? Ui.TextBrush : Ui.Muted));
        if (description is not null) Ui.Place(row, Ui.Text(description, Ui.Muted, 12), 1);
        if (active) { var check = Ui.Icon("check", Ui.Accent); Grid.SetRowSpan(check, 2); Ui.Place(row, check, 0, 1); }
        button.Content = row;
        button.Background = active ? Ui.PrimarySubtle : Ui.Elevated;
        button.BorderBrush = active ? Ui.PrimaryMuted : Ui.BorderBrush;
        button.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
        button.Resources["ButtonBackgroundPressed"] = Ui.Hover;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static void SystemThemeSelector(Control panel, string appearance, ThemeResolution resolution, Action<string> select)
    {
        var label = appearance == "light" ? "Light theme" : "Dark theme";
        var trigger = PageControl<Button>(panel, $"SystemTheme_{appearance}_trigger");
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Ui.Place(content, Ui.Text(resolution.Theme.Label, Ui.TextBrush));
        Ui.Place(content, Ui.Icon("arrowDown", Ui.Muted), 0, 1);
        trigger.Content = content;
        trigger.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
        trigger.Resources["ButtonBackgroundPressed"] = Ui.Hover;
        AutomationProperties.SetName(trigger, $"{label}: {resolution.Theme.Label}");
        var menu = new ContextMenu
        {
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Foreground = Ui.TextBrush,
            FontFamily = Ui.InterfaceFont,
            FontWeight = Ui.InterfaceWeight
        };
        menu.Resources["MenuFlyoutItemBackgroundPointerOver"] = Ui.Hover;
        menu.Resources["MenuFlyoutItemBackgroundPressed"] = Ui.Hover;
        menu.Resources["MenuFlyoutItemForegroundPointerOver"] = Ui.TextBrush;
        foreach (var theme in Themes.All.Where(theme => theme.Appearance == appearance))
        {
            var item = Ui.Menu(theme.Label, () => select(theme.Id));
            item.Name = $"SystemTheme_{appearance}_option_{theme.Id}";
            item.Foreground = Ui.TextBrush;
            item.Icon = theme == resolution.Theme ? Ui.Icon("check", Ui.Accent, 14) : null;
            menu.Items.Add(item);
        }
        trigger.ContextMenu = menu;
        trigger.Click += (_, _) => { menu.MinWidth = trigger.Bounds.Width; menu.Open(trigger); };
        var fallback = PageControl<TextBlock>(panel, $"SystemTheme_{appearance}_fallback");
        fallback.IsVisible = resolution.Fallback;
        fallback.Text = $"Configured theme unavailable in this app version. Using {resolution.Theme.Label}.";
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
            var row = new Grid { Name = "Preset_" + name, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
            Ui.Place(row, Ui.Text(char.ToUpperInvariant(name[0]) + name[1..]));
            var makeDefault = Ui.Button(profile.Data.Preferences.DefaultPreset == name ? "Default" : "Set default", () =>
            { profile.Data.Preferences.DefaultPreset = name; Save(); ShowSection(section); });
            Ui.Place(row, makeDefault, 0, 1);
            Ui.Place(row, Ui.Button("Apply now…", async () => await ApplyLayout(name)), 0, 2);
            if (profile.Data.Preferences.CustomPresets.ContainsKey(name))
                Ui.Place(row, Ui.IconButton("trash", "Delete preset", () =>
                {
                    profile.Data.Preferences.CustomPresets.Remove(name);
                    if (profile.Data.Preferences.DefaultPreset == name) profile.Data.Preferences.DefaultPreset = "balanced";
                    Save(); ShowSection(section);
                }), 0, 3);
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
        limits.Children.Add(Limit("side", layout.State.SideLimit, value => layout.Geometry(state => state.SideLimit = value)));
        limits.Children.Add(Limit("bottom", layout.State.BottomLimit, value => layout.Geometry(state => state.BottomLimit = value)));
        var alignment = PageControl<ComboBox>(panel, "BottomAlignment");
        alignment.ItemsSource = new[] { "center", "center-left", "center-right", "full" };
        alignment.SelectedItem = layout.State.BottomAlignment;
        alignment.SelectionChanged += (_, _) => { layout.Geometry(state => state.BottomAlignment = alignment.SelectedItem as string ?? "center"); Save(); };
        PageControl<ContentControl>(panel, "ResetFrame").Content = Ui.Button("Reset frame", async () => await ApplyLayout(profile.Data.Preferences.DefaultPreset), "refresh");
        return panel;
    }

    private async Task ApplyLayout(string name)
    {
        if (!await Dialogs.Confirm(this, "Apply this layout?", "Open files, documents, and terminals are preserved, but their groups and proportions will be rearranged across every workspace in this window. Other windows are unaffected.", "Apply layout")) return;
        var preset = (profile.Data.Preferences.CustomPresets.GetValueOrDefault(name) ?? DockState.Preset(name)).Copy();
        preset.SideLimit = Math.Max(layout.State.SideLimit, Math.Max(preset.Groups.Count(group => group.Region == "left"), preset.Groups.Count(group => group.Region == "right")));
        preset.BottomLimit = Math.Max(layout.State.BottomLimit, preset.Groups.Count(group => group.Region == "bottom"));
        layout.ApplyPreset(preset);
        profile.Data.Layout = layout.State; Save();
    }

    private Control Limit(string region, int value, Action<int> change)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Ui.Text(region == "side" ? "Side groups" : "Bottom groups", size: 12));
        var input = new NumericUpDown { Name = "GroupLimit_" + region, Minimum = 1, Maximum = 32, Value = value, Width = 96 };
        AutomationProperties.SetName(input, "Maximum " + region + " groups");
        var save = Ui.Button("Save", () =>
        {
            if (input.Value is not decimal next || next != decimal.Truncate(next) || next is < 1 or > 32) return;
            change((int)next); value = (int)next; Save(); ShowSection(section);
        });
        save.Name = "SaveGroupLimit_" + region;
        AutomationProperties.SetName(save, "Save " + region + " group limit");
        save.IsEnabled = false;
        input.ValueChanged += (_, _) => save.IsEnabled = input.Value is decimal next && next == decimal.Truncate(next) && next is >= 1 and <= 32 && next != value;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(input); row.Children.Add(save); panel.Children.Add(row); return panel;
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

    private Control GitHubSettings()
    {
        var panel = Page("GitHubPage");
        var status = PageControl<StackPanel>(panel, "GitHubStatus");
        var text = PageControl<TextBlock>(panel, "GitHubStatusText");
        var detail = PageControl<TextBlock>(panel, "GitHubStatusDetail");
        var refresh = Ui.Button("Refresh", () => { });
        refresh.Name = "GitHubRefresh";
        PageControl<ContentControl>(panel, "GitHubRefreshHost").Content = refresh;
        async Task Check()
        {
            refresh.IsEnabled = false;
            GitHubStatus result;
            try { result = await gitHub(lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { result = new(false, error.Message); }
            status.Tag = result.Connected;
            text.Text = result.Connected ? "Connected" : "Not connected";
            detail.Text = result.Detail;
            refresh.IsEnabled = true;
        }
        refresh.Click += async (_, _) => await Check();
        _ = Check();
        return panel;
    }
}
