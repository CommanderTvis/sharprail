using System.Globalization;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Plugins;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.UI.Panels;

public sealed partial class SettingsWindow : Window
{
    private readonly WorkbenchWindow window;
    private readonly ProfileStore profile;
    private readonly SharedState state;
    private readonly LayoutSession layout;
    private readonly Action apply;
    private readonly Func<CancellationToken, Task<GitHubStatus>> gitHub;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ContentControl body;
    private readonly Dictionary<string, Button> navigation = [];
    private readonly StackPanel navigationList;
    private string pluginSectionsSignature = "";
    private string section = "Appearance";
    private string? renamingPreset;
    private readonly List<Action> refreshers = [];
    private readonly TextBlock error = new() { Name = "SettingsError", IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Danger };

    /// <summary>
    /// Settings for <paramref name="window"/>. Shared settings round-trip through the host and every
    /// open Settings view re-renders when the snapshot arrives; <paramref name="apply"/> follows local changes.
    /// </summary>
    public SettingsWindow(WorkbenchWindow window, Action apply, Func<CancellationToken, Task<GitHubStatus>>? gitHub = null, string section = "Appearance")
    {
        this.section = section;
        this.window = window; profile = window.Workbench.Profile; state = window.Workbench.State; layout = window.Layout; this.apply = apply;
        this.gitHub = gitHub ?? GitHubProbe.CheckAsync;
        Closed += (_, _) =>
        {
            lifetime.Cancel(); Ui.ThemeChanged -= SystemThemeChanged; state.Changed -= SharedChanged;
            window.Workbench.PluginRegistry.Changed -= PluginsChanged;
#if !ANDROID
            if (window.Workbench.Listener is { } listener) listener.Changed -= ListenerChanged;
#endif
        };
        Ui.ThemeChanged += SystemThemeChanged;
        state.Changed += SharedChanged;
        Name = "SettingsWindow";
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("SettingsBody")!;
        Opened += (_, _) =>
        {
            var zoom = InterfaceZoom.Current;
            MinWidth *= zoom; MinHeight *= zoom; Width *= zoom;
            if (Owner is Window owner) Height = Math.Max(MinHeight, owner.Bounds.Height * .8);
        };
        var close = this.FindControl<Button>("SettingsClose")!;
        close.Content = Ui.Icon("close");
        close.Click += (_, _) => Close();
        navigationList = this.FindControl<StackPanel>("SettingsNavigation")!;
        foreach (var item in new[] { ("Appearance", "palette"), ("Line width", "fileText"), ("Layout", "layout"), ("Projects", "folderTab"), ("Terminal", "terminal"), ("Notifications", "alertInfo"), ("Host", "terminal"), ("GitHub", "gitBranch"), ("Plugins", "puzzle") })
            Navigation(this.FindControl<Button>("Settings_" + item.Item1.Replace(' ', '_'))!, item.Item1, Ui.Row(item.Item2, item.Item1));
        SyncPluginSections();
        window.Workbench.PluginRegistry.Changed += PluginsChanged;
#if !ANDROID
        if (window.Workbench.Listener is { } listener) listener.Changed += ListenerChanged;
#endif
        var frame = this.FindControl<Border>("SettingsFrame")!;
        // On Android Settings is a page over the whole screen, not a card.
        if (OperatingSystem.IsAndroid()) { frame.CornerRadius = default; frame.BorderThickness = default; }
        else frame.SizeChanged += (_, args) => frame.Clip = new RectangleGeometry(new Rect(args.NewSize), 8, 8);
        KeyDown += (_, e) => { if (e.Key == Key.Escape || AppCommands.IsClose(e)) { Close(); e.Handled = true; } };
        if (window.Workbench.Compact) WireSectionDrawer();
        ShowSection(section);
    }

    public string Section => section;

    private void Navigation(Button button, string key, StackPanel content)
    {
        button.Content = content;
        content.Spacing = 8;
        button.Click += (_, _) => ShowSection(key);
        navigation[key] = button;
    }

    private void PluginsChanged(PluginTables tables)
    {
        if (tables.HasFlag(PluginTables.SettingsSections) || tables.HasFlag(PluginTables.Roster)) SyncPluginSections();
    }

