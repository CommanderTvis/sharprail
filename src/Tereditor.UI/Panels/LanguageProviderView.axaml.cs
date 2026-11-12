using Avalonia.Controls;
using Avalonia.Interactivity;

using Tereditor.Host.Abstractions;

namespace Tereditor.UI.Panels;

internal sealed partial class LanguageProviderView : UserControl
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private CancellationTokenSource? lifetime;
    private string savedProvider = "standard";
    /// <summary>The choices: <c>standard</c>, then each server that replaces others (H21).</summary>
    private string[] choices = ["standard"];
    private HashSet<string> withTerms = [];

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
                var catalog = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "catalog"), current.Token), current.Token);
                var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), current.Token), current.Token);
                if (lifetime != current) return;
                LanguageServerNameConverter.Remember(catalog.Servers);
                var replacing = catalog.Servers.Where(server => server.Presentation?.Replaces is { Count: > 0 }).ToArray();
                choices = JavaKotlinProviderOptions.Available(catalog.Servers.Select(server => server.Id), replacing.Select(server => (server.Id, server.Presentation!.Replaces!)));
                withTerms = replacing.Where(server => server.Presentation?.HasTerms == true).Select(server => server.Id).ToHashSet();
                var replaced = replacing.SelectMany(server => server.Presentation!.Replaces!).Distinct().ToArray();
                var label = replacing.Select(server => server.Presentation!.ReplacesLabel).FirstOrDefault(text => text.Length > 0) ?? string.Join(" / ", replaced.Select(LanguageServerNameConverter.Display));
                this.FindControl<TextBlock>("ProviderChoiceHeading")!.Text = label + " provider · all projects on this host";
                providerChoiceLoading = true;
                var standardLabel = string.Join(" + ", replaced.Select(LanguageServerNameConverter.Display));
                this.FindControl<ComboBox>("ProviderChoice")!.ItemsSource = choices.Select(id => id == JavaKotlinProviderOptions.Standard ? standardLabel : LanguageServerNameConverter.Display(id)).ToArray();
                providerChoiceLoading = false;
                savedProvider = result.Configuration;
                RefreshProviderChoice();
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("ProviderChoiceMessage")!.Text = error.Message; }
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    internal void UpdateProvider(string provider)
    {
        savedProvider = provider;
        RefreshProviderChoice();
    }

    private bool providerChoiceLoading;
    private bool providerChoiceDirty;
    private bool providerChoiceSaving;
    private int savedProviderChoice;

    /// <summary>Offered only while its plugins are enabled: one option is a plain disabled choice, none hides the section, and a saved choice that is not offered shows the one in effect.</summary>
    private void RefreshProviderChoice()
    {
        this.FindControl<StackPanel>("ProviderChoiceSection")!.IsVisible = choices.Length > 0;
        var effective = JavaKotlinProviderOptions.Effective(savedProvider, choices);
        savedProviderChoice = Math.Max(0, Array.IndexOf(choices, effective));
        var single = choices.Length == 1;
        this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = !single && !providerChoiceSaving;
        this.FindControl<Button>("ApplyProviderChoice")!.IsVisible = !single;
        this.FindControl<TextBlock>("ProviderChoiceEffective")!.Text = effective != savedProvider && choices.Length > 0
            ? $"The saved choice's plugins are off, so {(effective == JavaKotlinProviderOptions.Standard ? "the standard providers are" : LanguageServerNameConverter.Display(effective) + " is")} in effect." : "";
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
            RefreshProviderChoice();
            message.Text = withTerms.Contains(chosen)
                ? $"Provider choice saved. {LanguageServerNameConverter.Display(chosen)} needs its license terms accepted before it can start: select it in the server list below and choose Review and accept terms."
                : "Provider choice saved for every project on this host. Start a server when ready.";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) message.Text = error.Message; }
        finally
        {
            providerChoiceSaving = false;
            if (lifetime == current)
            {
                this.FindControl<ComboBox>("ProviderChoice")!.IsEnabled = true;
                RefreshProviderChoice();
            }
        }
    }
}