using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Interactivity;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed partial class LanguageProviderView : UserControl
{
    private readonly IProjectServices host;
    private readonly string workspace;
    private CancellationTokenSource? lifetime;
    private IReadOnlyList<LanguageServerState> inventory = [];

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
                var result = await host.LanguageAsync(new(workspace, Operation: "servers"), current.Token);
                if (lifetime != current) return;
                inventory = result.Servers;
                RefreshProviderChoice(true);
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("JvmProviderMessage")!.Text = error.Message; }
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    private bool providerChoiceLoading;
    private bool providerChoiceDirty;
    private bool providerChoiceSaving;
    private int savedProviderChoice;

    private void RefreshProviderChoice(bool visible)
    {
        this.FindControl<StackPanel>("JvmProviderSection")!.IsVisible = visible;
        var configuration = inventory.FirstOrDefault(server => server.Id == "intellij")?.Configuration;
        savedProviderChoice = JsonNode.Parse(string.IsNullOrEmpty(configuration) ? "{}" : configuration)?["useForJavaAndKotlin"]?.GetValue<bool>() == true ? 1 : 0;
        if (!providerChoiceDirty && !providerChoiceSaving)
        {
            providerChoiceLoading = true;
            this.FindControl<ComboBox>("JvmProvider")!.SelectedIndex = savedProviderChoice;
            providerChoiceLoading = false;
        }
        this.FindControl<Button>("ApplyJvmProvider")!.IsEnabled = !providerChoiceSaving && this.FindControl<ComboBox>("JvmProvider")!.SelectedIndex != savedProviderChoice;
    }

    private void ProviderChoiceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (providerChoiceLoading) return;
        providerChoiceDirty = this.FindControl<ComboBox>("JvmProvider")!.SelectedIndex != savedProviderChoice;
        this.FindControl<Button>("ApplyJvmProvider")!.IsEnabled = providerChoiceDirty && !providerChoiceSaving;
    }

    private async void ApplyJvmProviderClicked(object? sender, RoutedEventArgs e)
    {
        if (providerChoiceSaving || lifetime is not { } current) return;
        var choice = this.FindControl<ComboBox>("JvmProvider")!.SelectedIndex;
        if (choice is not (0 or 1)) return;
        providerChoiceSaving = true;
        this.FindControl<ComboBox>("JvmProvider")!.IsEnabled = false;
        this.FindControl<Button>("ApplyJvmProvider")!.IsEnabled = false;
        var message = this.FindControl<TextBlock>("JvmProviderMessage")!;
        message.Text = "Saving provider choice…";
        try
        {
            var result = await Task.Run(async () =>
            {
                var saved = await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "intellij"), current.Token);
                var options = JsonNode.Parse(string.IsNullOrEmpty(saved.Configuration) ? "{}" : saved.Configuration)!.AsObject();
                options["useForJavaAndKotlin"] = choice == 1;
                return await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: options.ToJsonString()), current.Token);
            }, current.Token);
            if (lifetime != current) return;
            if (result.State is "error" or "disabled" or "unavailable") { message.Text = result.Detail; return; }
            var next = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "servers"), current.Token), current.Token);
            if (lifetime != current) return;
            providerChoiceDirty = false;
            inventory = next.Servers;
            RefreshProviderChoice(true);
            message.Text = "Provider choice saved. Start a server when ready.";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (Exception error) { if (lifetime == current) message.Text = error.Message; }
        finally
        {
            providerChoiceSaving = false;
            if (lifetime == current)
            {
                this.FindControl<ComboBox>("JvmProvider")!.IsEnabled = true;
                RefreshProviderChoice(true);
            }
        }
    }
}