using Avalonia;
using Avalonia.Controls;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>One kind of chip a terminal accessory row can show: a stable id and the name the strip's chip menu lists it by.</summary>
/// <param name="Id">The id the hidden-chips setting stores: a shared id such as <c>model</c>, or <c>pluginId/chipId</c> for a plugin's own chip.</param>
/// <param name="Label">The name the chip menu shows.</param>
public sealed record TerminalChipKind(string Id, string Label);

/// <summary>
/// How a terminal accessory row tells the strip what its chips are, so the user can choose which ones show. A row tags each
/// chip (the control that carries the chip's gap) with <see cref="IdProperty"/> and declares every kind it can show, present
/// now or not, with <see cref="KindsProperty"/> on its root. The strip hides a tagged chip whose id the user unchecked.
/// </summary>
public sealed class TerminalChips : AvaloniaObject
{
    private TerminalChips() { }

    /// <summary>The stable id of the chip this control is.</summary>
    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<TerminalChips, Control, string?>("Id");

    /// <summary>The chip kinds this row can show, in its order; null or empty while it shows none (for example, the terminal does not run its agent).</summary>
    public static readonly AttachedProperty<IReadOnlyList<TerminalChipKind>?> KindsProperty =
        AvaloniaProperty.RegisterAttached<TerminalChips, Control, IReadOnlyList<TerminalChipKind>?>("Kinds");

    /// <summary>Tags <paramref name="control"/> as the chip <paramref name="id"/> and returns it.</summary>
    public static T Tag<T>(T control, string id) where T : Control
    {
        control.SetValue(IdProperty, id);
        return control;
    }
}