using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.UI.Docking;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LayoutSettingsE2E
{
    internal static void Run(string root)
    {
        Review(root);
        DefaultReset(root);
        ContainerWidths(root);
        GrandfatheredLimit(root);
        CustomPresetsSync(root);
    }

    private static SettingsView Open(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OpenSettingsViews.Any());
        var settings = app.Window.OpenSettingsViews.Single();
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Layout"));
        return settings;
    }

    private static bool Label(Button button, string label) => button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == label);

    private static Button PresetButton(SettingsView settings, string name, string label) =>
        settings.GetLogicalDescendants().OfType<Grid>().Single(grid => grid.Name == "Preset_" + name)
            .GetLogicalDescendants().OfType<Button>().Single(button => Label(button, label));

    private static void Respond(E2eWorkspace app, SettingsView settings, bool accept)
    {
        Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any());
        var dialog = app.Window.OwnedWindows.OfType<DialogWindow>().Single();
        app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => Label(button, accept ? "Apply layout" : "Cancel")));
        Until(() => !app.Window.OwnedWindows.OfType<DialogWindow>().Any());
    }

    private static void Review(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-settings-review"));
        app.Open("README.md", true); app.Open("notes.txt", true);
        Require(app.Window.Layout.State.Groups.All(group => group.Region != "bottom"), "The workspace has no bottom zone.");
        var settings = Open(app);
        var limit = settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_side");
        settings.UpdateLayout();
        var steps = limit.GetVisualDescendants().OfType<RepeatButton>().ToArray();
        var digits = limit.GetVisualDescendants().OfType<TextBox>().Single();
        Require(steps.Length == 2 && steps.All(step => step.Bounds.Width == 22) && digits.Bounds.Width >= 36,
            $"A group limit leaves its digits room beside two narrow steppers ({digits.Bounds.Width} beside {string.Join(", ", steps.Select(step => step.Bounds.Width))}).");
        var spinner = limit.GetVisualDescendants().OfType<ButtonSpinner>().Single();
        var outline = spinner.GetVisualDescendants().OfType<Border>().First(border => border.BorderThickness == limit.BorderThickness);
        var outlineOrigin = outline.TranslatePoint(default, limit)!.Value;
        Require(outlineOrigin.Y >= 0 && outlineOrigin.Y + outline.Bounds.Height <= limit.Bounds.Height && digits.Bounds.Height <= limit.Bounds.Height,
            $"A compact number input must contain its complete outline and digits (input {limit.Bounds.Height}, outline {outlineOrigin.Y} + {outline.Bounds.Height}, digits {digits.Bounds.Height}).");
        var increase = steps.Single(step => step.Name == "PART_IncreaseButton");
        var decrease = steps.Single(step => step.Name == "PART_DecreaseButton");
        var original = limit.Value;
        app.Click(increase);
        Require(limit.Value == original + 1, "The compact increase stepper must remain clickable.");
        app.Click(decrease);
        Require(limit.Value == original, "The compact decrease stepper must remain clickable.");
        limit.Value = limit.Minimum; Settle();
        Require(!decrease.IsEnabled && increase.IsEnabled, "The minimum disables only the decrease stepper.");
        limit.Value = limit.Maximum; Settle();
        Require(!increase.IsEnabled && decrease.IsEnabled, "The maximum disables only the increase stepper.");
        limit.Value = original;
        var ink = (digits.Foreground as Avalonia.Media.ISolidColorBrush)?.Color ?? default;
        Require(settings.ActualThemeVariant == app.Window.ActualThemeVariant &&
            (settings.ActualThemeVariant != Avalonia.Styling.ThemeVariant.Dark || ink.R + ink.G + ink.B > 3 * 160),
            $"Settings takes its window's theme variant, so Fluent inputs stay legible ({settings.ActualThemeVariant} beside {app.Window.ActualThemeVariant}, {ink}).");
        foreach (var region in new[] { "side" })
        {
            settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_" + region).Value = region == "side" ? 3 : 2;
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SaveGroupLimit_" + region));
        }
        var before = System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State);
        app.Click(PresetButton(settings, "review", "Apply now…")); Respond(app, settings, false);
        Require(System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State) == before, "Canceling a preset must retain the exact frame and resources.");
        app.Click(PresetButton(settings, "review", "Apply now…")); Respond(app, settings, true);
        var state = app.Window.Layout.State;
        Require(state.SideLimit == 3 && state.Center.IsLeaf && Review(state) &&
            state.Center.Leaves().SelectMany(app.Window.Layout.Tabs).Select(tab => tab.Path).Order().SequenceEqual(new[] { "README.md", "notes.txt" }.Order()) &&
            state.Groups.Count(group => group.Region == "left") == 1 && state.Groups.Count(group => group.Region == "right") == 2 &&
            state.Groups.All(group => group.Region != "bottom"),
            "Review must preserve resources in the one main column group and install its auxiliary topology.");
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Settle();
        Require(!settings.IsVisible, "Escape must close Settings after applying a preset.");
        var workbench = app.Find<Control>("WorkspaceWorkbench");
        var right = app.Find<Grid>("AuxiliaryStack_right");
        Require(right.Bounds.Width / workbench.Bounds.Width > .3, "Review must allocate more than 30 percent to the right stack.");
        NoCenterSeparator(app);
        Console.WriteLine("PASS upstream layout.spec.ts: applying the Review preset preserves resources and installs its auxiliary topology");
    }

    private static void DefaultReset(string root)
    {
        var directory = Path.Combine(root, "layout-settings-default");
        using (var app = new E2eWorkspace(directory))
        {
            var settings = Open(app);
            app.Click(PresetButton(settings, "review", "Set default"));
            Require(PresetButton(settings, "review", "Default") is not null, "Review must become the local default.");
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => Label(button, "Reset frame")));
            Respond(app, settings, true);
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose"));
            Require(Review(app.Window.Layout.State), "An explicit reset must use the local default preset.");
            NoCenterSeparator(app);
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            Require(Review(app.Window.Layout.State), "Reload must retain the reset frame.");
            NoCenterSeparator(app);
            var settings = Open(app);
            Require(PresetButton(settings, "review", "Default") is not null, "Reload must retain the local default choice.");
            settings.Close();
        }
        Console.WriteLine("PASS upstream layout.spec.ts: the local default preset drives an explicit frame reset");
    }

    private static void ContainerWidths(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-settings-widths"));
        var settings = Open(app);
        settings.Width = 1200; Settle();
        var saveRow = settings.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "PresetSaveRow");
        var limits = settings.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "GroupLimits");
        Require(saveRow.MaxWidth == 512 && saveRow.Bounds.Width <= 512 && limits.MaxWidth == 384 && limits.Bounds.Width <= 384,
            "Layout settings controls must retain their reference container widths even when Settings has extra space.");
        settings.Close();
        Console.WriteLine("PASS upstream layout.spec.ts: Layout settings controls keep their container-preset max-widths");
    }

    private static void GrandfatheredLimit(string root)
    {
        var source = Environment.GetEnvironmentVariable("SHARPRAIL_TEST_GIT_SOURCE");
        if (string.IsNullOrEmpty(source)) { Console.WriteLine("SKIP upstream side-group overage: set SHARPRAIL_TEST_GIT_SOURCE."); return; }
        var directory = WorkspaceTabsE2E.Repository(root, "layout-side-overage", source);
        using (var app = new E2eWorkspace(directory))
        {
            void SaveLimit(int value)
            {
                var settings = Open(app);
                var spin = settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_side");
                var original = app.Window.Layout.State.SideLimit;
                var text = spin.GetVisualDescendants().OfType<TextBox>().Single();
                app.Click(text);
                var gesture = Application.Current!.PlatformSettings!.HotkeyConfiguration.SelectAll.First();
                var command = gesture.KeyModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;
                settings.KeyPress(Key.A, command, PhysicalKey.A, null);
                settings.KeyRelease(Key.A, command, PhysicalKey.A, null);
                settings.KeyTextInput(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Require(text.Text == value.ToString(System.Globalization.CultureInfo.InvariantCulture), "Select-all and typing must replace the displayed numeric draft.");
                settings.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Settle();
                var save = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SaveGroupLimit_side");
                Until(() => save.IsEnabled);
                Require(app.Window.Layout.State.SideLimit == original, "Editing a group limit must remain a draft until Save.");
                app.Click(save);
                Until(() => app.Window.Layout.State.SideLimit == value);
                settings.Close();
            }
            SaveLimit(3);
            app.Window.Layout.NewTerminal(app.Center);
            var id = app.Window.Layout.Tabs(app.Center).Single(tab => tab.Kind == "terminal").Id;
            app.SplitAction(app.Find<Button>("Tab_" + id.Replace(':', '_')), "Right");
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3, "The saved limit must permit the third right group.");
            WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            SaveLimit(2);
            WorkspaceTabsE2E.Switch(app, directory);
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3, "A lowered limit must retain existing groups across workspace switches.");
            foreach (var name in new[] { "Tab_files", "Tab_" + id.Replace(':', '_') })
            {
                var tab = app.Find<Button>(name);
                app.Click(tab, mouseButton: MouseButton.Right); Until(() => tab.ContextMenu!.IsOpen);
                var choice = app.SplitItem(tab, "Right");
                Require(!choice.IsEnabled && ((string)choice.Header!).EndsWith(" — limited to 2", StringComparison.Ordinal),
                    "An accepted overage must disable the region split command and explain the new limit.");
                tab.ContextMenu!.Close();
            }
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3, "Inspecting disabled commands must preserve the accepted overage.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
            Require(app.Window.Layout.State.SideLimit == 2 && app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3,
                "Reload must preserve the saved limit and the grandfathered groups.");
        Console.WriteLine("PASS upstream layout.spec.ts: an accepted side-group overage is grandfathered without allowing further growth");
    }

    private static Grid? PresetRow(SettingsView settings, string name) =>
        settings.GetLogicalDescendants().OfType<Grid>().SingleOrDefault(grid => grid.Name == "Preset_" + name);

    private static void CustomPresetsSync(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-settings-sync"));
        var settings = Open(app);
        var name = settings.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "PresetName");
        Require(Avalonia.Automation.AutomationProperties.GetName(name) == "Custom preset name", "The preset name field must be labelled.");
        name.Text = "My workbench";
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Save preset"));
        Until(() => PresetRow(settings, "My workbench") is not null);
        app.Click(PresetRow(settings, "My workbench")!.GetLogicalDescendants().OfType<Button>()
            .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Rename My workbench"));
        var rename = settings.GetLogicalDescendants().OfType<TextBox>().Single(box => Avalonia.Automation.AutomationProperties.GetName(box) == "Rename My workbench");
        rename.Text = "My renamed workbench";
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Save My workbench name"));
        Until(() => PresetRow(settings, "My renamed workbench") is not null && PresetRow(settings, "My workbench") is null);
        app.Click(PresetButton(settings, "My renamed workbench", "Set default"));
        Until(() => Equals(PresetRow(settings, "My renamed workbench")!.Tag, true));
        foreach (var (region, value) in new[] { ("side", 7) })
        {
            settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_" + region).Value = value;
            var save = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SaveGroupLimit_" + region);
            app.Click(save);
            Until(() => settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_" + region).Value == value &&
                !settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SaveGroupLimit_" + region).IsEnabled);
        }

        settings.Close();
        using var peer = app.NewWindow();
        var peerSettings = Open(peer);
        settings = Open(app);
        Until(() => PresetRow(peerSettings, "My renamed workbench") is not null);
        Require(!Equals(PresetRow(peerSettings, "My renamed workbench")!.Tag, true) && Equals(PresetRow(peerSettings, "balanced")!.Tag, true),
            "The custom preset synchronizes, but the default preset stays window-local.");
        Require(peerSettings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_side").Value == 6, "Group limits stay window-local.");

        app.Click(PresetRow(settings, "My renamed workbench")!.GetLogicalDescendants().OfType<Button>()
            .Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Delete My renamed workbench"));
        Until(() => PresetRow(settings, "My renamed workbench") is null && PresetRow(peerSettings, "My renamed workbench") is null);
        peerSettings.Close();
        Until(() => Equals(PresetRow(settings, "balanced")!.Tag, true));
        settings.Close();
        Console.WriteLine("PASS upstream layout.spec.ts: custom presets synchronize while defaults and group limits remain window-local");
    }

    // Review seats its tool first in the right rail and keeps the main column one group.
    private static bool Review(DockState state) => state.Center.IsLeaf &&
        state.Groups.First(group => group.Region == "right").Tools.FirstOrDefault()?.Id == "review";

    private static void NoCenterSeparator(E2eWorkspace app) =>
        Require(!app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.Name!.StartsWith("CenterSeparator_", StringComparison.Ordinal)),
            "The main column must have no split separator.");
}