using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Env.UI;

public sealed class EnvUI : PluginUIModule
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
        Margin = new Avalonia.Thickness(8, 0);
        IsVisible = false;
        // The first snapshot arrives on the subscription; later resolutions replace the chips.
        subscription = context.Subscribe(EnvContract.Changed, payload => Dispatcher.UIThread.Post(() => Apply(payload)), new EnvChipsQuery(workspaceId));
        DetachedFromVisualTree += (_, _) => subscription.Dispose();
    }

    private string shown = "\0";

    private void Apply(EnvChipsChanged payload)
    {
        var signature = string.Join("\n", payload.Chips.Select(chip => chip.Label + "\t" + chip.Detail + "\t" + chip.PluginId));
        if (signature == shown) return;
        shown = signature;
        Children.Clear();
        // Each environment is its own chip kind in the strip's chip menu, keyed by the plugin whose step applied it.
        var kinds = new List<TerminalChipKind>();
        foreach (var chip in payload.Chips)
        {
            var id = EnvContract.Id + "/" + (chip.PluginId ?? chip.Label);
            var control = TerminalChips.Tag(TerminalFacts.FactChip("env", chip.Label, string.IsNullOrEmpty(chip.Detail) ? chip.Label : chip.Detail), id);
            control.Margin = Chips.Gap;
            Children.Add(control);
            if (kinds.All(kind => kind.Id != id)) kinds.Add(new(id, chip.Label));
        }
        SetValue(TerminalChips.KindsProperty, kinds);
        IsVisible = Children.Count > 0;
    }
}