using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.AirContext.UI;

/// <summary>A workspace's retained, redacted Context output, refreshed while open.</summary>
internal sealed partial class LogsWindow : Window
{
    private readonly IPluginUIContext context;
    private readonly string workspace;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool busy;

    internal LogsWindow(IPluginUIContext context, string workspace)
    {
        this.context = context; this.workspace = workspace;
        AvaloniaXamlLoader.Load(this);
        Title = "JetBrains Context · Logs";
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
            var result = await context.RequestAsync(AirContextContract.Workspace, new(workspace, "logs"), token);
            if (token.IsCancellationRequested) return;
            var output = this.FindControl<TextBox>("ServerOutput")!;
            if (output.Text != result.Data) output.Text = result.Data;
            this.FindControl<TextBlock>("LogStatus")!.Text = result.State == "ready" ? "Live · retained through Stop / Retry" : result.State;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) this.FindControl<TextBlock>("LogStatus")!.Text = error.Message; }
        finally { busy = false; }
    }
}