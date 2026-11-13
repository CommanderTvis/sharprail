using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// A labelled <see cref="ToggleSwitch"/> whose caller owns the checked state: activation asks for the next value through
/// <see cref="CheckedChange"/> instead of flipping <see cref="Avalonia.Controls.Primitives.ToggleButton.IsChecked"/>.
/// </summary>
public sealed partial class Switch : ToggleSwitch
{
    /// <summary>The accessible label.</summary>
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<Switch, string>(nameof(Label), "");

    /// <summary>Creates a switch for a compiled layout; bind its label/state and handle its change requests.</summary>
    public Switch() => AvaloniaXamlLoader.Load(this);

    /// <summary>Creates a switch with its accessible label, initial checked state and change-request callback.</summary>
    public Switch(string label, bool isChecked, Action<bool> onCheckedChange) : this()
    {
        CheckedChange += onCheckedChange;
        Label = label;
        IsChecked = isChecked;
    }

    /// <summary>The label exposed to accessibility without visible On/Off text.</summary>
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Requests the next checked value on activation; the caller applies the new state.</summary>
    public event Action<bool>? CheckedChange;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) AutomationProperties.SetName(this, Label);
    }

    /// <summary>Raises Click, then requests the next value unless a handler handled it; the checked state stays the owner's.</summary>
    protected override void OnClick()
    {
        if (!IsEffectivelyEnabled) return;
        var next = IsChecked != true;
        var args = new RoutedEventArgs(ClickEvent);
        RaiseEvent(args);
        if (!args.Handled) CheckedChange?.Invoke(next);
    }
}