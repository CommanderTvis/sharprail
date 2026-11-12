using System.Globalization;
using System.Text.Json.Nodes;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI.Panels;

/// <summary>
/// The Running section of the Code Intelligence tool window: every running language server on the host grouped by workspace (the current one first)
/// with its state, memory and idle time, each with a Stop. Collapsible; it starts collapsed when more than a handful of servers run.
/// </summary>
internal sealed class LanguageRunningView : UserControl
{
    private const int ExpandedRows = 5;
    private readonly IProjectServices host;
    private readonly Button header = new() { Classes = { "link" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel body = new() { Spacing = 2, Name = "RunningServers" };
    private readonly TextBlock message = new() { FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? lifetime;
    private bool? open;
    private string summary = "Reading running servers…";
    private int count;

    internal LanguageRunningView(IProjectServices host, string workspace)
    {
        this.host = host; Workspace = workspace;
        AutomationProperties.SetName(body, "Running language servers");
        header.Click += (_, _) => { open = !IsOpen; Render(); };
        Content = new StackPanel { Spacing = 4, Children = { header, body, message } };
        Render();
        AttachedToVisualTree += (_, _) => { lifetime = new(); _ = PollAsync(lifetime.Token); };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    /// <summary>The pane's current workspace, listed first.</summary>
    internal string Workspace { get; set; }

    private bool IsOpen => open ?? count <= ExpandedRows;

    private static string Mib(long bytes) => (bytes / (1024 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + Units.NoBreak + "MiB";

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (IsEffectivelyVisible) await ReadAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) { summary = "Running servers unavailable: " + error.Message; count = 0; body.Children.Clear(); Render(); return; }
        }
    }

    private async Task ReadAsync(CancellationToken token)
    {
        var result = await Task.Run(async () => await host.LanguageAsync(new(Workspace, Operation: "running"), token), token);
        if (token.IsCancellationRequested) return;
        Show(result);
    }

    private void Render()
    {
        header.Content = (IsOpen ? "▾ " : "▸ ") + "Running · " + summary;
        body.IsVisible = IsOpen;
    }

    internal void Show(LanguageResult result)
    {
        var total = result.Configuration.Length > 0 && JsonNode.Parse(result.Configuration) is JsonObject json ? json["totalBytes"]?.GetValue<long>() ?? 0 : 0;
        count = result.Servers.Count;
        summary = $"{count} running · {Mib(total)}";
        body.Children.Clear();
        foreach (var group in result.Servers.GroupBy(server => server.Workspace)
                     .OrderByDescending(group => group.Key == Workspace).ThenBy(group => Path.GetFileName(group.Key), StringComparer.OrdinalIgnoreCase))
        {
            var title = new TextBlock { Text = Path.GetFileName(group.Key), FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, group.Key);
            body.Children.Add(title);
            foreach (var server in group.OrderByDescending(server => server.MemoryBytes))
                body.Children.Add(Row(server));
        }
        Render();
    }

    private Control Row(LanguageServerState server)
    {
        var row = new Grid { RowDefinitions = new("Auto,Auto"), ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        Ui.Place(row, new TextBlock { Text = LanguageServerNameConverter.Display(server.Id), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 0, 0);
        Ui.Place(row, new TextBlock { Text = server.MemoryBytes > 0 ? Mib(server.MemoryBytes) : "—", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted }, 0, 1);
        var id = server.Id; var root = server.Workspace;
        var stop = new Button { Content = "Stop" };
        AutomationProperties.SetName(stop, $"Stop {id} in {root}");
        stop.Click += (_, _) => _ = StopAsync(root, id);
        Ui.Place(row, stop, 0, 2);
        var idle = server.IdleSeconds < 60 ? "used just now" : $"idle {Units.Pair(server.IdleSeconds / 60, "min")}";
        var detail = new TextBlock { Text = $"{server.Detail} · {idle}", FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(detail, server.Detail);
        Grid.SetRow(detail, 1); Grid.SetColumnSpan(detail, 2);
        row.Children.Add(detail);
        return row;
    }

    private async Task StopAsync(string root, string id)
    {
        var token = lifetime?.Token ?? CancellationToken.None;
        try
        {
            var result = await Task.Run(async () => await host.LanguageAsync(new(root, Operation: "release", Server: id), token), token);
            message.Text = result.Detail;
            await ReadAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { message.Text = error.Message; }
    }
}