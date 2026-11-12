using System.Text;
using System.Text.Json.Serialization;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.PluginFixture.Outsider;

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
/// scroll, a note to type in, a button that floods its client with one-way calls, three popups (a context menu and
/// a flyout that each count a click, and a tooltip), and a row with a link that shows the hand cursor, a text field to
/// compose into with an input method, a button that asks in a dialog whether to count a click, a check box, and a fold whose
/// own automation peer expands and collapses, for accessibility.
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
        panel.Children.Add(Extras(context));
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

    /// <summary>
    /// A link with the hand cursor, a text field for input method composition, and a button that opens a dialog (the
    /// kit's <see cref="DialogWindow"/>, modal to the board): its button closes it with true, which counts a click, and
    /// closing it otherwise counts none. Nothing here is a tab stop, so the checks that tab through the board are
    /// unchanged.
    /// </summary>
    private static Control Extras(IPluginUIContext context)
    {
        var link = new TextBlock
        {
            Name = "OutsiderLink",
            Text = "A link",
            Foreground = Ui.PrimaryFill,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Padding = new(4),
            Background = Avalonia.Media.Brushes.Transparent,
        };
        var compose = new TextBox { Name = "OutsiderIme", Width = 120, IsTabStop = false };
        AutomationProperties.SetName(compose, "Compose here");
        var ask = Ui.Button("Ask", () => { });
        (ask.Name, ask.IsTabStop) = ("OutsiderAsk", false);
        ask.Click += (_, _) => _ = Ask(context, ask);
        var row = new StackPanel { Name = "OutsiderExtrasRow", Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { link, compose, ask } };
        var remind = new CheckBox { Name = "OutsiderCheck", Content = "Remind me", IsTabStop = false };
        return new StackPanel { Name = "OutsiderExtras", Spacing = 4, Children = { row, remind, new Fold { Name = "OutsiderFold" } } };
    }

    /// <summary>A control with a peer of its own that expands and collapses, as a plugin's own automation peer may.</summary>
    private sealed class Fold : Control
    {
        public bool Open { get; set; }

        protected override Size MeasureOverride(Size availableSize) => new(40, 16);

        protected override AutomationPeer OnCreateAutomationPeer() => new FoldPeer(this);
    }

    private sealed class FoldPeer(Fold fold) : ControlAutomationPeer(fold), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => fold.Open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public bool ShowsMenu => false;

        public void Expand() => fold.Open = true;

        public void Collapse() => fold.Open = false;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string? GetNameCore() => "Fold";
    }

    private static async Task Ask(IPluginUIContext context, Control from)
    {
        try
        {
            if (TopLevel.GetTopLevel(from) is not Window owner) return;
            var dialog = DialogWindow.Create("Count a click?", 320);
            var yes = Ui.Button("Count it", () => dialog.Close(true));
            yes.Name = "OutsiderDialogYes";
            dialog.FindControl<StackPanel>("DialogActions")!.Children.Add(yes);
            if (await dialog.ShowDialog<bool>(owner)) await Count(context);
        }
        catch (Exception error) { context.Log.Error("The dialog failed: " + error.Message); }
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
            // The client's session may end while the call waits for its reply: that cancels the call, and an unobserved
            // failure here would end the process every other client of the plugin shares.
            try
            {
                await context.UpdateSettingsAsync(context.Settings<OutsiderSettings>() with { Clicks = context.Settings<OutsiderSettings>().Clicks + 1 });
                clicks.Text = $"Clicks: {context.Settings<OutsiderSettings>().Clicks}";
            }
            catch (Exception error) { context.Log.Error("The click failed: " + error.Message); }
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