    private static string SectionKey(PluginRow<SettingsSectionRegistration> row) => "plugin:" + row.PluginId + ":" + (row.Value.Id ?? row.PluginId);

    // Plugin sections follow core's; a section whose plugin leaves takes its page with it.
    private void SyncPluginSections()
    {
        var registry = window.Workbench.PluginRegistry;
        var sections = registry.SettingsSections;
        var signature = string.Join("\n", sections.Select(row => SectionKey(row) + "\t" + row.Value.Label + "\t" + row.Value.Icon));
        if (signature == pluginSectionsSignature) return;
        pluginSectionsSignature = signature;
        foreach (var key in navigation.Keys.Where(key => key.StartsWith("plugin:", StringComparison.Ordinal)).ToArray())
            navigation.Remove(key);
        // Plugin pages are grouped under Plugins, indented behind a rule, as the fork's settings navigation does.
        var group = navigationList.Children.OfType<Border>().FirstOrDefault(child => child.Name == "PluginSettingsNavigation");
        if (group is null)
        {
            group = new Border
            {
                Name = "PluginSettingsNavigation",
                Margin = new Thickness(16, 0, 0, 0),
                Padding = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = Ui.BorderBrush,
                Child = new StackPanel { Spacing = 2 }
            };
            navigationList.Children.Insert(navigationList.Children.IndexOf(navigation["Plugins"]) + 1, group);
        }
        var groupList = (StackPanel)group.Child!;
        groupList.Children.Clear();
        group.IsVisible = sections.Count > 0;
        foreach (var row in sections)
        {
            var key = SectionKey(row);
            var button = new Button { Name = "Settings_" + key.Replace(':', '_'), Classes = { "settings-navigation" } };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(PluginIcons.Resolve(row.Value.Icon, registry.Entry(row.PluginId), Ui.Muted));
            content.Children.Add(Ui.Text(row.Value.Label));
            Navigation(button, key, content);
            groupList.Children.Add(button);
        }
        if (section.StartsWith("plugin:", StringComparison.Ordinal) && !navigation.ContainsKey(section)) ShowSection("Plugins");
        else PaintNavigation();
    }

    private Control PluginSection(string key)
    {
        var row = window.Workbench.PluginRegistry.SettingsSections.FirstOrDefault(candidate => SectionKey(candidate) == key);
        if (row is null) return new StackPanel();
        var page = new StackPanel { Spacing = 8 };
        page.Children.Add(new TextBlock { Text = row.Value.Label, Classes = { "settings-heading" } });
        page.Children.Add(WorkbenchWindow.Mount(row.PluginId, row.Value.Create));
        return page;
    }

    private Action? closeSections;

    // On a phone-sized screen a section takes the whole page and the section list is a drawer from the left.
    private void WireSectionDrawer()
    {
        var columns = this.FindControl<Grid>("SettingsColumns")!;
        var pane = this.FindControl<Border>("SettingsNavigationPane")!;
        var scrim = this.FindControl<Border>("SettingsScrim")!;
        var menu = this.FindControl<Button>("SettingsMenu")!;
        columns.ColumnDefinitions[0].Width = new(0);
        Grid.SetColumnSpan(pane, 2);
        pane.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; pane.Width = 240; pane.ZIndex = 2; pane.IsVisible = false;
        void Show(bool open) => pane.IsVisible = scrim.IsVisible = open;
        closeSections = () => Show(false);
        menu.Content = Ui.Icon("layoutLeft");
        menu.IsVisible = true;
        menu.Click += (_, _) => Show(!pane.IsVisible);
        scrim.PointerPressed += (_, e) => { Show(false); e.Handled = true; };
        AddHandler(BackRequestedEvent, (_, e) => { if (pane.IsVisible) { Show(false); e.Handled = true; } });
    }

