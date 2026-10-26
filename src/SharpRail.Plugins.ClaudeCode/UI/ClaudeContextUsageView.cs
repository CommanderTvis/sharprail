using System.Globalization;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The state of the Context surface's baseline (<c>claude -p /context</c> for a fresh session started like the tab's):
/// measuring, when it was measured, or a failure with Retry. The costs themselves are rows of the context
/// table. Static layout lives in the compiled AXAML.
/// </summary>
internal sealed partial class ClaudeContextUsageView : UserControl
{
    private readonly TextBlock message;
    private readonly Button retry;
    private object? shown;

    /// <summary>Raised when the user asks to measure again after a failure.</summary>
    public event Action? RetryRequested;

    public ClaudeContextUsageView()
    {
        AvaloniaXamlLoader.Load(this);
        message = this.FindControl<TextBlock>("ClaudeContextMessage")!;
        retry = this.FindControl<Button>("ClaudeContextRetry")!;
        retry.Click += (_, _) => RetryRequested?.Invoke();
    }

    /// <summary>"15.1k", "1m" or "640", as Claude Code abbreviates token counts.</summary>
    public static string Tokens(long tokens)
    {
        static string Trim(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        return tokens < 1_000 ? tokens.ToString(CultureInfo.InvariantCulture) : tokens < 1_000_000 ? Trim(tokens / 1_000.0) + "k" : Trim(tokens / 1_000_000.0) + "m";
    }

    /// <summary>Shows the host's answer; the same answer twice does nothing.</summary>
    public void Show(ClaudeContextUsage? usage)
    {
        var key = (usage, Ui.Theme);
        if (Equals(shown, key)) return;
        shown = key;
        message.Foreground = usage?.Error is not null ? Ui.Danger : Ui.Hint;
        ToolTip.SetTip(message, usage is { Running: false, Error: null, MeasuredAt: { } stamp } && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? ClaudeParts.Absolute(when) : null);
        message.Text = usage switch
        {
            null or { Running: true, Breakdown: null } => "Measuring with claude -p /context…",
            { Error: { } error } => "Could not measure the context: " + error,
            { Running: true } => "Measuring again…",
            { MeasuredAt: { } at } when DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var measured) =>
                "Measured " + ClaudeParts.Ago(measured),
            _ => ""
        };
        retry.IsVisible = usage is { Error: not null, Running: false };
    }
}