using Avalonia.Controls;
using Avalonia.Interactivity;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguageProviderView : UserControl
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private CancellationTokenSource? lifetime;
    private string savedProvider = "standard";
    /// <summary>The choices: <c>standard</c>, then each server that replaces others (H21).</summary>
    private string[] choices = ["standard"];

    internal LanguageProviderView(IProjectServices host, string workspace)
    {
        this.host = host;
        this.workspace = workspace;
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += async (_, _) =>
        {
            lifetime = new();
            var current = lifetime;
            try
            {
                var catalog = await host.LanguageAsync(new(workspace, Operation: "catalog"), current.Token);
                var result = await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), current.Token);
                if (lifetime != current) return;
                LanguageServerNameConverter.Remember(catalog.Servers);
                var replacing = catalog.Servers.Where(server => server.Presentation?.Replaces is { Count: > 0 }).ToArray();
                choices = ["standard", .. replacing.Select(server => server.Id)];
                var replaced = replacing.SelectMany(server => server.Presentation!.Replaces!).Distinct().ToArray();
                var label = replacing.Select(server => server.Presentation!.ReplacesLabel).FirstOrDefault(text => text.Length > 0) ?? string.Join(" / ", replaced.Select(LanguageServerNameConverter.Display));
                this.FindControl<TextBlock>("ProviderChoiceHeading")!.Text = label + " provider · all projects on this host";
                providerChoiceLoading = true;
                this.FindControl<ComboBox>("ProviderChoice")!.ItemsSource = new[] { string.Join(" + ", replaced.Select(LanguageServerNameConverter.Display)) }
                    .Concat(replacing.Select(server => LanguageServerNameConverter.Display(server.Id))).ToArray();
                providerChoiceLoading = false;
                savedProvider = result.Configuration;
                RefreshProviderChoice(replacing.Length > 0);
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ProviderChoiceMessage")!.Text = error.Message; }
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    internal void UpdateProvider(string provider)
    {
        savedProvider = provider;
        RefreshProviderChoice(choices.Length > 1);
    }

    private bool providerChoiceLoading;
    private bool providerChoiceDirty;
    private bool providerChoiceSaving;
    private int savedProviderChoice;

    private void RefreshProviderChoice(bool visible)
    {
        this.FindControl<StackPanel>("ProviderChoiceSection")!.IsVisible = visible;
        savedProviderChoice = Math.Max(0, Array.IndexOf(choices, savedProvider));
        if (!providerChoiceDirty && !providerChoiceSaving)
        {
            providerChoiceLoading = true;
            this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex = savedProviderChoice;
            providerChoiceLoading = false;
        }
        this.FindControl<Button>("ApplyProviderChoice")!.IsEnabled = !providerChoiceSaving && this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex != savedProviderChoice;
    }

    private void ProviderChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (providerChoiceLoading) return;
        providerChoiceDirty = this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex != savedProviderChoice;
        this.FindControl<Button>("ApplyProviderChoice")!.IsEnabled = providerChoiceDirty && !providerChoiceSaving;
    }

    private async void ApplyProviderChoiceClicked(object? sender, RoutedEventArgs e)
    {
        if (providerChoiceSaving || lifetime is not { } current) return;
        var choice = this.FindControl<ComboBox>("ProviderChoice")!.SelectedIndex;
        if (choice < 0 || choice >= choices.Length) return;
        var chosen = choices[choice];
        providerChoiceSaving = true;
        this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = false;
        this.FindControl<Button>("ApplyProviderChoice")!.IsEnabled = false;
        var message = this.FindControl<TextBlock>("ProviderChoiceMessage")!;
        message.Text = "Saving provider choice…";
        try
        {
            var result = await Task.Run(async () =>
            {
                return await host.LanguageAsync(new(workspace, Operation: "setJavaKotlinProvider", Configuration: chosen), current.Token);
            }, current.Token);
            if (lifetime != current) return;
            if (result.State is "error" or "disabled" or "unavailable") { message.Text = result.Detail; return; }
            var next = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), current.Token), current.Token);
            if (lifetime != current) return;
            providerChoiceDirty = false;
            savedProvider = next.Configuration;
            RefreshProviderChoice(choices.Length > 1);
            message.Text = "Provider choice saved for every project on this host. Start a server when ready.";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) message.Text = error.Message; }
        finally
        {
            providerChoiceSaving = false;
            if (lifetime == current)
            {
                this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = true;
                RefreshProviderChoice(choices.Length > 1);
            }
        }
    }
}