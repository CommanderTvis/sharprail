using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace SharpRail.Plugins.UI.Kit.Markdown;

internal sealed class FrontmatterField : UserControl
{
    private readonly AutoCompleteBox input;
    private string value;
    internal bool SuppressBlur { get; set; }

    internal FrontmatterField(string value, string name, Action<string> commit, IEnumerable<string>? suggestions = null, string? placeholder = null)
    {
        AvaloniaXamlLoader.Load(this);
        Name = name;
        if (name is "FrontmatterKey" or "FrontmatterMapKey") Classes.Add("muted");
        this.value = value;
        input = this.FindControl<AutoCompleteBox>("Input")!;
        input.Text = value;
        input.ItemsSource = suggestions;
        input.PlaceholderText = placeholder;
        AutomationProperties.SetName(input, placeholder ?? name);
        void Finish()
        {
            var next = input.Text ?? "";
            if (next == this.value) return;
            this.value = next;
            commit(next);
        }
        input.LostFocus += (_, _) => { if (!SuppressBlur && !input.IsKeyboardFocusWithin) Finish(); };
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { input.Text = this.value; input.IsDropDownOpen = false; if (placeholder is null) TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null); e.Handled = true; }
            else if (e.Key == Key.Enter) { input.IsDropDownOpen = false; Finish(); if (placeholder is null) TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null); e.Handled = true; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal void FocusInput() => input.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();

    internal void Clear() { value = ""; input.Text = ""; }
}