using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SharpRail.UI.Panels;
using SharpRail.UI.Docking;
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
    }

    private static SettingsWindow Open(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Layout"));
        return settings;
    }

    private static bool Label(Button button, string label) => button.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == label);

    private static Button PresetButton(SettingsWindow settings, string name, string label) =>
        settings.GetLogicalDescendants().OfType<Grid>().Single(grid => grid.Name == "Preset_" + name)
            .GetLogicalDescendants().OfType<Button>().Single(button => Label(button, label));

    private static void Respond(E2eWorkspace app, SettingsWindow settings, bool accept)
    {
        Until(() => settings.OwnedWindows.OfType<DialogWindow>().Any());
        var dialog = settings.OwnedWindows.OfType<DialogWindow>().Single();
        app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => Label(button, accept ? "Apply layout" : "Cancel")));
        Until(() => !settings.OwnedWindows.OfType<DialogWindow>().Any());
    }

    private static void Review(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-settings-review"));
        app.Open("README.md", true); app.Open("notes.txt", true);
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        Require(app.Window.Layout.Tabs(bottom.Id).Single().Kind == "terminal", "The workspace must start with its initial bottom terminal.");
        var settings = Open(app);
        foreach (var region in new[] { "side", "bottom" })
        {
            settings.GetLogicalDescendants().OfType<NumericUpDown>().Single(input => input.Name == "GroupLimit_" + region).Value = region == "side" ? 3 : 2;
            app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SaveGroupLimit_" + region));
        }
        var before = System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State);
        app.Click(PresetButton(settings, "review", "Apply now…")); Respond(app, settings, false);
        Require(System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State) == before, "Canceling a preset must retain the exact frame and resources.");
        app.Click(PresetButton(settings, "review", "Apply now…")); Respond(app, settings, true);
        var state = app.Window.Layout.State;
        Require(state.SideLimit == 3 && state.BottomLimit == 2 && state.Center.Leaves().Count() == 2 && state.Center.Axis == "vertical" &&
            state.Center.Leaves().SelectMany(app.Window.Layout.Tabs).Select(tab => tab.Path).Order().SequenceEqual(new[] { "README.md", "notes.txt" }.Order()) &&
            state.Groups.Count(group => group.Region == "left") == 1 && state.Groups.Count(group => group.Region == "right") == 2 &&
            state.Groups.Where(group => group.Region == "bottom").SelectMany(group => app.Window.Layout.Tabs(group.Id)).Single().Title == "Terminal 1",
            "Review must preserve resources and install its vertical center and auxiliary topology.");
        settings.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Settle();
        Require(!settings.IsVisible, "Escape must close Settings after applying a preset.");
        var workbench = app.Find<Control>("WorkspaceWorkbench");
        var right = app.Find<Grid>("AuxiliaryStack_right");
        Require(right.Bounds.Width / workbench.Bounds.Width > .3, "Review must allocate more than 30 percent to the right stack.");
        HorizontalSeparator(app);
        Console.WriteLine("PASS upstream layout.spec.ts: applying the Review preset preserves resources and installs its vertical center topology");
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
            Require(app.Window.Layout.State.Center.Leaves().Count() == 2 && app.Window.Layout.State.Center.Axis == "vertical", "An explicit reset must use the local default preset.");
            HorizontalSeparator(app);
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            Require(app.Window.Layout.State.Center.Leaves().Count() == 2 && app.Window.Layout.State.Center.Axis == "vertical", "Reload must retain the reset frame.");
            HorizontalSeparator(app);
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
            var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
            var id = app.Window.Layout.Tabs(bottom.Id).Single(tab => tab.Kind == "terminal").Id;
            app.ContextAction(app.Find<Button>("Tab_" + id.Replace(':', '_')), "New right group at bottom");
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3, "The saved limit must permit the third right group.");
            WorkspaceTabsE2E.CreateWorkspace(app, "workspace-1");
            SaveLimit(2);
            WorkspaceTabsE2E.Switch(app, directory);
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3, "A lowered limit must retain existing groups across workspace switches.");
            foreach (var name in new[] { "Tab_files", "Tab_" + id.Replace(':', '_') })
            {
                var tab = app.Find<Button>(name);
                app.Click(tab, mouseButton: MouseButton.Right); Until(() => tab.ContextMenu!.IsOpen);
                var choices = tab.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Header is string title && title.StartsWith("New right group at", StringComparison.Ordinal)).ToArray();
                Require(choices.Length == 2 && choices.All(item => !item.IsEnabled && ((string)item.Header!).EndsWith(" — limited to 2", StringComparison.Ordinal)),
                    "An accepted overage must disable both region-edge creation commands and explain the new limit.");
                tab.ContextMenu.Close();
            }
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3, "Inspecting disabled commands must preserve the accepted overage.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
            Require(app.Window.Layout.State.SideLimit == 2 && app.Window.Layout.State.Groups.Count(group => group.Region == "right") == 3,
                "Reload must preserve the saved limit and the grandfathered groups.");
        Console.WriteLine("PASS upstream layout.spec.ts: an accepted side-group overage is grandfathered without allowing further growth");
    }

    private static void HorizontalSeparator(E2eWorkspace app)
    {
        var separator = app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Single(handle => handle.Name!.StartsWith("CenterSeparator_", StringComparison.Ordinal));
        var peer = ControlAutomationPeer.CreatePeerForElement(separator)!;
        Require(peer.GetAutomationControlType() == AutomationControlType.Separator && peer.GetName() == "Horizontal pane separator",
            "The vertical center split must expose its horizontal resize separator through native automation.");
    }
}
