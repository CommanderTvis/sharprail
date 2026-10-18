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
                var result = await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), current.Token);
                if (lifetime != current) return;
                savedProvider = result.Configuration;
                RefreshProviderChoice(true);
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception error) { if (lifetime == current) this.FindControl<TextBlock>("JvmProviderMessage")!.Text = error.Message; }
        };
        DetachedFromVisualTree += (_, _) => { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; };
    }

    internal void UpdateProvider(string provider)
    {
        savedProvider = provider;
        RefreshProviderChoice(true);
    }

    private bool providerChoiceLoading;
    private bool providerChoiceDirty;
    private bool providerChoiceSaving;
    private int savedProviderChoice;

    private void RefreshProviderChoice(bool visible)
    {
        this.FindControl<StackPanel>("JvmProviderSection")!.IsVisible = visible;
        savedProviderChoice = savedProvider == "intellij" ? 1 : 0;
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
                return await host.LanguageAsync(new(workspace, Operation: "setJavaKotlinProvider", Configuration: choice == 1 ? "intellij" : "standard"), current.Token);
            }, current.Token);
            if (lifetime != current) return;
            if (result.State is "error" or "disabled" or "unavailable") { message.Text = result.Detail; return; }
            var next = await Task.Run(async () => await host.LanguageAsync(new(workspace, Operation: "javaKotlinProvider"), current.Token), current.Token);
            if (lifetime != current) return;
            providerChoiceDirty = false;
            savedProvider = next.Configuration;
            RefreshProviderChoice(true);
            message.Text = "Provider choice saved for every project on this host. Start a server when ready.";
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