using System.Text;
using System.Text.Json.Serialization;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.PluginFixture.Outsider;

/// <summary>The outsider's own settings: how often it was activated and clicked, and the greeting its asset gave.</summary>
public sealed record OutsiderSettings
{
    public int Activations { get; set; }
    public int Clicks { get; set; }
    public string Greeting { get; set; } = "";
}

/// <summary>
/// A UI-only external plugin: a side tool, a settings section and a viewer for <c>.outsider</c> files. Each activation
/// reads its greeting asset, counts itself in its own settings and says hello with the count it reads back, so a check
/// sees a settings round trip before any of its views can be shown. The button counts clicks the same way, and every
/// view shows the count as the settings change, so a click in one client shows in another. The board adds a list to
/// scroll, a note to type in, a button that floods its client with one-way calls, and three popups: a context menu and
/// a flyout that each count a click, and a tooltip.
/// </summary>
public sealed class OutsiderUI : PluginUIModule
{
    static OutsiderUI() => PluginJson.Register(OutsiderJson.Default);

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new("board", _ => Board(context)));
        context.SettingsSection(new("Outsider", "puzzle-2-line", () => View(context, "OutsiderSettings")));
        context.FileViewer(new(props => new Border { Name = "OutsiderViewer", Child = Ui.Text(props.Text ?? "", Ui.TextBrush) }));
        _ = Greet(context);
        return null;
    }

    private static async Task Greet(IPluginUIContext context)
    {
        try
        {
            var greeting = Words.Words.Greeting(Encoding.UTF8.GetString(await context.ReadAssetAsync("greeting.txt")));
            var settings = context.Settings<OutsiderSettings>();
            await context.UpdateSettingsAsync(settings with { Activations = settings.Activations + 1, Greeting = greeting });
            context.Notify(PluginNotificationKind.Info, greeting, $"Activations: {context.Settings<OutsiderSettings>().Activations}.");
        }
        catch (Exception error) { context.Log.Error("The greeting failed: " + error); }
    }

    private static Control Board(IPluginUIContext context)
    {
        var panel = (StackPanel)View(context, "OutsiderBoard");
        panel.Children.Add(Popups(context));
        var rows = new StackPanel { Name = "OutsiderRows" };
        for (var row = 1; row <= 60; row++) rows.Children.Add(Ui.Text($"Row {row}", Ui.TextBrush));
        panel.Children.Add(new ScrollViewer { Name = "OutsiderList", Height = 120, Content = rows });
        panel.Children.Add(new TextBox { Name = "OutsiderNote", Width = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        // Out of the tab order, so the checks that tab through the board are unchanged.
        var flood = Ui.Button("Flood this client", () => _ = Flood(context));
        (flood.Name, flood.IsTabStop) = ("OutsiderFlood", false);
        panel.Children.Add(flood);
        return panel;
    }

    /// <summary>
    /// A row with a context menu on its label, a flyout on its button and a tooltip on its last word. The menu item and
    /// the flyout's button count a click as the board's button does. Nothing here is a tab stop, so the checks that tab
    /// through the board are unchanged.
    /// </summary>
    private static Control Popups(IPluginUIContext context)
    {
        var menuItem = new MenuItem { Name = "OutsiderMenuCount", Header = "Count from the menu" };
        menuItem.Click += (_, _) => _ = Count(context);
        var menu = new Border
        {
            Name = "OutsiderMenuTarget",
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new(4),
            Child = Ui.Text("Right-click here", Ui.TextBrush),
            ContextMenu = new ContextMenu { Items = { menuItem } },
        };
        var inFlyout = Ui.Button("Count from the flyout", () => _ = Count(context));
        inFlyout.Name = "OutsiderFlyoutCount";
        var more = Ui.Button("More", () => { });
        (more.Name, more.IsTabStop) = ("OutsiderMore", false);
        more.Flyout = new Flyout { Content = inFlyout, Placement = PlacementMode.Bottom };
        var tip = new Border { Name = "OutsiderTip", Background = Avalonia.Media.Brushes.Transparent, Padding = new(4), Child = Ui.Text("Tip", Ui.TextBrush) };
        ToolTip.SetTip(tip, "A tip from the outsider");
        ToolTip.SetShowDelay(tip, 50);
        return new StackPanel { Name = "OutsiderPopups", Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { menu, more, tip } };
    }

    private static async Task Count(IPluginUIContext context)
    {
        try { await context.UpdateSettingsAsync(context.Settings<OutsiderSettings>() with { Clicks = context.Settings<OutsiderSettings>().Clicks + 1 }); }
        catch (Exception error) { context.Log.Error("The count failed: " + error.Message); }
    }

    /// <summary>Sends this client's context more one-way calls than a client may leave unread, a little at a time.</summary>
    private static async Task Flood(IPluginUIContext context)
    {
        try
        {
            for (var call = 1; call <= FloodCalls; call++)
            {
                context.Invalidate();
                if (call % 64 == 0) await Task.Delay(1);
            }
        }
        catch (Exception error) { context.Log.Error("The flood failed: " + error.Message); }
    }

    /// <summary>One-way calls the flood button sends: more than the 1,024 a client may leave unread.</summary>
    private const int FloodCalls = 2048;

    private static Control View(IPluginUIContext context, string name)
    {
        var settings = context.Settings<OutsiderSettings>();
        var clicks = Ui.Text($"Clicks: {settings.Clicks}", Ui.TextBrush);
        var button = Ui.Button("Count a click", async () =>
        {
            await context.UpdateSettingsAsync(context.Settings<OutsiderSettings>() with { Clicks = context.Settings<OutsiderSettings>().Clicks + 1 });
            clicks.Text = $"Clicks: {context.Settings<OutsiderSettings>().Clicks}";
        });
        button.Name = "OutsiderClick";
        var panel = new StackPanel { Name = name, Spacing = 8 };
        var watching = context.OnSettings<OutsiderSettings>(changed => clicks.Text = $"Clicks: {changed.Clicks}");
        panel.DetachedFromVisualTree += (_, _) => watching.Dispose();
        panel.Children.Add(Ui.Text(settings.Greeting, Ui.TextBrush));
        panel.Children.Add(clicks);
        panel.Children.Add(button);
        return panel;
    }
}

[JsonSerializable(typeof(OutsiderSettings))]
internal sealed partial class OutsiderJson : JsonSerializerContext;