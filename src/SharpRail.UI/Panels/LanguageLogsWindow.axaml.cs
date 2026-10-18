using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguageLogsWindow : Window
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private readonly string server;
    private readonly bool context;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool busy;

    internal LanguageLogsWindow(IProjectServices host, string workspace, string server, bool context = false)
    {
        this.host = host; this.workspace = workspace; this.server = server; this.context = context;
        AvaloniaXamlLoader.Load(this);
        Title = context ? "JetBrains Context · Logs" : server + " · LSP logs";
        var heading = this.FindControl<TextBlock>("LogHeading")!;
        heading.Text = Title;
        heading.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(heading).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        this.FindControl<TextBlock>("LogWorkspace")!.Text = workspace;
        this.FindControl<Button>("CloseLogs")!.Click += (_, _) => Close();
        this.FindControl<Button>("RefreshLogs")!.Click += async (_, _) => await RefreshAsync();
        this.FindControl<Button>("CopyLogs")!.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(this.FindControl<TextBox>("ServerOutput")!.Text ?? "");
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        timer.Tick += async (_, _) => await RefreshAsync();
        Opened += (_, _) =>
        {
            var card = this.FindControl<Border>("LogCard")!;
            if (OperatingSystem.IsAndroid())
            {
                card.Margin = default;
                card.CornerRadius = new(12, 12, 0, 0);
                card.BorderThickness = new(0, 1, 0, 0);
            }
            else card.Effect = new DropShadowEffect { OffsetX = 0, OffsetY = 8, BlurRadius = 28, Opacity = 1, Color = Ui.DialogShadow.Color };
            timer.Start(); _ = RefreshAsync();
        };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); };
    }

    private async Task RefreshAsync()
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true;
        var token = lifetime.Token;
        try
        {
            var result = await Task.Run(async () =>
            {
                if (!context) return await host.LanguageAsync(new(workspace, Operation: "logs", Server: server), token);
                var output = await host.ContextAsync(new(workspace, "logs"), token);
                return new LanguageResult(output.State, output.Data);
            }, token);
            if (token.IsCancellationRequested) return;
            var output = this.FindControl<TextBox>("ServerOutput")!;
            if (output.Text != result.Detail) output.Text = result.Detail;
            this.FindControl<TextBlock>("LogStatus")!.Text = result.State == "ready"
                ? result.Truncated ? "Live · older output omitted" : "Live · retained through Stop / Retry"
                : result.State;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) this.FindControl<TextBlock>("LogStatus")!.Text = error.Message; }
        finally { busy = false; }
    }
}