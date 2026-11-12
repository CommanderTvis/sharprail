using Avalonia;
using Avalonia.Controls;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>Agent-owned attachment behavior on a terminal accessory. The workbench resolves drops to host paths.</summary>
public sealed class FileDrop : AvaloniaObject
{
    private FileDrop() { }

    /// <summary>Whether this accessory currently represents an agent that accepts attachments.</summary>
    public static readonly AttachedProperty<Func<bool>?> AcceptsProperty =
        AvaloniaProperty.RegisterAttached<FileDrop, Control, Func<bool>?>("Accepts");

    /// <summary>Inserts the resolved paths into the agent's prompt, without submitting it.</summary>
    public static readonly AttachedProperty<Action<IReadOnlyList<string>>?> AttachProperty =
        AvaloniaProperty.RegisterAttached<FileDrop, Control, Action<IReadOnlyList<string>>?>("Attach");
}