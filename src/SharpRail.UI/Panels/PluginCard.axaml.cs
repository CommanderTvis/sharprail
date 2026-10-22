using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api;
using SharpRail.UI.Plugins;

using Origin = SharpRail.Plugins.Api.PluginOrigin;
using Status = SharpRail.Plugins.Api.PluginStatus;

namespace SharpRail.UI.Panels;

/// <summary>
/// One roster entry on Settings › Plugins, kept per plugin id for the page's life: a roster change updates it in place
/// and raises <see cref="Changed"/>, so the list never replaces an item and its realized card is redrawn where it is.
/// </summary>
internal sealed class PluginCardModel(PluginRosterEntry entry, IReadOnlyList<string> dependents, string signature, string? heading)
{
    /// <summary>The category this card opens in the list, or null when the card follows another of the same category.</summary>
    internal string? Heading { get; private set; } = heading;
    internal PluginRosterEntry Entry { get; private set; } = entry;
    internal IReadOnlyList<string> Dependents { get; private set; } = dependents;
    internal string Signature { get; private set; } = signature;
    internal event Action? Changed;

    internal void Update(PluginRosterEntry next, IReadOnlyList<string> nextDependents, string nextSignature, string? nextHeading)
    {
        Entry = next; Dependents = nextDependents; Heading = nextHeading;
        if (Signature == nextSignature) return;
        Signature = nextSignature;
        Changed?.Invoke();
    }
}

/// <summary>
/// A Settings › Plugins card. The list virtualizes, so one card is recycled across plugins: a new or changed model rewrites
/// its texts in place and swaps the icon only when the icon itself changes, so a refresh keeps focus and pointer.
/// </summary>
internal sealed partial class PluginCard : Border
{
    private readonly TextBlock label, version, originText, status, description, contributions, reason, dependents, category;
    private readonly ContentControl icon;
    private readonly Button retry;
    private readonly ToggleSwitch toggle;
    private string? iconKey, drawn;
    private bool showing;

    internal PluginCard(Action<PluginRosterEntry> retryPlugin, Action<PluginRosterEntry> togglePlugin)
    {
        AvaloniaXamlLoader.Load(this);
        T Part<T>(string name) where T : Control => this.FindControl<T>(name)!;
        label = Part<TextBlock>("PluginLabel"); version = Part<TextBlock>("PluginVersion"); originText = Part<TextBlock>("PluginOriginText");
        status = Part<TextBlock>("PluginStatus"); description = Part<TextBlock>("PluginDescription"); contributions = Part<TextBlock>("PluginContributions");
        reason = Part<TextBlock>("PluginReason"); dependents = Part<TextBlock>("PluginDependents");
        category = Part<TextBlock>("PluginCategory");
        icon = Part<ContentControl>("PluginIcon"); toggle = Part<ToggleSwitch>("PluginToggle");
        label.FontSize = Ui.FontSize;
        retry = Ui.Button("Retry", () => { if (Model is { } model) retryPlugin(model.Entry); });
        retry.Name = "PluginRetry";
        Part<ContentControl>("PluginRetrySlot").Content = retry;
        // The switch only asks: it snaps back to the roster's state, and the host's snapshot moves it.
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (showing || Model is not { } model) return;
            var enabled = model.Entry.Status != Status.Disabled;
            if (toggle.IsChecked == enabled) return;
            Set(toggle, enabled);
            togglePlugin(model.Entry);
        };
        DataContextChanged += (_, _) =>
        {
            if (Model is { } previous) previous.Changed -= Redraw;
            Model = DataContext as PluginCardModel;
            if (Model is null) return;
            Model.Changed += Redraw;
            Redraw();
        };
    }

    internal PluginCardModel? Model { get; private set; }

    /// <summary>Draws the current model unless it is already drawn; the plugin id stays readable as the card's tag.</summary>
    private void Redraw()
    {
        if (Model is not { } model || drawn == model.Signature) return;
        drawn = model.Signature;
        var entry = model.Entry;
        Tag = entry.Id;
        var key = entry.Id + "\n" + entry.Icon + "\n" + entry.Assets;
        if (key != iconKey)
        {
            iconKey = key;
            icon.Content = PluginIcons.ResolveSettings(entry);
        }
        Text(category, model.Heading);
        label.Text = entry.Label;
        version.Text = "v" + entry.Version;
        version.IsVisible = entry.Origin == Origin.External;
        originText.Text = entry.Origin == Origin.External ? "external" : "builtin";
        var troubled = entry.Status is Status.Failed or Status.Refused;
        status.Text = entry.Status.ToString().ToLowerInvariant();
        status.Foreground = troubled ? Ui.Danger : Ui.Hint;
        Text(description, entry.Description);
        Text(contributions, Summary(entry));
        Text(reason, troubled ? entry.Reason ?? entry.Status.ToString().ToLowerInvariant() : null);
        Text(dependents, model.Dependents.Count > 0 ? $"Also used by {string.Join(", ", model.Dependents)}. Disabling turns them off too." : null);
        retry.IsVisible = entry.Status == Status.Failed;
        AutomationProperties.SetName(toggle, entry.Label + " enabled");
        Set(toggle, entry.Status != Status.Disabled);
    }

    private void Set(ToggleSwitch target, bool value)
    {
        showing = true;
        try { target.IsChecked = value; }
        finally { showing = false; }
    }

    private static void Text(TextBlock block, string? value)
    {
        block.Text = value;
        block.IsVisible = value is { Length: > 0 };
    }

    private static string? Summary(PluginRosterEntry entry)
    {
        var parts = new List<string>();
        var tools = entry.Contributes.SideTools.Count;
        if (tools > 0) parts.Add($"{tools} side tool{(tools == 1 ? "" : "s")}");
        var viewers = entry.Contributes.FileViewers.Count;
        if (viewers > 0) parts.Add($"{viewers} file viewer{(viewers == 1 ? "" : "s")}");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }
}