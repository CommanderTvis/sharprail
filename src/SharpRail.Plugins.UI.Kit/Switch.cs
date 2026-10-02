using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>A labelled switch whose caller owns the checked state and handles requests to change it.</summary>
public sealed partial class Switch : Button
{
    /// <summary>The controlled checked state.</summary>
    public static readonly StyledProperty<bool> IsCheckedProperty = AvaloniaProperty.Register<Switch, bool>(nameof(IsChecked));
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

    /// <summary>Whether the switch is checked. Setting it does not invoke the callback.</summary>
    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) AutomationProperties.SetName(this, Label);
        if (change.Property == IsCheckedProperty)
        {
            PseudoClasses.Set(":checked", IsChecked);
            ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                IsChecked ? ToggleState.Off : ToggleState.On, IsChecked ? ToggleState.On : ToggleState.Off);
        }
    }

    /// <inheritdoc />
    protected override void OnClick()
    {
        if (!IsEffectivelyEnabled) return;
        var next = !IsChecked;
        var args = new RoutedEventArgs(ClickEvent);
        RaiseEvent(args);
        if (!args.Handled) CheckedChange?.Invoke(next);
    }

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new SwitchPeer(this);

    private sealed class SwitchPeer(Switch owner) : ControlAutomationPeer(owner), IToggleProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;
        public ToggleState ToggleState => owner.IsChecked ? ToggleState.On : ToggleState.Off;
        public void Toggle() => owner.OnClick();
    }
}