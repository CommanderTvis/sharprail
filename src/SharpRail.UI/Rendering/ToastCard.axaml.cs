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
        var choices = toast.Choices;
        // One link sits beside the message; more choices, or a button-styled one, share a row beneath it.
        var inline = choices is [{ Style: ToastActionStyle.Link }];
        var row = this.FindControl<WrapPanel>("ActionRow")!;
        var single = this.FindControl<Button>("SingleAction")!;
        var error = this.FindControl<TextBlock>("ToastError")!;
        row.IsVisible = choices.Count > 0 && !inline;
        single.IsVisible = inline;
        if (choices.Count > 0 && choices[0].Style != ToastActionStyle.Link) row.Margin = new(0, 4, 0, 0);
        // The visible buttons take the names the host contract and the checks address; the hidden one stays unnamed.
        single.Name = inline ? "ToastAction" : null;
        var buttons = new List<Button>();
        if (inline) buttons.Add(single);
        else
            for (var index = 0; index < choices.Count; index++)
            {
                var choice = choices[index];
                var button = new Button { Name = index switch { 0 => "ToastAction", 1 => "ToastSecondaryAction", _ => "ToastAction" + index } };
                if (choice.Style == ToastActionStyle.Link)
                {
                    button.Classes.Add("toast-action");
                    button.Content = new TextBlock { Text = choice.Label, Foreground = Ui.Accent, FontSize = Ui.FontSize, FontWeight = Ui.InterfaceWeight };
                }
                else
                {
                    if (choice.Style == ToastActionStyle.Primary) button.Classes.Add("primary");
                    button.Content = choice.Label;
                }
                row.Children.Add(button);
                buttons.Add(button);
            }
        for (var index = 0; index < buttons.Count; index++)
        {
            var choice = choices[index];
            buttons[index].Click += async (_, _) =>
            {
                if (choice.RunAsync is not { } run)
                {
                    if (choice.Closes) queue.Dismiss(toast.Id);
                    choice.Invoke();
                    return;
                }
                // The choice is answered elsewhere too, so its buttons wait for the result and a failure stays readable here.
                foreach (var button in buttons) button.IsEnabled = false;
                error.IsVisible = false;
                try
                {
                    await run();
                    if (choice.Closes) queue.Dismiss(toast.Id);
                }
                catch (Exception failure) { error.Text = failure.Message; error.IsVisible = true; }
                finally { foreach (var button in buttons) button.IsEnabled = true; }
            };
        }
        var dismiss = this.FindControl<Button>("ToastDismiss")!;
        dismiss.Content = Ui.Icon("close", Ui.Muted, 14);
        dismiss.Click += (_, _) => queue.Dismiss(toast.Id);
        if (toast is { Countdown: true, Lifetime: { } lifetime }) Count(toast, lifetime);
    }

    // The message gains the whole seconds left, ticking while the card is shown.
    private void Count(Toast toast, TimeSpan lifetime)
    {
        var message = this.FindControl<TextBlock>("ToastMessage")!;
        var ends = DateTime.UtcNow + lifetime;
        void Show() => message.Text = $"{toast.Message} ({Math.Max(0, (int)Math.Ceiling((ends - DateTime.UtcNow).TotalSeconds))} s)";
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Show();
        AttachedToVisualTree += (_, _) => { Show(); timer.Start(); };
        DetachedFromVisualTree += (_, _) => timer.Stop();
    }
}