using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>Where a setting's value is declared: its scope and the file, or no file for a value nothing on disk holds.</summary>
public sealed record ScopedSettingSource(string Scope, string? Path);

/// <summary>A value a higher-precedence scope overrides, shown struck through under the winner.</summary>
public sealed record ScopedSettingShadow(string Scope, string? Path, string ValueText);

/// <summary>
/// The rows a configuration pane is built from: a scope chip, a source path that opens its file, row actions, a
/// <c>key = value</c> setting row with the values it shadows, and the filter-and-add bar above them.
/// </summary>
public static partial class ScopedSetting
{
    [GeneratedRegex("^/Users/[^/]+|^/home/[^/]+")]
    private static partial Regex HomePrefix();

    /// <summary>A path with the user's home written as <c>~</c>.</summary>
    public static string AbbreviateHomePath(string path) => HomePrefix().Replace(path, "~", 1);

    private static (IBrush Background, IBrush Foreground) ScopeColours(string scope) => scope switch
    {
        "managed" or "system" => (Ui.Elevated, Ui.Muted),
        "local" => (Ui.DangerWash, Ui.Danger),
        "project" => (Ui.WarningWash, Ui.Warning),
        "user" or "global" => (Ui.InfoWash, Ui.Info),
        _ => (Ui.Elevated, Ui.Hint)
    };

    /// <summary>The upper-case pill naming a scope, coloured by how widely it applies.</summary>
    public static Border ScopeChip(string scope, string name = "ScopeChip")
    {
        var (background, foreground) = ScopeColours(scope);
        return new Border
        {
            Name = name,
            Tag = scope,
            Background = background,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text(scope.ToUpperInvariant(), foreground, 10)
        };
    }

    /// <summary>A home-abbreviated path that opens its file when clicked, with the whole path on hover.</summary>
    public static Button SourcePath(string path, Action<string> onOpen, string name = "SourcePath")
    {
        var text = Ui.Text(AbbreviateHomePath(path), Ui.Muted, 12);
        text.FontFamily = Ui.CodeFont;
        var button = new Button
        {
            Name = name,
            Tag = path,
            Content = text,
            Padding = new Thickness(0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        button.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        button.PointerEntered += (_, _) => { text.Foreground = Ui.Accent; text.TextDecorations = TextDecorations.Underline; };
        button.PointerExited += (_, _) => { text.Foreground = Ui.Muted; text.TextDecorations = null; };
        ToolTip.SetTip(button, path);
        AutomationProperties.SetName(button, path);
        button.Click += (_, _) => onOpen(path);
        return button;
    }

    /// <summary>A small upper-case outline action at the end of a row; <paramref name="danger"/> turns its hover red.</summary>
    public static Button RowAction(string name, string label, Action onClick, bool danger = false)
    {
        var text = Ui.Text(label.ToUpperInvariant(), danger ? Ui.Hint : Ui.Muted, 10);
        var button = new Button
        {
            Name = name,
            Content = text,
            Padding = new Thickness(8, 0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
        button.PointerEntered += (_, _) => text.Foreground = danger ? Ui.Danger : Ui.TextBrush;
        button.PointerExited += (_, _) => text.Foreground = danger ? Ui.Hint : Ui.Muted;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// One setting: <c>key = value</c> on one line when it fits, the value below the key otherwise; then its source path
    /// and scope with the row's actions, then every value it shadows. A documented key links to its reference entry.
    /// </summary>
    public static Control SettingRow(string namePrefix, string settingKey, Control value, ScopedSettingSource source,
        IReadOnlyList<ScopedSettingShadow> shadowed, Action<string> onOpen, IEnumerable<Control>? actions = null,
        string? docsUrl = null, string? docsTitle = null)
    {
        var row = new StackPanel { Name = namePrefix + "Setting", Tag = settingKey, Spacing = 2, Margin = new Thickness(8, 4) };
        var head = new WrapPanel { Orientation = Orientation.Horizontal };
        var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var keyText = Ui.Text(settingKey, Ui.TextBrush, 12);
        keyText.FontFamily = Ui.CodeFont;
        keyText.TextWrapping = TextWrapping.Wrap;
        keyText.TextTrimming = TextTrimming.None;
        if (docsUrl is not null)
        {
            var link = new Button
            {
                Name = namePrefix + "SettingDocs",
                Tag = docsUrl,
                Content = keyText,
                Padding = new Thickness(0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            link.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
            link.PointerEntered += (_, _) => { keyText.Foreground = Ui.Accent; keyText.TextDecorations = TextDecorations.Underline; };
            link.PointerExited += (_, _) => { keyText.Foreground = Ui.TextBrush; keyText.TextDecorations = null; };
            if (docsTitle is not null) ToolTip.SetTip(link, docsTitle);
            link.Click += (_, _) => TopLevel.GetTopLevel(link)?.Launcher.LaunchUriAsync(new Uri(docsUrl));
            key.Children.Add(link);
        }
        else key.Children.Add(keyText);
        var equals = Ui.Text("=", Ui.Muted, 12);
        equals.FontFamily = Ui.CodeFont;
        key.Children.Add(equals);
        key.Margin = new Thickness(0, 0, 4, 0);
        head.Children.Add(key);
        head.Children.Add(value);
        row.Children.Add(head);

        var meta = new DockPanel { LastChildFill = false };
        if (source.Path is { } path) meta.Children.Add(SourcePath(path, onOpen, namePrefix + "OpenSource"));
        var chip = ScopeChip(source.Scope, namePrefix + "ScopeChip");
        chip.Margin = new Thickness(4, 0, 0, 0);
        meta.Children.Add(chip);
        var end = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var action in actions ?? []) end.Children.Add(action);
        DockPanel.SetDock(end, Dock.Right);
        meta.Children.Add(end);
        row.Children.Add(meta);

        foreach (var shadow in shadowed)
        {
            var line = new DockPanel { Name = namePrefix + "SettingShadowed", Margin = new Thickness(8, 2, 0, 0) };
            var shadowChip = ScopeChip(shadow.Scope, namePrefix + "ScopeChip");
            DockPanel.SetDock(shadowChip, Dock.Right);
            line.Children.Add(shadowChip);
            var text = Ui.Text(shadow.ValueText, Ui.Hint, 12);
            text.FontFamily = Ui.CodeFont;
            text.TextDecorations = TextDecorations.Strikethrough;
            ToolTip.SetTip(text, shadow.Path ?? shadow.ValueText);
            line.Children.Add(text);
            row.Children.Add(line);
        }
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
    }

    /// <summary>The filter-and-add bar above a settings list; <paramref name="onAdd"/> null leaves the add button out.</summary>
    public static Control SettingsToolbar(string namePrefix, string query, Action<string> onQuery, Action? onAdd)
    {
        var bar = new DockPanel { Margin = new Thickness(8) };
        if (onAdd is not null)
        {
            var add = Ui.Button("Add a setting", onAdd, "add");
            add.Name = namePrefix + "SettingAdd";
            add.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(add, Dock.Right);
            bar.Children.Add(add);
        }
        var filter = new TextBox { Name = namePrefix + "SettingFilter", Text = query, PlaceholderText = "Filter keys…" };
        AutomationProperties.SetName(filter, "Filter settings keys");
        filter.TextChanged += (_, _) => onQuery(filter.Text ?? "");
        bar.Children.Add(filter);
        return new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
    }
}