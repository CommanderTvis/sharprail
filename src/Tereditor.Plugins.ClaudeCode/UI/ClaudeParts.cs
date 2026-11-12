using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>The Claude brand mark from this plugin's own <c>claude.svg</c>, read once and tinted like any glyph.</summary>
internal sealed class ClaudeGlyph(IPluginUIContext context)
{
    private Task<byte[]?>? svg;

    private Task<byte[]?> Svg() => svg ??= Read();

    private async Task<byte[]?> Read()
    {
        try { return await context.ReadAssetAsync("claude.svg"); }
        catch (Exception error) when (error is PluginCallException or IOException or InvalidOperationException) { return null; }
    }

    public void Preload() => _ = Svg();

    public Control Create(double size = 14, IBrush? color = null)
    {
        var host = new ContentControl { Name = "ClaudeGlyph", Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var pending = Svg();
        if (pending.IsCompletedSuccessfully) host.Content = Draw(pending.Result, size, color);
        else
        {
            host.Content = Ui.Icon("robot", color ?? Ui.Muted, size);
            _ = pending.ContinueWith(task => Dispatcher.UIThread.Post(() => host.Content = Draw(task.Result, size, color)), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        return host;
    }

    private static Control Draw(byte[]? bytes, double size, IBrush? color) =>
        bytes is null ? Ui.Icon("robot", color ?? Ui.Muted, size) : new SvgAsset(bytes, size, color ?? Ui.Muted);
}

/// <summary>The small shared pieces of the configuration pane and its dialogs.</summary>
internal static class ClaudeParts
{
    /// <summary>The one phrasing of a past time across the Claude panes: "just now", "5m ago", "3h ago" or "2d ago".</summary>
    public static string Ago(DateTimeOffset at)
    {
        var minutes = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - at).TotalMinutes, MidpointRounding.AwayFromZero));
        if (minutes < 1) return "just now";
        if (minutes < 60) return $"{minutes}m ago";
        var hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        return hours < 48 ? $"{hours}h ago" : $"{(int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero)}d ago";
    }

    /// <summary>The absolute local time shown in the tooltip of an <see cref="Ago"/> label.</summary>
    public static string Absolute(DateTimeOffset at) => at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public static string ErrorText(Exception error, string fallback = "The request failed.") => error.Message is { Length: > 0 } message ? message : fallback;

    public static Window? Owner(Control control) => TopLevel.GetTopLevel(control) as Window;

    public static Border ScopeChip(string scope) => ScopedSetting.ScopeChip(scope, "ClaudeScopeChip");

    public static Button SourceButton(string path, IReadOnlyList<string>? keyPath, Action<string, IReadOnlyList<string>?> open) =>
        ScopedSetting.SourcePath(path, opened => open(opened, keyPath), "ClaudeOpenSource");

    public static TextBlock Wrapped(string text, IBrush? color = null, double size = 12)
    {
        var block = Ui.Text(text, color ?? Ui.Muted, size);
        block.TextWrapping = TextWrapping.Wrap;
        block.TextTrimming = TextTrimming.None;
        return block;
    }

    public static TextBlock Code(string text, IBrush? color = null, double size = 12)
    {
        var block = Ui.Text(text, color ?? Ui.Muted, size);
        block.FontFamily = Ui.CodeFont;
        return block;
    }

    public static StackPanel Field(string label, Control input, string? hint = null)
    {
        var field = new StackPanel { Spacing = 4 };
        field.Children.Add(Ui.Text(label, Ui.Muted, 12));
        field.Children.Add(input);
        if (hint is not null) field.Children.Add(Wrapped(hint, Ui.Hint));
        return field;
    }

    public static TextBox Input(string name, string? placeholder = null, bool multiline = false) => new()
    {
        Name = name,
        PlaceholderText = placeholder,
        FontFamily = Ui.CodeFont,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinHeight = multiline ? 64 : 0
    };

    public static Button Primary(Button button)
    {
        button.Classes.Add("primary");
        return button;
    }
}