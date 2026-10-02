using System.Text.RegularExpressions;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintEditable : UserControl
{
    private readonly Border reading;
    private readonly ContentControl value;
    private readonly Button edit;
    private readonly TextBox input;
    private readonly Action<string> commit;
    private readonly Func<string, Control> render;
    private readonly bool multiline;
    private string text;
    private bool editing;

    public BlueprintEditable(string text, bool multiline, string name, Action<string> commit, Func<string, Control>? render = null,
        string placeholder = "Add a line…")
    {
        AvaloniaXamlLoader.Load(this);
        this.text = text; this.multiline = multiline; this.commit = commit;
        this.render = render ?? (next => Ui.Text(next.Length == 0 ? placeholder : next, next.Length == 0 ? Ui.Hint : Ui.TextBrush));
        Name = name;
        reading = this.FindControl<Border>("Reading")!;
        value = this.FindControl<ContentControl>("Value")!;
        edit = this.FindControl<Button>("Edit")!;
        input = this.FindControl<TextBox>("Input")!;
        AutomationProperties.SetName(edit, "Edit");
        input.AcceptsReturn = multiline;
        input.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        input.MinHeight = multiline ? 60 : 0;
        edit.Content = Ui.Icon("pencil", Ui.Hint, 14);
        edit.Click += (_, _) => Begin();
        input.LostFocus += (_, _) => Finish(save: true);
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(save: false); e.Handled = true; }
            else if (e.Key == Key.Enter && (!multiline || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            { Finish(save: true); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        Update(text);
    }

    public void Update(string next)
    {
        text = next;
        if (!editing) value.Content = render(text);
    }

    private void Begin()
    {
        editing = true;
        input.Text = text;
        reading.IsVisible = edit.IsVisible = false;
        input.IsVisible = true;
        input.Focus();
        input.CaretIndex = text.Length;
    }

    private void Finish(bool save)
    {
        if (!editing) return;
        editing = false;
        var next = multiline ? (input.Text ?? "").Trim() : Regex.Replace(input.Text ?? "", @"\s+", " ").Trim();
        input.IsVisible = false;
        reading.IsVisible = edit.IsVisible = true;
        if (save && next != text) commit(next);
        value.Content = render(text);
    }
}