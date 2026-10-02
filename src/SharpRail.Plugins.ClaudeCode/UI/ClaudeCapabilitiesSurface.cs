using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>What Claude can reach: MCP servers, plugins, skills, subagents, hooks and marketplaces, each with its source and switches.</summary>
internal sealed class ClaudeCapabilitiesSurface(ClaudeConfigPanel pane, IReadOnlyList<ClaudeCapability> capabilities)
{
    private static readonly (ClaudeCapabilityKind Kind, string Heading)[] Sections =
    [
        (ClaudeCapabilityKind.Mcp, "MCP servers"), (ClaudeCapabilityKind.Plugin, "Plugins"), (ClaudeCapabilityKind.Skill, "Skills"),
        (ClaudeCapabilityKind.Agent, "Subagents"), (ClaudeCapabilityKind.Hook, "Hooks"), (ClaudeCapabilityKind.Marketplace, "Marketplaces")
    ];

    private static readonly (string Kind, string Label)[] Addable = [("mcp", "MCP server"), ("skill", "Skill"), ("hook", "Hook"), ("plugin", "Plugin"), ("marketplace", "Marketplace")];

    /// <summary>The edit a row's switch proposes, or null for a capability Claude Code gives no switch.</summary>
    private static PendingEdit? ToggleFor(ClaudeCapability item) => item.Kind switch
    {
        // A server no file declares (a claude.ai connector, a plugin's) is switched in /mcp, not here.
        ClaudeCapabilityKind.Mcp when item.Origin.Path is null => null,
        ClaudeCapabilityKind.Mcp => new(new McpEdit(item.Name, !item.Enabled), item.Enabled ? $"Deny \"{item.Name}\"" : $"Allow \"{item.Name}\""),
        ClaudeCapabilityKind.Plugin => new(new PluginEdit(item.Name, !item.Enabled), item.Enabled ? $"Turn off \"{item.Name}\"" : $"Turn on \"{item.Name}\""),
        ClaudeCapabilityKind.Skill => new(new SkillEdit(item.Name, !item.Enabled), item.Enabled ? $"Turn off \"{item.Name}\"" : $"Turn on \"{item.Name}\""),
        _ => null
    };

    // Uninstalling and marketplace runs are Claude's own CLI against a scope it accepts; managed settings have none.
    private static ClaudeWritableScope? WritableScope(ClaudeCapability item) =>
        ClaudeValues.WritableScopes.Cast<ClaudeWritableScope?>().FirstOrDefault(scope => scope!.Value.Name() == item.Origin.Scope.Name());

