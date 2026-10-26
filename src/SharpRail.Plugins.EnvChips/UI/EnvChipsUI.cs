using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.EnvChips.UI;

public sealed class EnvChipsUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.TerminalAccessory(new(terminal => new EnvChipsRow(context, terminal.WorkspaceId)) { ForWorkspace = workspaceId => new EnvChipsRow(context, workspaceId) });
        return null;
    }
}

/// <summary>One chip per environment applied to the terminal's workspace; hidden while none is.</summary>
internal sealed class EnvChipsRow : WrapPanel
{
    private readonly IDisposable subscription;

    public EnvChipsRow(IPluginUIContext context, string workspaceId)
    {
        Name = "TerminalEnvironmentChips";
        Orientation = Orientation.Horizontal;
        // The inset the agent plugins give their fact rows, so the chips do not sit against the terminal edge.
        Margin = new Avalonia.Thickness(8, 2);
        IsVisible = false;
        // The first snapshot arrives on the subscription; later resolutions replace the chips.
        subscription = context.Subscribe(EnvChipsContract.Changed, payload => Dispatcher.UIThread.Post(() => Apply(payload)), new EnvChipsQuery(workspaceId));
        DetachedFromVisualTree += (_, _) => subscription.Dispose();
    }

    private string shown = "\0";

    private void Apply(EnvChipsChanged payload)
    {
        var signature = string.Join("\n", payload.Chips.Select(chip => chip.Label + "\t" + chip.Detail));
        if (signature == shown) return;
        shown = signature;
        Children.Clear();
        foreach (var chip in payload.Chips)
        {
            var control = TerminalFacts.FactChip("env", chip.Label, string.IsNullOrEmpty(chip.Detail) ? chip.Label : chip.Detail);
            control.Margin = new Avalonia.Thickness(0, 0, 4, 0);
            Children.Add(control);
        }
        IsVisible = Children.Count > 0;
    }
}