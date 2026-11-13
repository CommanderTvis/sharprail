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

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Docking;
using Tereditor.UI.Plugins;
using Tereditor.UI.Rendering;
using Tereditor.UI.State;

namespace Tereditor.UI.Panels;

public sealed partial class SettingsView : UserControl
{
    private readonly WorkbenchWindow window;
    private readonly ProfileStore profile;
    private readonly SharedState state;
    private readonly LayoutSession layout;
    private readonly Action apply;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ContentControl body;
    private readonly Dictionary<string, Button> navigation = [];
    private readonly StackPanel navigationList;
    private string pluginSectionsSignature = "";
    private string section = "Appearance";
    private string? renamingPreset;
    private readonly List<Action> refreshers = [];
    private readonly TextBlock error = new() { Name = "SettingsError", IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Danger };

    // Section headings read as uppercase muted labels; Avalonia has no text transform, so the text itself is uppercased
    // (static XAML, plugin pages and headings set later alike) wherever the shared settings-heading class is on.
    static SettingsView() => TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => UppercaseHeading(block));

    private static void UppercaseHeading(TextBlock block)
    {
        if (!block.Classes.Contains("settings-heading") || block.Classes.Contains("verbatim") || block.Text is not { Length: > 0 } text) return;
        var upper = text.ToUpperInvariant();
        if (upper != text) block.Text = upper;
    }

    // Compiled XAML never uses the runtime loader; this public constructor only makes Avalonia register the view (AVLN3001).
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Obsolete("Construct SettingsView with its arguments.", true)]
    public SettingsView() => throw new System.NotSupportedException("SettingsView needs its constructor arguments.");

    /// <summary>
    /// Settings for <paramref name="window"/>. Shared settings round-trip through the host and every
    /// open Settings view re-renders when the snapshot arrives; <paramref name="apply"/> follows local changes.
    /// </summary>
    public SettingsView(WorkbenchWindow window, Action apply, string section = "Appearance")
    {
        this.section = section;
        this.window = window; profile = window.Workbench.Profile; state = window.Workbench.State; layout = window.Layout; this.apply = apply;
        Closed += (_, _) =>
        {
            lifetime.Cancel(); Ui.ThemeChanged -= SystemThemeChanged; state.Changed -= SharedChanged; state.HandshakeChanged -= CapabilitiesChanged;
            window.Workbench.PluginRegistry.Changed -= PluginsChanged;
            Tereditor.Plugins.UI.Kit.Editor.CustomHighlighting.Changed -= HighlightingChanged;
#if !ANDROID && !BROWSER
            if (window.Workbench.Listener is { } listener) listener.Changed -= ListenerChanged;
#endif
        };
        Ui.ThemeChanged += SystemThemeChanged;
        Tereditor.Plugins.UI.Kit.Editor.CustomHighlighting.Changed += HighlightingChanged;
        state.Changed += SharedChanged;
        state.HandshakeChanged += CapabilitiesChanged;
        Name = "SettingsView";
        Focusable = true;
        AvaloniaXamlLoader.Load(this);
        body = this.FindControl<ContentControl>("SettingsBody")!;
        var close = this.FindControl<Button>("SettingsClose")!;
        close.Content = Ui.Icon("close");
        close.Click += (_, _) => Close();
        navigationList = this.FindControl<StackPanel>("SettingsNavigation")!;
        foreach (var item in new[] { ("Appearance", "palette"), ("Highlighting", ""), ("Code Intelligence", "code"), ("Editor", ""), ("Layout", "layout"), ("Keymap", "code"), ("Projects", "folderTab"), ("Terminal", "terminal"), ("Notifications", "alertInfo"), ("Host", "server"), ("Plugins", "puzzle"), ("About", "alertInfo") })
            Navigation(this.FindControl<Button>("Settings_" + item.Item1.Replace(' ', '_'))!, item.Item1, item.Item2.Length == 0 ? IndentedLabel(item.Item1) : Ui.Row(item.Item2, item.Item1));
        SyncPluginSections();
        navigation["Code Intelligence"].IsVisible = LanguageSupport.Available;
        window.Workbench.PluginRegistry.Changed += PluginsChanged;
#if !ANDROID && !BROWSER
        if (window.Workbench.Listener is { } listener) listener.Changed += ListenerChanged;
#endif
        var frame = this.FindControl<Border>("SettingsFrame")!;
        // On Android Settings is a page over the whole screen, not a card.
        if (OperatingSystem.IsAndroid()) { frame.CornerRadius = default; frame.BorderThickness = default; }
        KeyDown += (_, e) => { if (e.Key == Key.Escape || AppCommands.IsClose(e)) { Close(); e.Handled = true; } };
        ShowSection(section);
        if (window.Workbench.Compact) WireSectionList();
    }

    public string Section => section;

    /// <summary>The card's size in a window of <paramref name="available"/> size: 832 wide, 80% tall (at least 440), never past the window.</summary>
    internal static Size FitSize(Size available) =>
        new(Math.Min(832, available.Width), Math.Min(Math.Max(440, available.Height * .8), available.Height));

    public event EventHandler? Closed;

    /// <summary>Closes Settings; the owner removes it from the overlay on <see cref="Closed"/>. Closing twice is harmless.</summary>
    public void Close()
    {
        if (closing) return;
        closing = true;
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private bool closing;

    /// <summary>The system Back gesture: from a section on a phone it returns to the list, from the list it closes.</summary>
    internal void GoBack()
    {
        if (showSectionList is not null && !this.FindControl<Border>("SettingsNavigationPane")!.IsVisible) showSectionList(true);
        else Close();
    }

    // No icon: the label keeps the column the other rows' labels start at.
    private static StackPanel IndentedLabel(string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Text(label));
        row.Margin = new Thickness(PluginsLabelColumn - 12, 0, 0, 0);
        return row;
    }

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
        if (tables.HasFlag(PluginTables.Roster)) navigation["Code Intelligence"].IsVisible = LanguageSupport.Available;
        if (tables.HasFlag(PluginTables.KeymapActions) || tables.HasFlag(PluginTables.Active) || tables.HasFlag(PluginTables.Roster)) RefreshKeymapList();
        if (section == "Highlighting" && (tables.HasFlag(PluginTables.Launchers) || tables.HasFlag(PluginTables.Roster))) RefreshHighlightingAgents();
    }

    private void HighlightingChanged()
    {
        if (section == "Highlighting" && body.Content is Control panel) PopulateHighlightingGrammars(panel);
    }

    private static string SectionKey(PluginRow<SettingsSectionRegistration> row) => "plugin:" + row.PluginId + ":" + (row.Value.Id ?? row.PluginId);

    // Navigation buttons pad 12 px; the icon is Ui.Icon's default 16 px and the label follows at the content spacing of 8.
    private const double PluginsIconCentre = 12 + 16 / 2.0;
    private const double PluginsLabelColumn = 12 + 16 + 8;

    // Plugins whose pages configure the app itself, not an add-on: they are listed with the core sections, not under Plugins.
    private static readonly HashSet<string> CoreSectionPlugins = ["providers"];
    private readonly List<Button> coreSectionButtons = [];

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
        foreach (var old in coreSectionButtons) navigationList.Children.Remove(old);
        coreSectionButtons.Clear();
        // Plugin pages are grouped under Plugins, indented behind a rule, as the fork's settings navigation does.
        var group = navigationList.Children.OfType<Grid>().FirstOrDefault(child => child.Name == "PluginSettingsNavigation");
        if (group is null)
        {
            // The rule drops from the centre of the Plugins icon (button padding plus half the 16 px icon, less half the 1 px line)
            // and stops at the last page's vertical centre (half the 36 px button); the pages' icons sit in the Plugins label column.
            group = new Grid { Name = "PluginSettingsNavigation" };
            group.Children.Add(new Border
            {
                Name = "PluginSettingsGuide",
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(PluginsIconCentre - .5, 0, 0, 18),
                Background = Ui.BorderBrush
            });
            group.Children.Add(new StackPanel { Name = "PluginSettingsList", Spacing = 2, Margin = new Thickness(PluginsLabelColumn - 12, 0, 0, 0) });
            navigationList.Children.Insert(navigationList.Children.IndexOf(navigation["Plugins"]) + 1, group);
        }
        var groupList = (StackPanel)group.Children[1];
        groupList.Children.Clear();
        group.IsVisible = sections.Any(row => !CoreSectionPlugins.Contains(row.PluginId));
        foreach (var row in sections)
        {
            var key = SectionKey(row);
            var button = new Button { Name = "Settings_" + key.Replace(':', '_'), Classes = { "settings-navigation" } };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(PluginIcons.Resolve(row.Value.Icon, registry.Entry(row.PluginId), Ui.Muted));
            content.Children.Add(Ui.Text(row.Value.Label));
            Navigation(button, key, content);
            if (CoreSectionPlugins.Contains(row.PluginId))
            {
                navigationList.Children.Insert(navigationList.Children.IndexOf(navigation["Plugins"]), button);
                coreSectionButtons.Add(button);
            }
            else groupList.Children.Add(button);
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

    private Action<bool>? showSectionList;

    private static readonly Dictionary<string, string> SectionSummaries = new()
    {
        ["Appearance"] = "Theme, file icons, interface font size",
        ["Code Intelligence"] = "Language servers and launchers",
        ["Editor"] = "Font, wrapping, display",
        ["Layout"] = "Presets, panes, file previews",
        ["Keymap"] = "Shortcuts and keymap presets",
        ["Projects"] = "Recent projects",
        ["Terminal"] = OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser() ? "Replayed output" : "Renderer, replayed output",
        ["Notifications"] = "When an agent needs you",
        ["Host"] = "The host this app works with",
        ["About"] = "Version, runtime, licenses",
        ["Plugins"] = "Installed plugins and their settings"
    };

    // On a phone-sized screen Settings is a list of sections; choosing one gives it the whole page, and Back
    // or the header's arrow returns to the list.
    private void WireSectionList()
    {
        var columns = this.FindControl<Grid>("SettingsColumns")!;
        var pane = this.FindControl<Border>("SettingsNavigationPane")!;
        var back = this.FindControl<Button>("SettingsBack")!;
        var title = this.FindControl<TextBlock>("SettingsTitle")!;
        pane.BorderThickness = default;
        foreach (var (key, button) in navigation)
        {
            if (!SectionSummaries.TryGetValue(key, out var summary) || button.Content is not StackPanel row) continue;
            var name = row.Children.OfType<TextBlock>().First();
            row.Children.Remove(name);
            Typography.Apply(name, Typography.TitleSection); name.Foreground = Ui.TextBrush;
            row.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Children = { name, Ui.Text(summary, Ui.Muted, 12) } });
            row.Spacing = 16;
            button.Height = 60;
        }
        void Show(bool list)
        {
            columns.ColumnDefinitions[0].Width = list ? new(1, GridUnitType.Star) : new(0);
            columns.ColumnDefinitions[1].Width = list ? new(0) : new(1, GridUnitType.Star);
            pane.IsVisible = list; back.IsVisible = !list;
            title.Text = list ? "Settings" : section.StartsWith("plugin:", StringComparison.Ordinal) ? "Plugin settings" : section;
        }
        showSectionList = Show;
        PaintNavigation();
        back.Content = Ui.Icon("arrowGoBack");
        back.Click += (_, _) => Show(true);
        Show(true);
    }

    public void ShowSection(string name)
    {
        if (name.StartsWith("plugin:", StringComparison.Ordinal) && !navigation.ContainsKey(name)) name = "Plugins";
        section = name; refreshers.Clear();
        PaintNavigation();
        var page = name switch
        {
            "Editor" => EditorSettings(),
            "Highlighting" => HighlightingSettings(),
            "Code Intelligence" => LspSettings(),
            "Layout" => LayoutSettings(),
            "Keymap" => KeymapSettings(),
            "Projects" => ProjectSettings(),
            "Terminal" => TerminalSettings(),
            "Notifications" => NotificationSettings(),
            "Host" => ServingSettings(),
            "About" => new AboutFields { Margin = new(24) },
            "Plugins" => new PluginsSettings(state, window.Workbench.Plugins, window),
            _ when name.StartsWith("plugin:", StringComparison.Ordinal) => PluginSection(name),
            _ => Appearance()
        };
        (error.Parent as Panel)?.Children.Remove(error);
        if (page is Panel content) content.Children.Add(error);
        body.Content = page;
        foreach (var heading in page.GetLogicalDescendants().OfType<TextBlock>()) UppercaseHeading(heading);
        showSectionList?.Invoke(false);
    }

    /// <summary>
    /// Redraws the current page for a change the user did not make here (a host broadcast, the system theme, a failed save):
    /// on a phone the section list stays in front when it is showing, since only choosing a section leaves it.
    /// </summary>
    private void RefreshSection()
    {
        var listing = showSectionList is not null && this.FindControl<Border>("SettingsNavigationPane")!.IsVisible;
        ShowSection(section);
        if (listing) showSectionList!(true);
    }

    private void PaintNavigation()
    {
        foreach (var entry in navigation)
        {
            var active = showSectionList is null && entry.Key == section;
            entry.Value.Background = active ? Ui.PrimarySubtle : Ui.Elevated;
            foreach (var text in ((StackPanel)entry.Value.Content!).Children.OfType<TextBlock>()) text.Foreground = active ? Ui.Accent : Ui.Muted;
            // Only a drawn icon (its glyph is the mask of this border) takes the row colour; a plugin without an icon keeps an empty slot, not a filled square.
            if (((StackPanel)entry.Value.Content!).Children[0] is Border { OpacityMask: not null } glyph) glyph.Background = active ? Ui.Accent : Ui.Muted;
        }
    }

    /// <summary>The device's appearance can change while Settings is open; keep the current system resolution accurate.</summary>
    private void SystemThemeChanged()
    {
        if (section == "Appearance" && state.Preferences.ThemeMode == "system") RefreshSection();
    }

    private void CapabilitiesChanged(HostHandshake? handshake)
    {
        if (section == "Appearance") foreach (var refresh in refreshers) refresh();
    }

    private void SharedChanged(HostState previous, HostState next)
    {
        // Terminal activity and other clients' workspace updates must not replace the open page.
        if (previous.Settings == next.Settings && previous.Presets.SequenceEqual(next.Presets) && previous.KeymapPresets.SequenceEqual(next.KeymapPresets) && previous.Projects.SequenceEqual(next.Projects)) return;
        if (section == "Code Intelligence" && (previous.Settings.JavaKotlinProvider != next.Settings.JavaKotlinProvider || previous.Settings.LanguageServers != next.Settings.LanguageServers || previous.Settings.LspEnabled != next.Settings.LspEnabled || previous.Settings.LanguageLimits != next.Settings.LanguageLimits || previous.Settings.LanguageTools != next.Settings.LanguageTools)) foreach (var refresh in refreshers) refresh();
        else if (section == "Editor") foreach (var refresh in refreshers) refresh();
        // A project joining the recents list (the window's own open lands after it mounts) leaves the other pages alone.
        else if (section == "Keymap") { EndKeymapRecording(); RefreshSection(); }
        else if (section == "Appearance" && previous.Settings != next.Settings || section == "Layout" && (previous.Settings != next.Settings || !previous.Presets.SequenceEqual(next.Presets)) ||
            section == "Projects" || section == "Terminal" && (previous.Settings.TerminalReplayKb != next.Settings.TerminalReplayKb || previous.Settings.TerminalKeepAfterExit != next.Settings.TerminalKeepAfterExit ||
                previous.Settings.TerminalUndoCloseSeconds != next.Settings.TerminalUndoCloseSeconds ||
                previous.Settings.ShellIntegrationEnabled != next.Settings.ShellIntegrationEnabled)) RefreshSection();
        else if (section is "Notifications" or "Terminal") foreach (var refresh in refreshers) refresh();
    }

    /// <summary>Saves this app's own preferences and window-local layout choices.</summary>
    private void Save()
    {
        profile.Save(); apply();
    }

    /// <summary>Sends shared changes to the host; the view updates when the resulting snapshot arrives.</summary>
    private async void Share(params HostStateChange[] changes)
    {
        try { await Task.Run(() => state.ChangeAsync(changes)); error.IsVisible = false; }
        catch (OperationCanceledException) { }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            error.Text = "The host could not save this change: " + failure.Message; error.IsVisible = true;
            RefreshSection();
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
            RefreshSection();
        }
        finally { themeChanging = false; GateThemeControls(); SyncThemeControls(); }
    }

    private string shownTheme = "";

    private string ThemeSignature()
    {
        var preferences = state.Preferences;
        var pair = preferences.SystemThemePair ?? Themes.DerivePair(preferences.Theme);
        return $"{preferences.ThemeMode}|{preferences.Theme}|{pair.Light}|{pair.Dark}";
    }

    /// <summary>The theme controls show the host's state: once a change settles, a page still drawn from older state is redrawn.</summary>
    private void SyncThemeControls()
    {
        if (section == "Appearance" && shownTheme != ThemeSignature()) RefreshSection();
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
        shownTheme = ThemeSignature();
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
        AccentSettings(panel);
        // Host state: the selection follows the host's broadcast, never the pick.
        var fileIcons = PageControl<ComboBox>(panel, "FileTypeIconChoices");
        var fileIconStyles = new[]
        {
            ("", "Plain: single-colour icons that follow the text colour"),
            ("colored", "Colored: the same icons tinted by file kind with the theme’s colours"),
            ("material", "Material: Material Icon Theme in its own colours"),
            ("none", "None: generic file glyphs only")
        };
        fileIcons.ItemsSource = fileIconStyles.Select(style => style.Item2).ToArray();
        var syncingFileIcons = false;
        string CurrentFileIcons() => state.Current.Settings.FileTypeIconsEnabled ? state.Current.Settings.FileTypeIconStyle : "none";
        void FileIconChoices()
        {
            syncingFileIcons = true;
            try { fileIcons.SelectedIndex = Array.FindIndex(fileIconStyles, style => style.Item1 == CurrentFileIcons()); }
            finally { syncingFileIcons = false; }
            fileIcons.IsEnabled = state.Supports(HostProtocol.FileTypeIconStyle);
        }
        FileIconChoices();
        fileIcons.SelectionChanged += (_, _) =>
        {
            if (syncingFileIcons || fileIcons.SelectedIndex < 0) return;
            var value = fileIconStyles[fileIcons.SelectedIndex].Item1;
            if (value != CurrentFileIcons())
            {
                if (value == "none") Share(HostStateChange.Setting("file-type-icons", "false"));
                else Share(HostStateChange.Setting("file-type-icons", "true"), HostStateChange.Setting("file-type-icon-style", value.Length == 0 ? "plain" : value));
            }
            // The selection follows the host, not the pick: put it back until the broadcast moves it.
            FileIconChoices();
        };
        refreshers.Add(FileIconChoices);
        WindowDecorationSettings(panel);
        var size = PageControl<NumericUpDown>(panel, "InterfaceSize");
        CompactSteppers(size, 24); FitDigits(size, 24);
        size.Value = (decimal)state.Preferences.FontSize;
        size.ValueChanged += (_, _) => { if (size.Value is not null) { state.Preferences.FontSize = (double)size.Value; Save(); } };
        foreach (var name in new[] { "ThemeModeChoices", "ThemeChoices", "SystemThemes" })
            PageControl<StackPanel>(panel, name).IsEnabled = !themeChanging;
        return panel;
    }

    /// <summary>The accent palette and the custom colour, a host setting: the app itself is the preview, so a choice applies to open windows at once.</summary>
    private void AccentSettings(Control panel)
    {
        var choices = PageControl<WrapPanel>(panel, "AccentChoices");
        var custom = PageControl<TextBox>(panel, "AccentCustom");
        var apply = PageControl<Button>(panel, "AccentCustomApply");
        var message = PageControl<TextBlock>(panel, "AccentCustomMessage");
        var current = Accents.Canonical(state.Preferences.Accent);
        var isCustom = Accents.ParseCustom(current) is not null && current.StartsWith('#');
        var theme = Themes.Resolve(state.Preferences, Themes.SystemAppearance).Theme;
        void Choose(string id) { if (id != current && !(Accents.IsDefault(current) && id == Accents.DefaultId)) Share(HostStateChange.Setting("accent", id)); }
        foreach (var choice in Accents.Curated)
        {
            var id = choice.Id;
            choices.Children.Add(AccentSwatch("Accent_" + id, choice.Label, Accents.SwatchBrush(theme, id),
                Accents.IsDefault(current) ? id == Accents.DefaultId : id == current, () => Choose(id)));
        }
        if (isCustom) choices.Children.Add(AccentSwatch("Accent_custom", "Custom " + current, Accents.SwatchBrush(theme, current), true, () => { }));
        custom.Text = isCustom ? current : "";
        void UseCustom()
        {
            if (Accents.ParseCustom(custom.Text) is not { } hex)
            {
                message.Text = "Enter a colour as six hex digits, such as #3b82f6.";
                message.IsVisible = true;
                return;
            }
            message.IsVisible = false;
            Choose(hex);
        }
        apply.Click += (_, _) => UseCustom();
        custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) { UseCustom(); e.Handled = true; } };
        if (isCustom)
        {
            message.Text = "Each theme adjusts a custom colour’s lightness so it stays legible.";
            message.IsVisible = true;
        }
    }

    private static Button AccentSwatch(string name, string label, IBrush swatch, bool active, Action select)
    {
        var button = Ui.Button(label, select);
        button.Name = name;
        button.Padding = new(12, 8); button.MinHeight = 36;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = swatch, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Ui.Text(label, active ? Ui.TextBrush : Ui.Muted));
        if (active) row.Children.Add(Ui.Icon("check", Ui.Accent));
        button.Content = row;
        button.Background = active ? Ui.PrimarySubtle : Ui.Elevated;
        button.BorderBrush = active ? Ui.PrimaryMuted : Ui.BorderBrush;
        AutomationProperties.SetName(button, active ? label + ", selected" : label);
        return button;
    }

    /// <summary>The Linux window frame choice, a host setting: the environment override, when set, wins and the page says so.</summary>
    private void WindowDecorationSettings(Control panel)
    {
        var section = PageControl<StackPanel>(panel, "WindowDecorationsSection");
        section.IsVisible = LinuxWindowChrome.Available;
        if (!section.IsVisible) return;
        var current = state.Current.Settings.WindowDecorations;
        var choices = PageControl<StackPanel>(panel, "WindowDecorationChoices");
        foreach (var (value, name, label, description) in new[]
        {
            ("", "Automatic", "Automatic (recommended)", "Integrated header on desktops known to support it, the system title bar elsewhere"),
            ("client", "Integrated", "Integrated header", "The header is the title bar, with the window controls on its right"),
            ("system", "System", "System title bar", "The window manager’s own title bar above the header")
        })
            choices.Children.Add(Choice("WindowDecorations_" + name, label, description, current == value, () =>
            {
                if (state.Current.Settings.WindowDecorations != value) Share(HostStateChange.Setting("window-decorations", value.Length == 0 ? "auto" : value));
            }));
        var note = PageControl<TextBlock>(panel, "WindowDecorationsOverride");
        note.Text = LinuxWindowChrome.OverrideNote(LinuxSession.FromEnvironment());
        note.IsVisible = note.Text.Length > 0;
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
            item.Icon = theme == resolution.Theme ? ActionIcons.Create(ActionIcon.Check, Ui.Accent) : null;
            menu.Items.Add(item);
        }
        trigger.ContextMenu = menu;
        trigger.Click += (_, _) => { menu.MinWidth = trigger.Bounds.Width; menu.Open(trigger); };
        var fallback = PageControl<TextBlock>(panel, $"SystemTheme_{appearance}_fallback");
        fallback.IsVisible = resolution.Fallback;
        fallback.Text = $"Configured theme unavailable in this app version. Using {resolution.Theme.Label}.";
    }

    private Control EditorSettings()
    {
        var panel = Page("EditorPage");
        var preferences = state.Preferences;
        var size = PageControl<NumericUpDown>(panel, "EditorSize");
        CompactSteppers(size, 24); FitDigits(size, 24);
        size.Value = (decimal)Tereditor.Scintilla.ScintillaEditor.TextSize;
        size.ValueChanged += (_, _) =>
        {
            if (size.Value is null) return;
            Tereditor.Scintilla.ScintillaEditor.TextSize = (double)size.Value;
            preferences.CodeFontSize = Tereditor.Scintilla.ScintillaEditor.TextSize; Save();
        };
        var tab = PageControl<NumericUpDown>(panel, "EditorTabWidth");
        CompactSteppers(tab, 24); FitDigits(tab, 24);
        tab.Value = preferences.EditorTabWidth;
        tab.ValueChanged += (_, _) =>
        {
            if (tab.Value is { } width && (int)width != preferences.EditorTabWidth) Share(HostStateChange.Setting("editor-tab-width", ((int)width).ToString(CultureInfo.InvariantCulture)));
        };
        var numbers = PageControl<Switch>(panel, "EditorLineNumbers");
        numbers.IsChecked = preferences.EditorLineNumbers;
        AutomationProperties.SetName(numbers, "Show line numbers");
        numbers.CheckedChange += requested =>
        {
            if (requested == preferences.EditorLineNumbers) return;
            Share(HostStateChange.Setting("editor-line-numbers", Setting(requested)));
            numbers.IsChecked = preferences.EditorLineNumbers;
        };
        refreshers.Add(() => { tab.Value = preferences.EditorTabWidth; numbers.IsChecked = preferences.EditorLineNumbers; });
        EditorFontSettings(panel);
        var height = PageControl<NumericUpDown>(panel, "EditorLineHeight");
        CompactSteppers(height, 24); FitDigits(height, 24); height.Width += Math.Ceiling(height.FontSize * 0.62);
        decimal CurrentHeight() => (state.Current.Settings.EditorLineHeight is >= HostSettings.MinEditorLineHeight and <= HostSettings.MaxEditorLineHeight
            ? state.Current.Settings.EditorLineHeight : HostSettings.DefaultEditorLineHeight) / 10m;
        height.Value = CurrentHeight();
        AutomationProperties.SetName(height, "Line height");
        height.ValueChanged += (_, _) =>
        {
            if (height.Value is not { } value || value == CurrentHeight()) return;
            var tenths = (int)Math.Clamp(Math.Round(value * 10), HostSettings.MinEditorLineHeight, HostSettings.MaxEditorLineHeight);
            Share(HostStateChange.Setting("editor-line-height", tenths.ToString(CultureInfo.InvariantCulture)));
        };
        var indentWith = PageControl<ComboBox>(panel, "EditorIndentWith");
        indentWith.ItemsSource = new[] { "Spaces", "Tabs" };
        var syncingIndent = false;
        void IndentChoice()
        {
            syncingIndent = true;
            try { indentWith.SelectedIndex = state.Current.Settings.EditorIndentWithTabs ? 1 : 0; }
            finally { syncingIndent = false; }
        }
        IndentChoice();
        indentWith.SelectionChanged += (_, _) =>
        {
            if (syncingIndent || indentWith.SelectedIndex < 0 || (indentWith.SelectedIndex == 1) == state.Current.Settings.EditorIndentWithTabs) return;
            Share(HostStateChange.Setting("editor-indent-tabs", Setting(indentWith.SelectedIndex == 1)));
            // The selection follows the host, not the pick: put it back until the broadcast moves it.
            IndentChoice();
        };
        var detect = PageControl<Switch>(panel, "EditorDetectIndent");
        detect.IsChecked = state.Current.Settings.EditorDetectIndent;
        AutomationProperties.SetName(detect, "Detect indentation from the file");
        detect.CheckedChange += requested =>
        {
            if (requested == state.Current.Settings.EditorDetectIndent) return;
            Share(HostStateChange.Setting("editor-detect-indent", Setting(requested)));
            detect.IsChecked = state.Current.Settings.EditorDetectIndent;
        };
        refreshers.Add(() => { height.Value = CurrentHeight(); IndentChoice(); detect.IsChecked = state.Current.Settings.EditorDetectIndent; });
        PageControl<ContentControl>(panel, "FileLineWidthHost").Content = LineWidthControl("File",
            "Wraps source files and diffs in the editor font.",
            () => preferences.FileLineWidth, () => preferences.FileLineWidthBounded);
        PageControl<ContentControl>(panel, "MarkdownLineWidthHost").Content = LineWidthControl("Markdown",
            "Wraps rendered Markdown and rendered diffs, measured in the reading font.",
            () => preferences.MarkdownLineWidth, () => preferences.MarkdownLineWidthBounded);
        return panel;
    }

    // An editable list of this device's monospace fonts: picking one, or typing a family and pressing Enter or leaving the
    // field, sends it; the bundled font is sent as empty. The field shows the host's family until its broadcast arrives.
    private void EditorFontSettings(Control panel)
    {
        var fonts = PageControl<ComboBox>(panel, "EditorFontFamily");
        var fallback = PageControl<TextBlock>(panel, "EditorFontFallback");
        string Current() => state.Current.Settings.EditorFontFamily is { Length: > 0 } family ? family : Tereditor.Plugins.UI.Kit.Editor.EditorFonts.Bundled;
        string[] families = [Tereditor.Plugins.UI.Kit.Editor.EditorFonts.Bundled];
        var syncing = false;
        void Show()
        {
            syncing = true;
            try
            {
                var current = Current();
                var listed = families.Contains(current, StringComparer.OrdinalIgnoreCase) ? families : [.. families, current];
                if (!ReferenceEquals(fonts.ItemsSource, listed) && !(fonts.ItemsSource is string[] shown && shown.SequenceEqual(listed))) fonts.ItemsSource = listed;
                fonts.SelectedItem = ((string[])fonts.ItemsSource!).First(item => item.Equals(current, StringComparison.OrdinalIgnoreCase));
                fonts.Text = current;
            }
            finally { syncing = false; }
            fallback.IsVisible = Tereditor.Plugins.UI.Kit.Editor.EditorFonts.FellBack;
            fallback.Text = $"“{state.Current.Settings.EditorFontFamily}” is not installed on this device; editors use {Tereditor.Plugins.UI.Kit.Editor.EditorFonts.Bundled}.";
        }
        void Commit(string? family)
        {
            family = (family ?? "").Trim();
            if (family.Equals(Tereditor.Plugins.UI.Kit.Editor.EditorFonts.Bundled, StringComparison.OrdinalIgnoreCase)) family = "";
            if (family.Length > HostSettings.MaxEditorFontFamilyLength || family == state.Current.Settings.EditorFontFamily) { Show(); return; }
            Share(HostStateChange.Setting("editor-font-family", family));
            Show();
        }
        Show();
        fonts.SelectionChanged += (_, _) => { if (!syncing && fonts.SelectedItem is string picked) Commit(picked); };
        // Tunnelling, so Enter and Escape reach the field before the combo box acts on them.
        fonts.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (fonts.IsDropDownOpen) return;
            if (e.Key == Key.Enter) { Commit(fonts.Text); e.Handled = true; }
            else if (e.Key == Key.Escape) { Show(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        fonts.LostFocus += (_, _) => { if (!syncing && fonts.Text != Current()) Commit(fonts.Text); };
        refreshers.Add(Show);
        // Listing the installed fonts loads each face, so it runs off the UI thread; the list fills in when ready.
        ListFonts();
        async void ListFonts()
        {
            try { families = [.. await Task.Run(Tereditor.Plugins.UI.Kit.Editor.EditorFonts.Monospace)]; Show(); }
            catch (Exception failure) when (failure is not OutOfMemoryException) { Console.Error.WriteLine("Font list failed: " + failure.Message); }
        }
    }

    // A draft input committed by Enter or when it loses focus (an invalid draft then reverts); Escape reverts it.
    // Both the width and the toggle show the host's value until its broadcast confirms a change.
    private Control LineWidthControl(string kind, string description, Func<int> value, Func<bool> bounded)
    {
        var key = kind.ToLowerInvariant();
        void setValue(int width) => Share(HostStateChange.Setting(key + "-width", width.ToString(CultureInfo.InvariantCulture)));
        var control = Page("LineWidthControl");
        control.Name = kind + "LineWidthControl";
        PageControl<TextBlock>(control, "LineWidthTitle").Text = kind;
        PageControl<TextBlock>(control, "LineWidthDescription").Text = description;
        var input = PageControl<TextBox>(control, "LineWidthInput");
        var limit = PageControl<Switch>(control, "LineWidthBounded");
        input.Name = kind + "LineWidthInput"; limit.Name = kind + "LineWidthBounded";
        ToolTip.SetTip(input, "Enter a whole number from 40 to 240.");
        AutomationProperties.SetName(input, kind + " line width");
        AutomationProperties.SetName(limit, "Limit " + kind.ToLowerInvariant() + " lines to this width");
        string Current() => value().ToString(CultureInfo.InvariantCulture);
        int? Parsed() => input.Text is { Length: > 0 and <= 3 } text && text.All(char.IsAsciiDigit) &&
            int.Parse(text, CultureInfo.InvariantCulture) is var number && LineWidths.IsValid(number) ? number : null;
        void Validate()
        {
            var parsed = Parsed();
            input.Classes.Set("invalid", parsed is null);
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
        input.LostFocus += (_, _) => { Commit(); if (Parsed() is null) input.Text = Current(); Validate(); };
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
        var directions = PageControl<StackPanel>(panel, "PaneDirections");
        var directionDetail = PageControl<TextBlock>(panel, "PaneDirectionDetail");
        directionDetail.Text = "What a new pair looks like when two tabs are first shown together, from a tab's menu. A tab joining an existing group follows that group.";
        void RenderTabs()
        {
            directions.Children.Clear();
            foreach (var (direction, label) in new[] { ("horizontal", "Columns"), ("vertical", "Rows") })
            {
                var active = state.Preferences.DefaultPaneDirection == direction;
                var choice = new Button
                {
                    Name = "DefaultPane_" + direction,
                    Content = label,
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
            RefreshSection(); return;
        }
        if (window.Slot.DefaultPreset == name) { window.Slot.DefaultPreset = next; Save(); }
    }

    private async Task ApplyLayout(string name)
    {
        if (!await Dialogs.Confirm(window, "Apply this layout?", "Open files, documents, and terminals are preserved, but their groups and proportions will be rearranged across every workspace in this window. Other windows are unaffected.", "Apply layout")) return;
        var preset = (state.Preferences.CustomPresets.GetValueOrDefault(name) ?? DockState.Preset(name)).Copy();
        preset.SideLimit = Math.Max(layout.State.SideLimit, Math.Max(preset.Groups.Count(group => group.Region == "left"), preset.Groups.Count(group => group.Region == "right")));
        layout.ApplyPreset(preset);
        window.Slot.Layout = layout.State; Save();
    }

    // Small-number fields are as wide as the digits of their maximum (at least two) plus the two steppers, the
    // content padding and the border, and sit on the row's right edge; big-value fields do not call this.
    internal static double DigitFieldWidth(NumericUpDown input, double stepper)
    {
        var digits = Math.Max(2, Math.Abs(input.Maximum).ToString("0", CultureInfo.InvariantCulture).Length);
        return Math.Ceiling(digits * input.FontSize * 0.62 + 10 + input.Padding.Left + input.Padding.Right + 2 * stepper + 2);
    }

    private static void FitDigits(NumericUpDown input, double stepper)
    {
        input.Padding = new Thickness(8, 0);
        input.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        input.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        input.Width = DigitFieldWidth(input, stepper);
    }

    // The spinner's template draws its own large chevrons; small muted ones of the app's icon set replace them.
    private static void CompactSteppers(NumericUpDown input, double width)
    {
        input.TemplateApplied += (_, outer) =>
        {
            if (outer.NameScope.Find<ButtonSpinner>("PART_Spinner") is not { } spinner) return;
            spinner.TemplateApplied += (_, inner) =>
            {
                foreach (var (name, up) in new[] { ("PART_IncreaseButton", true), ("PART_DecreaseButton", false) })
                    if (inner.NameScope.Find<RepeatButton>(name) is { } step)
                    {
                        step.Width = width; step.MinWidth = 0; step.Padding = new Thickness(0);
                        var icon = Ui.Icon("arrowDown", Ui.Muted, 10);
                        if (up) icon.RenderTransform = new RotateTransform(180);
                        step.Content = icon;
                    }
            };
        };
    }

    private Control Limit(string region, int value, Action<int> change)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(Ui.Text(window.HasUnifiedChrome ? "Right side groups" : "Side groups", size: 12));
        var input = new NumericUpDown
        {
            Name = "GroupLimit_" + region,
            Classes = { "compact-number" },
            Minimum = 1,
            Maximum = 32,
            Value = value,
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
        CompactSteppers(input, 22); FitDigits(input, 22);
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
        var group = PageControl<CheckBox>(panel, "GroupWorkspacesUnderCreator");
        group.IsChecked = state.Preferences.GroupWorkspacesUnderCreator;
        group.IsCheckedChanged += (_, _) =>
        {
            state.Preferences.GroupWorkspacesUnderCreator = group.IsChecked == true;
            Save();
            foreach (var open in window.Workbench.Windows) open.ApplyTabLayout();
        };
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
        // Metal is available only on macOS.
        PageControl<StackPanel>(panel, "TerminalRendererSection").IsVisible = OperatingSystem.IsMacOS();
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
        var replay = PageControl<ComboBox>(panel, "TerminalReplayChoices");
        var replaySizes = new[]
        {
            (0, "Off: reattaching shows an empty screen over the live shell"),
            (16, "16 KB: about a screenful"),
            (HostSettings.DefaultTerminalReplayKb, "64 KB: a screenful plus scrollback (default)"),
            (256, "256 KB: long scrollback; more memory per terminal"),
            (HostSettings.MaxTerminalReplayKb, "1 MB: maximum")
        };
        replay.ItemsSource = replaySizes.Select(size => size.Item2).ToArray();
        AutomationProperties.SetName(replay, "Replayed output");
        var syncingReplay = false;
        void ShowReplay()
        {
            syncingReplay = true;
            try { replay.SelectedIndex = Array.FindIndex(replaySizes, size => size.Item1 == state.Current.Settings.TerminalReplayKb); }
            finally { syncingReplay = false; }
        }
        ShowReplay();
        replay.SelectionChanged += (_, _) =>
        {
            if (syncingReplay || replay.SelectedIndex < 0) return;
            var kb = replaySizes[replay.SelectedIndex].Item1;
            if (state.Current.Settings.TerminalReplayKb != kb) Share(HostStateChange.Setting("terminal-replay", kb.ToString(CultureInfo.InvariantCulture)));
            // The selection follows the host, not the pick: put it back until the broadcast moves it.
            ShowReplay();
        };
        refreshers.Add(ShowReplay);
        // Host state: the switch and boxes follow the host's broadcast, never the click.
        var programs = PageControl<Switch>(panel, "TerminalNotificationsEnabled");
        programs.IsChecked = state.Current.Settings.TerminalNotificationsEnabled;
        programs.CheckedChange += on =>
        {
            Share(HostStateChange.Setting("terminal-notifications", on ? "true" : "false"));
            if (on) window.Workbench.Notifier?.RequestPermission();
        };
        refreshers.Add(() => programs.IsChecked = state.Current.Settings.TerminalNotificationsEnabled);
        PageControl<Border>(panel, "TerminalOptionAsAltRow").IsVisible = OperatingSystem.IsMacOS();
        var optionAsAlt = PageControl<Switch>(panel, "TerminalOptionAsAlt");
        optionAsAlt.IsChecked = state.Current.Settings.TerminalOptionAsAlt;
        optionAsAlt.CheckedChange += on => Share(HostStateChange.Setting("terminal-option-as-alt", on ? "true" : "false"));
        refreshers.Add(() => optionAsAlt.IsChecked = state.Current.Settings.TerminalOptionAsAlt);
        foreach (var (feature, name) in new[] { ("title", "BellTitle"), ("attention", "BellAttention"), ("system", "BellSystem") })
        {
            var box = PageControl<CheckBox>(panel, name);
            box.IsChecked = state.Current.Settings.Bell(feature);
            box.IsCheckedChanged += (_, _) =>
            {
                var features = HostSettings.BellFeatureNames.Where(other => other == feature ? box.IsChecked == true : state.Current.Settings.Bell(other)).ToArray();
                var value = features.Length == 0 ? "none" : string.Join(",", features);
                if (value != state.Current.Settings.BellFeatures) Share(HostStateChange.Setting("bell-features", value));
            };
            refreshers.Add(() => box.IsChecked = state.Current.Settings.Bell(feature));
        }
        var exit = PageControl<StackPanel>(panel, "TerminalExitChoices");
        var keep = state.Current.Settings.TerminalKeepAfterExit;
        foreach (var (value, name, label, description) in new[]
        {
            (false, "Close", "Close the tab (like Ghostty)", "The tab closes as soon as its shell exits (default)"),
            (true, "Keep", "Keep the tab with an exit notice", "The tab stays with its final output and exit code until you close it")
        })
            exit.Children.Add(Choice("TerminalExit_" + name, label, description, keep == value, () =>
            {
                if (state.Current.Settings.TerminalKeepAfterExit != value) Share(HostStateChange.Setting("terminal-keep-after-exit", value ? "true" : "false"));
            }));
        var undo = PageControl<StackPanel>(panel, "TerminalUndoCloseChoices");
        var undoSeconds = state.Current.Settings.TerminalUndoCloseSeconds;
        foreach (var (seconds, label, description) in new[]
        {
            (0, "Off", "Closing a tab is final and ends its process at once"),
            (HostSettings.DefaultTerminalUndoCloseSeconds, "5 seconds", "As Ghostty does (default)"),
            (15, "15 seconds", "More time to change your mind"),
            (60, "1 minute", "A closed terminal keeps running for a minute")
        })
            undo.Children.Add(Choice("TerminalUndoClose_" + seconds, label, description, undoSeconds == seconds, () =>
            {
                if (state.Current.Settings.TerminalUndoCloseSeconds != seconds)
                    Share(HostStateChange.Setting("terminal-undo-close", seconds.ToString(CultureInfo.InvariantCulture)));
            }));
        TerminalMemorySettings(panel);
        var integration = PageControl<Switch>(panel, "ShellIntegrationEnabled");
        integration.IsChecked = state.Current.Settings.ShellIntegrationEnabled;
        integration.IsEnabled = state.Supports(HostProtocol.ShellIntegration);
        integration.CheckedChange += on => Share(HostStateChange.Setting("shell-integration", on ? "true" : "false"));
        // Secure Event Input exists only on macOS; elsewhere the section is hidden.
        PageControl<StackPanel>(panel, "TerminalSecureInputSection").IsVisible = Terminal.SecureInput.Shared.Supported;
        var secure = PageControl<Switch>(panel, "TerminalSecureInput");
        secure.IsChecked = state.Preferences.SecureInputForPasswords;
        secure.CheckedChange += on =>
        {
            if (state.Preferences.SecureInputForPasswords == on) return;
            state.Preferences.SecureInputForPasswords = on;
            Save();
            Terminal.SecureInput.Shared.Automatic = on;
            foreach (var open in window.Workbench.Windows) open.UpdateSecureInput();
        };
        return panel;
    }

    // Host state: the controls follow the host's broadcast, never the click. The setting is one JSON object; a change edits one member of it.
    private void TerminalMemorySettings(Control panel)
    {
        System.Text.Json.Nodes.JsonObject Current()
        {
            try { return System.Text.Json.Nodes.JsonNode.Parse(state.Current.Settings.TerminalMemory) as System.Text.Json.Nodes.JsonObject ?? []; }
            catch (System.Text.Json.JsonException) { return []; }
        }
        void Change(string member, System.Text.Json.Nodes.JsonNode? value)
        {
            var next = Current();
            next[member] = value;
            Share(HostStateChange.Setting("terminal-memory", next.ToJsonString()));
        }
        bool Enabled() => Current()["enabled"]?.GetValue<bool>() ?? true;
        var enabled = PageControl<Switch>(panel, "TerminalMemoryEnabled");
        enabled.IsChecked = Enabled();
        enabled.CheckedChange += on => { if (on != Enabled()) Change("enabled", on); };
        refreshers.Add(() => enabled.IsChecked = Enabled());
        void Limit(string name, string member, string automatic, int[] gigabytes)
        {
            var box = PageControl<ComboBox>(panel, name);
            var sizes = new[] { 0 }.Concat(gigabytes.Select(gb => gb * 1024)).ToArray();
            box.ItemsSource = new[] { automatic }.Concat(gigabytes.Select(gb => Units.Pair(gb, "GB"))).ToArray();
            AutomationProperties.SetName(box, name);
            var syncing = false;
            void Show()
            {
                syncing = true;
                try
                {
                    var megabytes = Current()[member]?.GetValue<int>() ?? 0;
                    box.SelectedIndex = Math.Max(0, Array.IndexOf(sizes, megabytes));
                }
                finally { syncing = false; }
            }
            Show();
            box.SelectionChanged += (_, _) =>
            {
                if (syncing || box.SelectedIndex < 0) return;
                if ((Current()[member]?.GetValue<int>() ?? 0) != sizes[box.SelectedIndex]) Change(member, sizes[box.SelectedIndex]);
                Show();
            };
            refreshers.Add(Show);
        }
        Limit("TerminalMemoryPerTerminal", "perTerminalMb", "Automatic: a quarter of physical memory, 2 to 32 GB", [2, 4, 8, 16, 32, 64]);
        Limit("TerminalMemoryTotal", "totalMb", "Automatic: 60% of physical memory", [8, 16, 32, 64, 128]);
        var protect = PageControl<TextBox>(panel, "TerminalMemoryProtect");
        string Names() => string.Join(", ", (Current()["protect"] as System.Text.Json.Nodes.JsonArray ?? []).Select(name => name?.GetValue<string>()));
        void Commit()
        {
            var names = (protect.Text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.Join(", ", names) == Names()) return;
            Change("protect", new System.Text.Json.Nodes.JsonArray(names.Select(name => (System.Text.Json.Nodes.JsonNode?)name).ToArray()));
        }
        protect.Text = Names();
        protect.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { protect.Text = Names(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        };
        protect.LostFocus += (_, _) => Commit();
        refreshers.Add(() => { if (!protect.IsFocused) protect.Text = Names(); });
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
            : "Your operating system delivers these notifications; allow or silence tereditor in its notification settings.";
        return panel;
    }
}