    public Control View
    {
        get
        {
            var list = new StackPanel();
            var adding = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4) };
            adding.Children.Add(Ui.Text("WHAT CLAUDE CAN REACH", Ui.Muted, 11));
            var buttons = new WrapPanel();
            foreach (var (kind, label) in Addable)
            {
                var button = Ui.Button(label, () =>
                {
                    if (kind == "marketplace") pane.Marketplace(new AddMarketplace("", ClaudeWritableScope.User));
                    else pane.Add(kind);
                }, "add");
                button.Name = "ClaudeAdd_" + kind;
                button.Margin = new Thickness(0, 0, 4, 4);
                buttons.Children.Add(button);
            }
            adding.Children.Add(buttons);
            list.Children.Add(new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = adding });
            if (capabilities.Count == 0)
            {
                var none = Ui.Text("Nothing grants Claude extra abilities here.", Ui.Muted, 13);
                none.Margin = new Thickness(8);
                list.Children.Add(none);
            }
            foreach (var (kind, heading) in Sections)
            {
                var rows = capabilities.Where(item => item.Kind == kind).ToArray();
                if (rows.Length == 0) continue;
                var section = new StackPanel { Name = "ClaudeCapabilitySection", Tag = kind };
                section.Children.Add(new Border { Background = Ui.Header, Padding = new Thickness(8, 2), Child = Ui.Text(heading.ToUpperInvariant(), Ui.Muted, 11) });
                foreach (var item in rows) section.Children.Add(Row(item));
                list.Children.Add(section);
            }
            return list;
        }
    }

    private Control Row(ClaudeCapability item)
    {
        var row = new StackPanel { Name = "ClaudeCapability", Tag = item, Margin = new Thickness(8, 4) };
        var head = new DockPanel();
        var toggle = ToggleFor(item);
        var writable = WritableScope(item);
        var uninstallScope = item.Kind == ClaudeCapabilityKind.Plugin ? writable : null;
        var marketplaceScope = item.Kind == ClaudeCapabilityKind.Marketplace ? writable : null;
        if (toggle is not null || uninstallScope is not null || marketplaceScope is not null)
        {
            Button? menuButton = null;
            menuButton = Ui.IconButton("more", $"Actions for {item.Name}", () =>
            {
                var menu = new ContextMenu { Name = "ClaudeCapabilityActions", Placement = PlacementMode.BottomEdgeAlignedRight, PlacementTarget = menuButton };
                static MenuItem Item(string name, string label, Action action)
                {
                    var entry = Ui.Menu(label, action);
                    entry.Name = name;
                    return entry;
                }
                if (toggle is not null) menu.Items.Add(Item("ClaudeCapabilityToggle", item.Enabled ? "Turn off" : "Turn on", () => pane.Review(toggle)));
                if (marketplaceScope is { } ownScope)
                {
                    menu.Items.Add(Item("ClaudeMarketplaceUpdate", "Update from source…", () => pane.Marketplace(new UpdateMarketplace(item.Name))));
                    menu.Items.Add(Item("ClaudeMarketplaceRemove", "Remove…", () => pane.Marketplace(new RemoveMarketplace(item.Name, ownScope))));
                }
                if (uninstallScope is { } from)
                {
                    foreach (var to in ClaudeValues.WritableScopes.Where(scope => scope != from))
                    {
                        var entry = Item("ClaudeCapabilityMove_" + to.Name(), $"Move to {to.Name()}…", () => pane.Move(new(item.Name, from, to)));
                        ToolTip.SetTip(entry, ClaudeValues.PluginScopeWording(to));
                        menu.Items.Add(entry);
                    }
                    menu.Items.Add(Item("ClaudeCapabilityUninstall", "Uninstall…", () => pane.Uninstall(new(item.Name, from))));
                }
                menuButton!.ContextMenu = menu;
                menu.Open(menuButton);
            });
            menuButton.Name = "ClaudeCapabilityMenu";
            menuButton.Width = menuButton.Height = 22;
            menuButton.Padding = new Thickness(4);
            DockPanel.SetDock(menuButton, Dock.Right);
            head.Children.Add(menuButton);
        }
        if (!item.Enabled)
        {
            var off = Ui.Text(item.DisabledBy is { } by ? $"OFF · {by.Scope.Name().ToUpperInvariant()}" : "OFF", Ui.Hint, 10);
            off.Name = "ClaudeCapabilityOff";
            off.Margin = new Thickness(4, 0);
            if (item.DisabledBy?.Path is not null) ToolTip.SetTip(off, $"Switched off in {item.DisabledBy.Scope.Name()} settings");
            DockPanel.SetDock(off, Dock.Right);
            head.Children.Add(off);
        }
        if (item.Origin.Path is null)
        {
            var chip = ClaudeParts.ScopeChip(item.Origin.Scope.Name());
            chip.Margin = new Thickness(4, 0);
            DockPanel.SetDock(chip, Dock.Right);
            head.Children.Add(chip);
        }
        head.Children.Add(Ui.Text(item.Name, Ui.TextBrush, 13));
        row.Children.Add(head);
        if (item.DisabledBy?.Path is { } disabledPath && disabledPath != item.Origin.Path)
            row.Children.Add(ClaudeParts.SourceButton(disabledPath, item.DisabledBy.KeyPath, pane.Open));
        if (item.Origin.Path is { } path)
        {
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            source.Children.Add(ClaudeParts.SourceButton(path, item.Origin.KeyPath, pane.Open));
            source.Children.Add(ClaudeParts.ScopeChip(item.Origin.Scope.Name()));
            row.Children.Add(source);
        }
        if (item.Detail is { } detail) row.Children.Add(ClaudeParts.Code(detail, Ui.Hint));
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }
}