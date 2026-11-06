using System.Text;
using System.Text.Json.Serialization;

using Avalonia.Controls;

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
/// sees a settings round trip before any of its views can be shown. The button counts clicks the same way.
/// </summary>
public sealed class OutsiderUI : PluginUIModule
{
    static OutsiderUI() => PluginJson.Register(OutsiderJson.Default);

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new("board", _ => View(context, "OutsiderBoard")));
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
        panel.Children.Add(Ui.Text(settings.Greeting, Ui.TextBrush));
        panel.Children.Add(clicks);
        panel.Children.Add(button);
        return panel;
    }
}

[JsonSerializable(typeof(OutsiderSettings))]
internal sealed partial class OutsiderJson : JsonSerializerContext;