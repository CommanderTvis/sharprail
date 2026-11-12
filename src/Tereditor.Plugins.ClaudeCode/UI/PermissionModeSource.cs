using Avalonia.Controls;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The permission modes the host's CLI accepts, as the launcher and Settings last heard them. Until the host answers
/// (or when it cannot) the fallback list stands in; a lookup runs off the UI thread and only its result is applied here.
/// </summary>
internal static class PermissionModeSource
{
    public static PermissionModesResult Current { get; private set; } = new(ClaudePermissionModes.Fallback);

    private static Task<PermissionModesResult>? pending;

    /// <summary>Asks the host once at a time; the result replaces <see cref="Current"/>, a failure keeps it.</summary>
    public static async Task<PermissionModesResult> RefreshAsync(IPluginUIContext context)
    {
        var lookup = pending ??= Task.Run(async () => await context.RequestAsync(ClaudeCodeContract.PermissionModesGet, new NoParams()));
        try { Current = await lookup; }
        catch (Exception error) when (error is not OperationCanceledException) { }
        finally { if (ReferenceEquals(pending, lookup)) pending = null; }
        return Current;
    }

    /// <summary>"Claude Code's default", naming the mode the user's own settings choose when there is one.</summary>
    public static string DefaultLabel(PermissionModesResult modes) => modes.ConfiguredDefault is { } configured
        ? $"Claude Code's default ({ClaudePermissionModes.Describe(configured).Label}, from ~/.claude/settings.json)"
        : "Claude Code's default";

    public static string Label(ClaudePermissionMode mode) => mode.Dangerous ? mode.Label + " (dangerous)" : mode.Label;

    /// <summary>The modes to offer, with a saved one the CLI no longer lists kept so a choice never silently vanishes.</summary>
    public static IReadOnlyList<ClaudePermissionMode> Modes(PermissionModesResult modes, string? saved = null) =>
        [.. modes.Modes.Concat(saved is { Length: > 0 } && !modes.Modes.Contains(saved) ? [saved] : []).Select(ClaudePermissionModes.Describe)];

    /// <summary>A dangerous mode reads in the danger colour wherever it is offered.</summary>
    public static void Mark(Avalonia.Controls.Primitives.TemplatedControl item, ClaudePermissionMode mode)
    {
        if (mode.Description.Length > 0) ToolTip.SetTip(item, mode.Description);
        if (mode.Dangerous) item.Foreground = Ui.Danger;
    }
}