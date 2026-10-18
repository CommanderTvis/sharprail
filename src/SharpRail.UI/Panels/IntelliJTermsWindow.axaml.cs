using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class IntelliJTermsWindow : Window
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private readonly bool startAfterAcceptance;
    internal bool StartRequested { get; private set; }
    internal IntelliJTermsResult? Result { get; private set; }

    internal IntelliJTermsWindow(IProjectServices host, string workspace, bool startAfterAcceptance = false)
    {
        this.host = host; this.workspace = workspace; this.startAfterAcceptance = startAfterAcceptance;
        AvaloniaXamlLoader.Load(this);
        Title = "IntelliJ · License agreement";
        if (startAfterAcceptance) Find<Button>("AcceptTerms").Content = "Accept and start";
        Find<TextBlock>("TermsHeading").PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        Find<CheckBox>("TermsConsent").IsCheckedChanged += (_, _) => UpdateActions();
        Find<Button>("ReloadTerms").Click += async (_, _) => await LoadAsync(false);
        Find<Button>("AcceptTerms").Click += async (_, _) =>
        {
            if (startAfterAcceptance && Result?.Accepted == true) { StartRequested = true; Close(); }
            else if (Find<CheckBox>("TermsConsent").IsChecked == true) await LoadAsync(true);
        };
        Find<Button>("CloseTerms").Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Opened += async (_, _) => await LoadAsync(false);
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    private T Find<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void UpdateActions()
    {
        var reviewable = Result is { State: "requiresAcceptance", Text.Length: > 0, Accepted: false };
        Find<CheckBox>("TermsConsent").IsEnabled = !busy && reviewable;
        Find<Button>("AcceptTerms").IsEnabled = !busy && (reviewable && Find<CheckBox>("TermsConsent").IsChecked == true || startAfterAcceptance && Result?.Accepted == true);
        if (startAfterAcceptance) Find<Button>("AcceptTerms").Content = Result?.Accepted == true ? "Start IntelliJ" : "Accept and start";
        Find<Button>("ReloadTerms").IsEnabled = !busy;
    }

    private async Task LoadAsync(bool accept)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        if (accept && Result is not { State: "requiresAcceptance", Accepted: false }) return;
        busy = true; UpdateActions();
        var token = lifetime.Token;
        try
        {
            var request = accept ? new IntelliJTermsRequest(workspace, true, Result!.ArtifactId, Result.EulaSha256) : new(workspace);
            var result = await host.IntelliJTermsAsync(request, token);
            if (token.IsCancellationRequested) return;
            Result = result;
            Find<CheckBox>("TermsConsent").IsChecked = result.Accepted;
            Find<TextBox>("TermsText").Text = result.Text;
            Find<TextBlock>("TermsIdentity").Text = result.Version.Length == 0 ? result.Detail :
                $"{result.Detail}\nBuild {result.Version}\nArtifact: {result.ArtifactId}\nEULA SHA-256: {result.EulaSha256}";
            ToolTip.SetTip(Find<TextBlock>("TermsIdentity"), result.Detail);
            Find<TextBlock>("TermsStatus").Text = result.Accepted ? "Accepted " + result.AcceptedAtUtc + ". Already accepted — choose Start IntelliJ to continue." :
                result.State == "changed" ? result.Detail : result.State == "requiresAcceptance" ? "No acceptance is recorded for this artifact and agreement." : result.Detail;
            if (result.State == "changed") Result = result with { State = "requiresAcceptance" };
            if (accept && result.Accepted && startAfterAcceptance) { StartRequested = true; Close(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) { Result = null; Find<TextBlock>("TermsStatus").Text = error.Message; }
        }
        finally { busy = false; UpdateActions(); }
    }
}