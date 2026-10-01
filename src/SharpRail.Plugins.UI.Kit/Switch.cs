using Avalonia.Controls.Primitives;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// The shared on/off control: a track and a thumb beside its label. Its state reaches assistive technology
/// through the toggle pattern, so it never spells out On or Off. The look is the control theme in App.axaml.
/// </summary>
public sealed class Switch : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(Switch);
}