    public void ShowSection(string name)
    {
        closeSections?.Invoke();
        if (name.StartsWith("plugin:", StringComparison.Ordinal) && !navigation.ContainsKey(name)) name = "Plugins";
        section = name; refreshers.Clear();
        PaintNavigation();
        var page = name switch
        {
            "Line width" => LineWidth(),
            "Layout" => LayoutSettings(),
            "Projects" => ProjectSettings(),
            "Terminal" => TerminalSettings(),
            "Notifications" => NotificationSettings(),
            "Host" => ServingSettings(),
            "GitHub" => GitHubSettings(),
            "Plugins" => new PluginsSettings(state, window.Workbench.Plugins, this),
            _ when name.StartsWith("plugin:", StringComparison.Ordinal) => PluginSection(name),
            _ => Appearance()
        };
        (error.Parent as Panel)?.Children.Remove(error);
        if (page is Panel content) content.Children.Add(error);
        body.Content = page;
    }

    private void PaintNavigation()
    {
        foreach (var entry in navigation)
        {
            var active = entry.Key == section;
            entry.Value.Background = active ? Ui.PrimarySubtle : Ui.Elevated;
            foreach (var text in ((StackPanel)entry.Value.Content!).Children.OfType<TextBlock>()) text.Foreground = active ? Ui.Accent : Ui.Muted;
            if (((StackPanel)entry.Value.Content!).Children[0] is Border glyph) glyph.Background = active ? Ui.Accent : Ui.Muted;
        }
    }

    /// <summary>The device's appearance can change while Settings is open; keep the current system resolution accurate.</summary>
    private void SystemThemeChanged()
    {
        if (section == "Appearance" && state.Preferences.ThemeMode == "system") ShowSection(section);
    }

    private void SharedChanged(HostState previous, HostState next)
    {
        RequestedThemeVariant = (Owner as Window)?.RequestedThemeVariant;
        if (section == "Line width") foreach (var refresh in refreshers) refresh();
        else if (section is "Appearance" or "Layout" or "Projects" || section == "Terminal" && previous.Settings.TerminalReplayKb != next.Settings.TerminalReplayKb) ShowSection(section);
        else if (section == "Notifications") foreach (var refresh in refreshers) refresh();
    }

    /// <summary>Saves this app's own preferences and window-local layout choices.</summary>
    private void Save()
    {
        profile.Save(); apply();
        RequestedThemeVariant = (Owner as Window)?.RequestedThemeVariant;
    }

