using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.PluginFixture.UI;

// The UI half's own copy of the fixture contract: an external UI half never references its host half's assembly,
// and the payloads cross as JSON, so the two copies interoperate.
public sealed record FixtureSettings { public string Greeting { get; init; } = "hello"; }
public sealed record EchoParams(string Text);
public sealed record EchoResult(string Text, string Greeting);
public sealed record CounterParams(string Workspace);
public sealed record CounterState(string Workspace, int Value);
public sealed record Ping(string Message);

public static class FixtureContract
{
    public static readonly PluginMethod<EchoParams, EchoResult> Echo = new("echo");
    public static readonly PluginMethod<CounterParams, CounterState> Counter = new("counter");
    public static readonly PluginMethod<CounterParams, CounterState> Bump = new("bump");
    public static readonly PluginChannel<CounterState> CounterChannel = PluginChannel<CounterState>.State("counter", Counter, "workspace");
    public static readonly PluginChannel<Ping> Pings = PluginChannel<Ping>.Event("pings");
    public static readonly PluginContract Contract = PluginContract.Create<FixtureSettings>("fixture", 1, [Echo, Counter, Bump], [CounterChannel, Pings]);
}

public sealed class FixtureUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.SideTool(new("board", workspace => Board(context, workspace)));
        context.SettingsSection(new("Fixture", "puzzle-2-line", () =>
        {
            var greeting = Ui.Text(context.Settings<FixtureSettings>().Greeting, Ui.TextBrush);
            greeting.Name = "FixtureGreeting";
            var greet = Ui.Button("Say hey", async () =>
            {
                await context.UpdateSettingsAsync(new FixtureSettings { Greeting = "hey" });
                greeting.Text = context.Settings<FixtureSettings>().Greeting;
            });
            greet.Name = "FixtureGreet";
            var section = new StackPanel { Spacing = 4 };
            section.Children.Add(greeting); section.Children.Add(greet);
            return section;
        }));
        context.FileViewer(new(props =>
        {
            var text = Ui.Text(props.Text ?? "", Ui.TextBrush);
            text.Name = "FixtureViewerText";
            return new Border { Name = "FixtureViewer", Child = text };
        }));
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("echo", (workspace, group) =>
        {
            var result = Ui.Text("", Ui.Muted);
            result.Name = "FixtureActionResult";
            var button = Ui.Button("Echo", async () =>
            {
                var echoed = await context.RequestAsync(FixtureContract.Echo, new EchoParams(workspace));
                result.Text = echoed.Greeting + " " + echoed.Text;
            });
            button.Name = "FixtureAction";
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(button); row.Children.Add(result);
            return row;
        }));
        context.TerminalAccessory(new(terminal =>
        {
            var row = new StackPanel { Name = "FixtureAccessory", Orientation = Orientation.Horizontal, Spacing = 4 };
            var write = Ui.Button("Say fixture", () => terminal.Write("echo fixture-accessory\r"));
            write.Name = "FixtureAccessoryWrite";
            row.Children.Add(write);
            row.Children.Add(Ui.Text(terminal.TabKey, Ui.Hint, 11));
            return row;
        }));
        context.FileIconSlot((path, kind) => kind == FileIconKind.File && path.EndsWith(".fixture", StringComparison.Ordinal) ? "asset:icon.svg" : null);
        return null;
    }

    private static Control Board(IPluginUIContext context, string workspace)
    {
        var value = Ui.Text("…", Ui.TextBrush);
        value.Name = "FixtureCounter";
        var panel = new StackPanel { Name = "FixtureBoard", Spacing = 8, Margin = new Avalonia.Thickness(12) };
        panel.Children.Add(value);
        var bump = Ui.Button("Bump", async () => await context.RequestAsync(FixtureContract.Bump, new CounterParams(workspace)));
        bump.Name = "FixtureBump";
        panel.Children.Add(bump);
        IDisposable? subscription = null;
        panel.AttachedToVisualTree += (_, _) =>
            subscription ??= context.Subscribe(FixtureContract.CounterChannel, state => value.Text = state.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new CounterParams(workspace));
        panel.DetachedFromVisualTree += (_, _) => { subscription?.Dispose(); subscription = null; };
        return panel;
    }
}