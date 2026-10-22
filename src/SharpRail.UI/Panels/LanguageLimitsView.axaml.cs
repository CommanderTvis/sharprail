using System.Globalization;
using System.Text.Json.Nodes;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Panels;

/// <summary>
/// Settings › Code Intelligence: every running language server on the host with its workspace and memory, the memory budget, and the
/// limits (idle timeout, budget, count caps), saved as the host setting <c>language-limits</c> and refreshed from its broadcast.
/// </summary>
internal sealed partial class LanguageLimitsView : UserControl
{
    private static readonly string[] JvmServers = ["intellij", "kotlin", "scala"];
    private readonly IProjectServices host;
    private readonly string workspace;
    private readonly Func<string> current;
    private readonly Func<string, Task> save;
    private CancellationTokenSource? lifetime;
    private JsonObject defaults = new();

    internal LanguageLimitsView(IProjectServices host, string workspace, Func<string> current, Func<string, Task> save)
    {
        this.host = host; this.workspace = workspace; this.current = current; this.save = save;
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) =>
        {
            lifetime = new();
            _ = PollAsync(lifetime.Token);
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    /// <summary>Shows the saved limits again, after this or another client changed them.</summary>
    internal void Refresh() => ShowLimits();

    private async Task PollAsync(CancellationToken token)
    {
        var first = true;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "running"), token), token);
                if (token.IsCancellationRequested) return;
                Show(result);
                if (first) { first = false; ShowLimits(); }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                Control<TextBlock>("LimitsSummary").Text = "Running servers unavailable: " + error.Message;
                return;
            }
        }
    }

    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private static string Mib(long bytes) => (bytes / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + " MiB";

    private void Show(LanguageResult result)
    {
        var summary = result.Configuration.Length > 0 ? JsonNode.Parse(result.Configuration) as JsonObject : null;
        if (summary is null) { Control<TextBlock>("LimitsSummary").Text = result.Detail; return; }
        defaults = summary["defaultCaps"] as JsonObject ?? new();
        var budget = summary["budgetBytes"]?.GetValue<long>() ?? 0;
        var total = summary["totalBytes"]?.GetValue<long>() ?? 0;
        var physical = summary["physicalBytes"]?.GetValue<long>() ?? 0;
        var automatic = summary["automaticBudget"]?.GetValue<bool>() == true;
        Control<TextBlock>("LimitsSummary").Text = $"{result.Servers.Count} running · {Mib(total)} of the {Mib(budget)} budget" +
            (automatic ? $" (automatic: 30% of {Mib(physical)} physical memory, 3–24 GiB)" : "") +
            (summary["pressure"]?.GetValue<bool>() == true ? " · the system reports memory pressure" : "");
        var rows = Control<StackPanel>("RunningServers");
        rows.Children.Clear();
        foreach (var server in result.Servers.OrderByDescending(server => server.MemoryBytes))
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
            var idle = server.IdleSeconds < 60 ? "used just now" : $"idle {server.IdleSeconds / 60} min";
            var text = new TextBlock
            {
                Text = $"{LanguageServerNameConverter.Display(server.Id)} · {Path.GetFileName(server.Workspace)} · {server.Detail} · {idle}",
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(text, server.Workspace);
            Ui.Place(row, text, 0, 0);
            Ui.Place(row, new TextBlock { Text = server.MemoryBytes > 0 ? Mib(server.MemoryBytes) : "—", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted }, 0, 1);
            var id = server.Id; var root = server.Workspace;
            var stop = new Button { Content = "Stop" };
            AutomationProperties.SetName(stop, $"Stop {id} in {root}");
            stop.Click += (_, _) => _ = StopAsync(root, id);
            Ui.Place(row, stop, 0, 2);
            rows.Children.Add(row);
        }
    }

    private async Task StopAsync(string root, string id)
    {
        var token = lifetime?.Token ?? CancellationToken.None;
        try
        {
            var result = await Task.Run(async () => await host.LanguageAsync(new(root, Operation: "release", Server: id), token), token);
            Control<TextBlock>("LimitsMessage").Text = result.Detail;
            Show(await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "running"), token), token));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Control<TextBlock>("LimitsMessage").Text = error.Message; }
    }

    private void ShowLimits()
    {
        JsonObject saved;
        try { saved = JsonNode.Parse(current() is { Length: > 0 } json ? json : "{}") as JsonObject ?? new(); }
        catch (System.Text.Json.JsonException) { saved = new(); }
        var caps = saved["caps"] as JsonObject ?? new();
        int Cap(string id, int fallback) => caps[id]?.GetValue<int>() ?? defaults[id]?.GetValue<int>() ?? fallback;
        Control<TextBox>("LimitsIdle").Text = (saved["idleMinutes"]?.GetValue<int>() ?? 10).ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("LimitsBudget").Text = (saved["memoryBudgetMb"]?.GetValue<int>() ?? 0).ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("LimitsCSharp").Text = Cap("csharp", 4).ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("LimitsJvm").Text = Cap("kotlin", 2).ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("LimitsOther").Text = Cap("*", 6).ToString(CultureInfo.InvariantCulture);
    }

    private async void SaveClicked(object? sender, RoutedEventArgs e)
    {
        var message = Control<TextBlock>("LimitsMessage");
        static int? Number(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
        if (Number(Control<TextBox>("LimitsIdle").Text) is not { } idle || Number(Control<TextBox>("LimitsBudget").Text) is not { } budget ||
            Number(Control<TextBox>("LimitsCSharp").Text) is not { } csharp || Number(Control<TextBox>("LimitsJvm").Text) is not { } jvm || Number(Control<TextBox>("LimitsOther").Text) is not { } other)
        {
            message.Foreground = Ui.Danger; message.Text = "Enter whole numbers.";
            return;
        }
        var caps = new JsonObject { ["csharp"] = csharp, ["*"] = other };
        foreach (var id in JvmServers) caps[id] = jvm;
        var json = new JsonObject { ["idleMinutes"] = idle, ["memoryBudgetMb"] = budget, ["caps"] = caps }.ToJsonString();
        var button = Control<Button>("LimitsSave");
        button.IsEnabled = false;
        try
        {
            await Task.Run(() => save(json));
            message.Foreground = Ui.Muted; message.Text = "Saved; applies to running servers now.";
        }
        catch (Exception error) { message.Foreground = Ui.Danger; message.Text = error.Message; }
        finally { button.IsEnabled = true; }
    }
}