    /// <summary>Sends shared changes to the host; the view updates when the resulting snapshot arrives.</summary>
    private async void Share(params HostStateChange[] changes)
    {
        try { await state.ChangeAsync(changes); error.IsVisible = false; }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            error.Text = "The host could not save this change: " + failure.Message; error.IsVisible = true;
            ShowSection(section);
        }
    }

    // One theme mutation at a time: a second complete-pair write built from a stale sibling slot would
    // overwrite the first, so the theme controls are really disabled until the request settles.
    private bool themeChanging;

    private async void ShareTheme(params HostStateChange[] changes)
    {
        if (themeChanging) return;
        themeChanging = true; GateThemeControls();
        try { await state.ChangeAsync(changes); error.IsVisible = false; }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            error.Text = "The host could not save this change: " + failure.Message; error.IsVisible = true;
            ShowSection(section);
        }
        finally { themeChanging = false; GateThemeControls(); }
    }

    private void GateThemeControls()
    {
        if (section != "Appearance" || body.Content is not Control page) return;
        foreach (var name in new[] { "ThemeModeChoices", "ThemeChoices", "SystemThemes" })
            PageControl<StackPanel>(page, name).IsEnabled = !themeChanging;
    }

    private static string Setting(bool value) => value ? "true" : "false";

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
        var preferences = state.Preferences;
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
                ShareTheme(mode == "system"
                    ? [HostStateChange.Setting("theme-mode", mode), HostStateChange.Setting("system-light", pair.Light), HostStateChange.Setting("system-dark", pair.Dark)]
                    : [HostStateChange.Setting("theme-mode", mode)]);
            }));
        var themes = PageControl<StackPanel>(panel, "ThemeChoices");
        themes.IsVisible = !system;
        var fixedTheme = Themes.Resolve(preferences.Theme);
        foreach (var theme in Themes.All)
        {
            var choice = Choice("Theme_" + theme.Id, theme.Label, null, theme == fixedTheme, () =>
                ShareTheme(HostStateChange.Setting("theme", theme.Id), HostStateChange.Setting("theme-mode", "fixed")));
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
                    ShareTheme(HostStateChange.Setting("system-light", next.Light), HostStateChange.Setting("system-dark", next.Dark));
                });
            }
        }
        var size = PageControl<NumericUpDown>(panel, "InterfaceSize");
        size.Value = (decimal)state.Preferences.FontSize;
        size.ValueChanged += (_, _) => { if (size.Value is not null) { state.Preferences.FontSize = (double)size.Value; Save(); } };
        foreach (var name in new[] { "ThemeModeChoices", "ThemeChoices", "SystemThemes" })
            PageControl<StackPanel>(panel, name).IsEnabled = !themeChanging;
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
        Ui.FollowEnabled(trigger);
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
        var preferences = state.Preferences;
        PageControl<ContentControl>(panel, "FileLineWidthHost").Content = LineWidthControl("File",
            "Wraps source files and diffs in the editor font.",
            () => preferences.FileLineWidth, () => preferences.FileLineWidthBounded);
        PageControl<ContentControl>(panel, "MarkdownLineWidthHost").Content = LineWidthControl("Markdown",
            "Wraps rendered Markdown and rendered diffs, measured in the reading font.",
            () => preferences.MarkdownLineWidth, () => preferences.MarkdownLineWidthBounded);
        return panel;
    }

    // A draft input saved only by Save or Enter, as in the reference's line-width controls. Both the
    // width and the toggle show the host's value until its broadcast confirms a change.
    private Control LineWidthControl(string kind, string description, Func<int> value, Func<bool> bounded)
    {
        var key = kind.ToLowerInvariant();
        void setValue(int width) => Share(HostStateChange.Setting(key + "-width", width.ToString(CultureInfo.InvariantCulture)));
        var control = Page("LineWidthControl");
        control.Name = kind + "LineWidthControl";
        PageControl<TextBlock>(control, "LineWidthTitle").Text = kind;
        PageControl<TextBlock>(control, "LineWidthDescription").Text = description;
        var input = PageControl<TextBox>(control, "LineWidthInput");
        var save = PageControl<Button>(control, "LineWidthSave");
        var error = PageControl<TextBlock>(control, "LineWidthError");
        var limit = PageControl<Switch>(control, "LineWidthBounded");
        input.Name = kind + "LineWidthInput"; save.Name = kind + "LineWidthSave";
        error.Name = kind + "LineWidthError"; limit.Name = kind + "LineWidthBounded";
        AutomationProperties.SetName(input, kind + " line width");
        AutomationProperties.SetName(limit, "Limit " + kind.ToLowerInvariant() + " lines to this width");
        string Current() => value().ToString(CultureInfo.InvariantCulture);
        int? Parsed() => input.Text is { Length: > 0 and <= 3 } text && text.All(char.IsAsciiDigit) &&
            int.Parse(text, CultureInfo.InvariantCulture) is var number && LineWidths.IsValid(number) ? number : null;
        void Validate()
        {
            var parsed = Parsed();
            error.IsVisible = parsed is null;
            input.Classes.Set("invalid", parsed is null);
            save.IsEnabled = parsed is not null && parsed != value();
        }
        void Commit()
        {
            if (Parsed() is not { } parsed || parsed == value()) return;
            setValue(parsed); Validate();
        }
        input.Text = Current();
        input.TextChanged += (_, _) => Validate();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { input.Text = Current(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        save.Click += (_, _) => Commit();
        limit.IsChecked = bounded();
        limit.CheckedChange += requested =>
        {
            if (requested == bounded()) return;
            Share(HostStateChange.Setting(key + "-bounded", Setting(requested)));
            limit.IsChecked = bounded();
        };
        // A broadcast updates the controls in place: an untouched input follows the host, an edited draft is kept.
        var shown = Current();
        refreshers.Add(() =>
        {
            if (input.Text == shown) input.Text = Current();
            shown = Current();
            limit.IsChecked = bounded();
            Validate();
        });
        Validate();
        return control;
    }

    private Control LayoutSettings()
    {
        var panel = Page("LayoutPage");
        var presets = PageControl<StackPanel>(panel, "PresetChoices");
        var custom = state.Preferences.CustomPresets;
        var defaultPreset = custom.ContainsKey(window.Slot.DefaultPreset) || Builtin.Contains(window.Slot.DefaultPreset) ? window.Slot.DefaultPreset : "balanced";
        foreach (var name in Builtin.Concat(custom.Keys))
        {
            var row = new Grid { Name = "Preset_" + name, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 8, Tag = name == defaultPreset };
            if (renamingPreset == name && custom.ContainsKey(name))
            {
                var rename = new TextBox { Name = "PresetRenameInput", Text = name, MaxLength = 200, MinWidth = 180 };
                AutomationProperties.SetName(rename, "Rename " + name);
                var commit = Ui.Button("Save", () => RenamePreset(name, rename.Text));
                commit.Name = "PresetRenameSave";
                AutomationProperties.SetName(commit, $"Save {name} name");
                rename.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { RenamePreset(name, rename.Text); e.Handled = true; }
                    else if (e.Key == Key.Escape) { renamingPreset = null; ShowSection(section); e.Handled = true; }
                };
                Ui.Place(row, rename); Ui.Place(row, commit, 0, 1);
                presets.Children.Add(row);
                continue;
            }
            Ui.Place(row, Ui.Text(char.ToUpperInvariant(name[0]) + name[1..]));
            var makeDefault = Ui.Button(defaultPreset == name ? "Default" : "Set default", () =>
            { window.Slot.DefaultPreset = name; Save(); ShowSection(section); });
            Ui.Place(row, makeDefault, 0, 1);
            Ui.Place(row, Ui.Button("Apply now…", async () => await ApplyLayout(name)), 0, 2);
            if (custom.ContainsKey(name))
            {
                var rename = Ui.Button("Rename", () => { renamingPreset = name; ShowSection(section); });
                AutomationProperties.SetName(rename, "Rename " + name);
                rename.Name = "PresetRename";
                Ui.Place(row, rename, 0, 3);
                var delete = Ui.IconButton("trash", "Delete " + name, () =>
                {
                    if (window.Slot.DefaultPreset == name) { window.Slot.DefaultPreset = "balanced"; Save(); }
                    Share(HostStateChange.DeletePreset(name));
                });
                delete.Name = "PresetDelete";
                Ui.Place(row, delete, 0, 4);
            }
            presets.Children.Add(row);
        }
        var input = PageControl<TextBox>(panel, "PresetName");
        AutomationProperties.SetName(input, "Custom preset name");
        var savePreset = Ui.Button("Save current", () =>
        {
            var name = input.Text?.Trim() ?? "";
            if (name.Length == 0 || Builtin.Contains(name)) return;
            var frame = layout.State.Copy(); frame.Workspaces.Clear(); frame.ActiveWorkspace = "";
            Share(HostStateChange.SavePreset(name, SharedState.Serialize(frame)));
        });
        AutomationProperties.SetName(savePreset, "Save preset");
        PageControl<ContentControl>(panel, "SavePreset").Content = savePreset;
        var limits = PageControl<StackPanel>(panel, "GroupLimits");
        limits.Children.Add(Limit("side", layout.State.SideLimit, value => layout.Geometry(state => state.SideLimit = value)));
        limits.Children.Add(Limit("bottom", layout.State.BottomLimit, value => layout.Geometry(state => state.BottomLimit = value)));
        var alignment = PageControl<ComboBox>(panel, "BottomAlignment");
        alignment.ItemsSource = new[] { "center", "center-left", "center-right", "full" };
        alignment.SelectedItem = layout.State.BottomAlignment;
        alignment.SelectionChanged += (_, _) => { layout.Geometry(state => state.BottomAlignment = alignment.SelectedItem as string ?? "center"); Save(); };
        var vertical = PageControl<CheckBox>(panel, "VerticalCenterTabs");
        var inProjects = PageControl<CheckBox>(panel, "VerticalTabsInProjects");
        var directions = PageControl<StackPanel>(panel, "PaneDirections");
        var directionLabel = PageControl<TextBlock>(panel, "PaneDirectionLabel");
        var directionDetail = PageControl<TextBlock>(panel, "PaneDirectionDetail");
        // A workbench that keeps its tabs in Projects offers no other place for them.
        var fixedTabs = window.Workbench.TabsInProjects;
        PageControl<TextBlock>(panel, "EditorTabsLabel").IsVisible = PageControl<TextBlock>(panel, "EditorTabsDetail").IsVisible =
            vertical.IsVisible = inProjects.IsVisible = !fixedTabs;
        void RenderTabs()
        {
            var on = fixedTabs || state.Preferences.VerticalCenterTabs;
            vertical.IsChecked = on;
            inProjects.IsChecked = state.Preferences.VerticalTabsInProjects;
            inProjects.IsEnabled = on;
            directionLabel.Foreground = on ? Ui.TextBrush : Ui.Hint;
            directionDetail.Text = on
                ? "What a new pair looks like when two tabs are first shown together, from a tab's menu. A tab joining a pane that already exists follows that pane."
                : "Tabs are shown together only in the vertical strip, so this waits on the setting above.";
            directions.Children.Clear();
            foreach (var (direction, label) in new[] { ("horizontal", "Columns"), ("vertical", "Rows") })
            {
                var active = state.Preferences.DefaultPaneDirection == direction;
                var choice = new Button
                {
                    Name = "DefaultPane_" + direction,
                    Content = label,
                    IsEnabled = on,
                    Padding = new Thickness(12, 4),
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = active ? Ui.PrimaryMuted : Ui.BorderBrush,
                    Background = active ? Ui.PrimarySubtle : Avalonia.Media.Brushes.Transparent,
                    Foreground = active ? Ui.TextBrush : Ui.Muted
                };
                choice.Click += (_, _) => { state.Preferences.DefaultPaneDirection = direction; Save(); RenderTabs(); };
                directions.Children.Add(choice);
            }
        }
        RenderTabs();
        vertical.IsCheckedChanged += (_, _) =>
        {
            if (state.Preferences.VerticalCenterTabs == (vertical.IsChecked == true)) return;
            state.Preferences.VerticalCenterTabs = vertical.IsChecked == true; Save(); RenderTabs();
        };
        inProjects.IsCheckedChanged += (_, _) =>
        {
            if (state.Preferences.VerticalTabsInProjects == (inProjects.IsChecked == true)) return;
            state.Preferences.VerticalTabsInProjects = inProjects.IsChecked == true; Save(); RenderTabs();
        };
        var preview = PageControl<CheckBox>(panel, "PreviewTabs");
        preview.IsChecked = state.Preferences.PreviewTabs;
        preview.IsCheckedChanged += (_, _) => { state.Preferences.PreviewTabs = preview.IsChecked == true; Save(); };
        PageControl<ContentControl>(panel, "ResetFrame").Content = Ui.Button("Reset frame", async () => await ApplyLayout(defaultPreset), "refresh");
        return panel;
    }

    private static readonly string[] Builtin = ["balanced", "focus", "review"];

    private async void RenamePreset(string name, string? text)
    {
        var next = text?.Trim() ?? "";
        if (next.Length == 0 || Builtin.Contains(next)) return;
        renamingPreset = null;
        if (next == name) { ShowSection(section); return; }
        try { await state.ChangeAsync(HostStateChange.RenamePreset(name, next)); }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            error.Text = "The host could not save this change: " + failure.Message; error.IsVisible = true;
            ShowSection(section); return;
        }
        if (window.Slot.DefaultPreset == name) { window.Slot.DefaultPreset = next; Save(); }
    }

    private async Task ApplyLayout(string name)
    {
        if (!await Dialogs.Confirm(this, "Apply this layout?", "Open files, documents, and terminals are preserved, but their groups and proportions will be rearranged across every workspace in this window. Other windows are unaffected.", "Apply layout")) return;
        var preset = (state.Preferences.CustomPresets.GetValueOrDefault(name) ?? DockState.Preset(name)).Copy();
        preset.SideLimit = Math.Max(layout.State.SideLimit, Math.Max(preset.Groups.Count(group => group.Region == "left"), preset.Groups.Count(group => group.Region == "right")));
        preset.BottomLimit = Math.Max(layout.State.BottomLimit, preset.Groups.Count(group => group.Region == "bottom"));
        layout.ApplyPreset(preset);
        window.Slot.Layout = layout.State; Save();
    }

    private Control Limit(string region, int value, Action<int> change)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Ui.Text(region == "side" ? "Side groups" : "Bottom groups", size: 12));
        var input = new NumericUpDown
        {
            Name = "GroupLimit_" + region,
            Minimum = 1,
            Maximum = 32,
            Value = value,
            Width = 88,
            Height = 28,
            MinHeight = 28,
            FontSize = 13,
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };
        // A setting that rarely changes: the steppers stay small and muted, lighting only on hover. The spinner's
        // template sizes its buttons itself, so they are narrowed once it exists, leaving room for the digits.
        input.TemplateApplied += (_, outer) =>
        {
            if (outer.NameScope.Find<ButtonSpinner>("PART_Spinner") is not { } spinner) return;
            spinner.TemplateApplied += (_, inner) =>
            {
                foreach (var name in new[] { "PART_IncreaseButton", "PART_DecreaseButton" })
                    if (inner.NameScope.Find<RepeatButton>(name) is { } step) { step.Width = 22; step.MinWidth = 0; step.Padding = new Thickness(4, 0); }
            };
        };
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
        var recent = PageControl<StackPanel>(panel, "RecentProjects");
        foreach (var path in state.Current.Projects)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(row, Ui.Text(path, size: 12));
            Ui.Place(row, Ui.IconButton("close", "Remove from recent projects", () => Share(HostStateChange.ForgetProject(path))), 0, 1);
            recent.Children.Add(row);
        }
        return panel;
    }

    private Control TerminalSettings()
    {
        var panel = Page("TerminalPage");
        var choices = PageControl<StackPanel>(panel, "TerminalRendererChoices");
        foreach (var (renderer, label, description) in new[]
        {
            (Terminal.TerminalRenderers.Texture, "Metal texture", "Ghostty’s Metal output composed with the workbench, supporting its overlays and clipping."),
            (Terminal.TerminalRenderers.Skia, "Skia", "Draw terminal text with the workbench. Use when Metal textures are unavailable.")
        })
            choices.Children.Add(Choice("TerminalRenderer_" + renderer, label, description, state.Preferences.TerminalRenderer == renderer, () =>
            {
                if (state.Preferences.TerminalRenderer == renderer) return;
                state.Preferences.TerminalRenderer = renderer;
                Save();
                foreach (var open in window.Workbench.Windows) open.RestartTerminals();
                ShowSection(section);
            }));
        // Host state: the selection follows the host's broadcast, never the click.
        var replay = PageControl<StackPanel>(panel, "TerminalReplayChoices");
        var current = state.Current.Settings.TerminalReplayKb;
        foreach (var (kb, label, description) in new[]
        {
            (0, "Off", "Reattaching shows an empty screen over the live shell"),
            (16, "16 KB", "About a screenful"),
            (HostSettings.DefaultTerminalReplayKb, "64 KB", "A screenful plus scrollback (default)"),
            (256, "256 KB", "Long scrollback; more memory per terminal"),
            (HostSettings.MaxTerminalReplayKb, "1 MB", "Maximum")
        })
            replay.Children.Add(Choice("TerminalReplay_" + kb, label, description, current == kb, () =>
            {
                if (state.Current.Settings.TerminalReplayKb != kb) Share(HostStateChange.Setting("terminal-replay", kb.ToString(CultureInfo.InvariantCulture)));
            }));
        return panel;
    }

    private Control NotificationSettings()
    {
        var panel = Page("NotificationsPage");
        var notifier = window.Workbench.Notifier;
        // Host state: the switch follows the host's broadcast, never the click.
        var enabled = PageControl<Switch>(panel, "NotificationsEnabled");
        enabled.IsChecked = state.Current.Settings.NotificationsEnabled;
        enabled.CheckedChange += on =>
        {
            Share(HostStateChange.Setting("notifications", on ? "true" : "false"));
            // Turning it on is the user's gesture: the system asks for permission now, not at the first notification.
            if (on) notifier?.RequestPermission();
        };
        refreshers.Add(() => enabled.IsChecked = state.Current.Settings.NotificationsEnabled);
        PageControl<TextBlock>(panel, "NotificationsChannel").Text = notifier is null
            ? "Desktop notifications are not available on this platform."
            : "Your operating system delivers these notifications; allow or silence SharpRail in its notification settings.";
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