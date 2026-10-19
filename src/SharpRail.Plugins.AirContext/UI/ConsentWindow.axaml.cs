using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.AirContext.UI;

internal sealed partial class ConsentWindow : Window
{
    private readonly IPluginUIContext context;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private ContextConsentResult? result;
    internal event Action<ContextConsentResult>? Updated;

    internal ConsentWindow(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        Title = "Air Context · Consent";
        Find<TextBlock>("ConsentHeading").PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        Find<CheckBox>("ConsentCheck").IsCheckedChanged += (_, _) => UpdateActions();
        Find<Button>("ConsentReload").Click += async (_, _) => await RunAsync("review");
        Find<Button>("ConsentDownload").Click += async (_, _) =>
        {
            if (result?.AcceptedAtUtc.Length > 0) await RunAsync("download");
            else if (Find<CheckBox>("ConsentCheck").IsChecked == true) await RunAsync("acceptAndDownload");
        };
        Find<Button>("ConsentAbout").Click += async (_, _) => await AirContextPane.OpenAboutAsync(this);
        Find<Button>("ConsentClose").Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Opened += async (_, _) => await RunAsync("review");
        Closed += (_, _) => lifetime.Cancel();
    }

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private void UpdateActions()
    {
        var reviewed = result is { Text.Length: > 0, State: "requiresAcceptance" or "accepted" or "changed" };
        var accepted = result?.AcceptedAtUtc.Length > 0;
        Find<CheckBox>("ConsentCheck").IsEnabled = !busy && reviewed && !accepted;
        Find<Button>("ConsentDownload").IsVisible = result?.State != "downloaded";
        Find<Button>("ConsentDownload").IsEnabled = !busy && reviewed && (accepted || Find<CheckBox>("ConsentCheck").IsChecked == true);
        Find<Button>("ConsentDownload").Content = accepted ? "Download CLI" : "Accept and download";
        Find<Button>("ConsentReload").IsEnabled = !busy;
        Find<Button>("ConsentClose").Content = busy ? "Cancel" : result?.State == "downloaded" ? "Done" : "Close";
    }

    private async Task RunAsync(string operation)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true; UpdateActions();
        Find<TextBlock>("ConsentStatus").Text = operation == "review" ? "Reading public agreements; no CLI download." : "Checking consent and downloading the verified CLI…";
        try
        {
            var request = new ContextConsentRequest(operation, result?.ArtifactId ?? "", result?.TermsSha256 ?? "");
            var next = await context.RequestAsync(AirContextContract.Consent, request, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            result = next;
            Updated?.Invoke(next);
            Find<TextBlock>("ConsentHeading").Text = next.State == "downloaded" ? "Air Context · Installation" : "Air Context · Review before download";
            Find<TextBlock>("ConsentIdentity").Text = next.Version.Length > 0 ? $"Build {next.Version}\n{next.Detail}\nTerms SHA-256: {next.TermsSha256}" : next.Detail;
            Find<TextBox>("ConsentText").Text = next.Text;
            Find<CheckBox>("ConsentCheck").IsChecked = next.AcceptedAtUtc.Length > 0;
            Find<TextBlock>("ConsentStatus").Text = next.State == "downloaded" ? "Downloaded and verified. Downloading does not start the CLI or enable workspace indexing.\n" + next.Installation :
                next.AcceptedAtUtc.Length > 0 ? "Accepted " + next.AcceptedAtUtc + ". Choose Download CLI to continue." :
                next.State == "requiresAcceptance" ? "Review the agreements, then check the box to download." : next.Detail;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { result = null; Find<TextBlock>("ConsentStatus").Text = error.Message; }
        finally { busy = false; UpdateActions(); }
    }
}