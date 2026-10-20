using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;

using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>One toast: compiled XAML bound to the <see cref="Toast"/>, with the button wiring the queue needs.</summary>
internal sealed partial class ToastCard : Border
{
    internal ToastCard(Toast toast, ToastQueue queue)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = toast;
        Tag = toast;
        AutomationProperties.SetLiveSetting(this, toast.Variant == ToastVariant.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (toast.Variant != ToastVariant.Info) this.FindControl<Border>("Stripe")!.Classes.Add(toast.Variant == ToastVariant.Error ? "error" : "success");
        foreach (var text in this.GetLogicalDescendants().OfType<TextBlock>())
        {
            text.FontSize = Ui.FontSize; text.FontWeight = Ui.InterfaceWeight;
        }
        this.FindControl<TextBlock>("ToastTitle")!.Foreground = Ui.TextBrush;
        var two = toast is { Action: not null, Secondary: not null };
        var row = this.FindControl<StackPanel>("ActionRow")!;
        var single = this.FindControl<Button>("SingleAction")!;
        var first = this.FindControl<Button>("RowAction")!;
        var second = this.FindControl<Button>("RowSecondary")!;
        row.IsVisible = two;
        single.IsVisible = toast.Action is not null && !two;
        // The visible buttons take the names the host contract and the checks address; the hidden ones stay unnamed.
        first.Name = second.Name = single.Name = null;
        (two ? first : single).Name = "ToastAction";
        if (two) second.Name = "ToastSecondaryAction";
        void Wire(Button button, ToastAction? action)
        {
            if (action is not null) button.Click += (_, _) => { queue.Dismiss(toast.Id); action.Invoke(); };
        }
        Wire(first, toast.Action); Wire(second, toast.Secondary); Wire(single, toast.Action);
        var dismiss = this.FindControl<Button>("ToastDismiss")!;
        dismiss.Content = Ui.Icon("close", Ui.Muted, 14);
        dismiss.Click += (_, _) => queue.Dismiss(toast.Id);
    }
}