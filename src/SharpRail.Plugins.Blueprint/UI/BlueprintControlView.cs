using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintControlView : UserControl
{
    public void SetChanged(bool changed) => this.FindControl<Border>("Frame")!.BorderBrush = changed ? Ui.Warning : Ui.BorderBrush;

    public BlueprintControlView(BlueprintControl control, bool changed, Action<string> toggle, Action<BlueprintEditTarget, string> edit)
    {
        AvaloniaXamlLoader.Load(this);
        Name = "BlueprintControl"; Tag = control.Id;
        var frame = this.FindControl<Border>("Frame")!;
        frame.Background = Ui.Elevated;
        frame.BorderBrush = changed ? Ui.Warning : Ui.BorderBrush;
        var title = this.FindControl<TextBlock>("Title")!;
        title.Text = control.Title; title.Foreground = Ui.Hint;
        var locked = this.FindControl<ContentControl>("Lock")!;
        locked.IsVisible = control.Locked;
        locked.Content = Ui.Icon("lock", Ui.Hint, 12);
        ToolTip.SetTip(locked, "You set this — regeneration keeps it");
        var choice = this.FindControl<Button>("Choice")!;
        var options = this.FindControl<StackPanel>("Options")!;
        choice.IsVisible = control.Kind == BlueprintControlKind.Select;
        choice.IsEnabled = !control.Pending;
        var selected = control.Options.FirstOrDefault(option => control.SelectedIds.Contains(option.Id));
        choice.Content = selected?.Label ?? "…";
        choice.Name = "BlueprintChoice"; choice.Tag = string.Join(' ', control.SelectedIds);
        var menu = new StackPanel { MaxWidth = 420 };
        foreach (var option in control.Options)
        {
            var label = new StackPanel();
            label.Children.Add(Ui.Text((control.SelectedIds.Contains(option.Id) ? "✓ " : "   ") + option.Label, Ui.TextBrush));
            if (option.Axis.Length > 0) label.Children.Add(Ui.Text(option.Axis, Ui.Muted, 12));
            var button = new Button { Name = "BlueprintOption", Tag = option.Id, Content = label, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            button.Click += (_, _) => { choice.Flyout?.Hide(); toggle(option.Id); };
            menu.Children.Add(button);
        }
        choice.Flyout = new Flyout { Content = menu, Placement = PlacementMode.BottomEdgeAlignedLeft };
        void AddOption(BlueprintOption option, bool checkbox)
        {
            var text = new StackPanel();
            text.Children.Add(new BlueprintEditable(option.Label, false, "BlueprintOptionLabel",
                next => edit(new BlueprintOptionLabelTarget(control.Id, option.Id), next)));
            text.Children.Add(new BlueprintEditable(option.Axis, false, "BlueprintOptionAxis",
                next => edit(new BlueprintOptionAxisTarget(control.Id, option.Id), next), placeholder: "why you would pick it"));
            if (!checkbox) { options.Children.Add(text); return; }
            var check = new CheckBox { Name = "BlueprintCheckbox", Tag = option.Id, IsChecked = control.SelectedIds.Contains(option.Id), Content = text };
            check.IsCheckedChanged += (_, _) => toggle(option.Id);
            options.Children.Add(check);
        }
        if (control.Kind == BlueprintControlKind.Select)
        { if (selected is not null) AddOption(selected, false); }
        else foreach (var option in control.Options) AddOption(option, true);
    }
}