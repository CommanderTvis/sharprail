using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Providers.UI;

/// <summary>
/// Settings › Model providers: Pi's or omp's providers (a switch at the top), the connected ones with Remove, the rest with
/// Add key, and a form for a provider the lists do not name. Keys are typed into masked fields, sent once and never shown again.
/// Subscription sign-ins are not offered: every row takes an API key.
/// </summary>
internal sealed class ProvidersView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly StackPanel list = new() { Spacing = 8, Name = "ProvidersList" };
    private readonly TextBlock location = Ui.Text("", Ui.Muted, 12);
    private readonly TextBlock note = Ui.Text("", Ui.Muted, 12);
    private readonly ToggleButton pi = new() { Content = "Pi", Name = "ProvidersPi" };
    private readonly TextBlock intro = Ui.Text("", Ui.Muted, 12);
    private readonly ToggleButton omp = new() { Content = "omp", Name = "ProvidersOmp" };
    private ProviderAgent agent = ProviderAgent.Pi;
    private string? adding;
    private bool custom;
    private int generation;
    private bool tab;

    public ProvidersView(IPluginUIContext context)
    {
        this.context = context;
        Name = "SettingsProviders";
        Spacing = 16;
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        location.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var switcher = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pi.Click += (_, _) => Choose(ProviderAgent.Pi);
        omp.Click += (_, _) => Choose(ProviderAgent.Omp);
        switcher.Children.Add(pi);
        switcher.Children.Add(omp);
        Children.Add(Ui.Text("Model providers", Ui.TextBrush, 15));
        intro.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        Children.Add(intro);
        Children.Add(switcher);
        // omp is a program of the host's operating system: a host that refuses its plugin (a browser tab's) has no omp to configure.
        void ShowAgents()
        {
            var offered = context.Host().Roster.FirstOrDefault(entry => entry.Id == "omp") is { Status: not PluginStatus.Refused };
            switcher.IsVisible = offered;
            tab = !offered;
            intro.Text = offered
                ? "API keys for the providers Pi and omp use. Each agent keeps its own keys, in the file it reads. Sign-ins with a subscription are not offered here."
                : "API keys for the providers Pi uses. Sign-ins with a subscription are not offered here.";
            if (!offered && agent != ProviderAgent.Pi) Choose(ProviderAgent.Pi);
        }
        ShowAgents();
        AttachedToVisualTree += (_, _) => ShowAgents();
        Children.Add(location);
        Children.Add(note);
        Children.Add(list);
        Choose(ProviderAgent.Pi);
    }

    private void Choose(ProviderAgent next)
    {
        agent = next;
        adding = null;
        custom = false;
        pi.IsChecked = next == ProviderAgent.Pi;
        omp.IsChecked = next == ProviderAgent.Omp;
        _ = LoadAsync(() => context.RequestAsync(ProvidersContract.List, new ProvidersParams(next)).AsTask());
    }

    private async Task LoadAsync(Func<Task<ProvidersReport>> request)
    {
        var mine = ++generation;
        try
        {
            var report = await request();
            if (mine == generation) Render(report);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (mine == generation) context.Notify(PluginNotificationKind.Error, "Couldn't change the provider", error.Message);
        }
    }

    private void Render(ProvidersReport report)
    {
        location.Text = "Stored in " + report.Location;
        note.Text = report.Note ?? "";
        note.IsVisible = report.Note is not null;
        list.Children.Clear();
        // A browser tab's Pi can sign in to JetBrains AI, which no other Pi knows, so the row exists before a token is stored.
        var providers = tab && report.Agent == ProviderAgent.Pi && report.Providers.All(provider => provider.Id != JetBrainsAi.Id)
            ? [new ProviderEntry(JetBrainsAi.Id, "JetBrains AI", null, ProviderSource.None), .. report.Providers]
            : report.Providers;
        var connected = providers.Where(provider => provider.Configured).ToList();
        var rest = providers.Where(provider => !provider.Configured).ToList();
        if (connected.Count > 0)
        {
            list.Children.Add(Ui.Text("Connected", Ui.Muted, 12));
            foreach (var provider in connected) list.Children.Add(Row(report, provider));
        }
        list.Children.Add(Ui.Text("Available", Ui.Muted, 12));
        foreach (var provider in rest) list.Children.Add(Row(report, provider));
        list.Children.Add(CustomForm(report));
    }

    private Control Row(ProvidersReport report, ProviderEntry provider)
    {
        var column = new StackPanel { Spacing = 4, Name = "ProviderRow-" + provider.Id };
        var line = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        DockPanel.SetDock(actions, Dock.Right);
        if (provider.Source is ProviderSource.Key)
            actions.Children.Add(Ui.Button("Remove", () => _ = LoadAsync(() => context.RequestAsync(ProvidersContract.RemoveKey, new RemoveProviderKeyParams(agent, provider.Id)).AsTask())));
        if (provider.Source is not ProviderSource.SignIn)
            actions.Children.Add(Ui.Button(provider.Source is ProviderSource.Key ? "Replace key" : "Add key", () =>
            {
                adding = adding == provider.Id ? null : provider.Id;
                custom = false;
                Render(report);
            }));
        line.Children.Add(actions);
        var label = provider.Name + (provider.Id == provider.Name ? "" : "  ·  " + provider.Id);
        line.Children.Add(Ui.Text(label + SourceTag(provider.Source), Ui.TextBrush, 13));
        column.Children.Add(line);
        if (provider.Note is { } text)
        {
            var hint = Ui.Text(text, provider.Source == ProviderSource.SignIn ? Ui.Warning : Ui.Muted, 12);
            hint.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            column.Children.Add(hint);
        }
        if (provider.Id == ProviderCatalog.AnthropicId && provider.Source is not ProviderSource.Key) column.Children.Add(CreditsHint());
        if (provider.Id == JetBrainsAi.Id) column.Children.Add(JetBrainsAi.Notice());
        if (adding == provider.Id) column.Children.Add(provider.Id == JetBrainsAi.Id ? JetBrainsForm(report) : KeyForm(report, provider.Id, provider.Name, provider.EnvVar, null));
        return column;
    }

    // Anthropic takes an API key only: a Claude subscription sign-in is not offered, so say where a subscriber gets API credit.
    private static Control CreditsHint()
    {
        var hint = new StackPanel { Spacing = 4 };
        var text = Ui.Text("Claude Max or Team plan? It includes monthly API credits ($100–$500): claim them in claude.ai billing, create an API key in the Claude Console, and paste it here. A Claude subscription sign-in is not supported.", Ui.Muted, 12);
        text.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        hint.Children.Add(text);
        var link = Ui.Button("About API credits for subscribers", () => { });
        link.Click += (_, _) => TopLevel.GetTopLevel(link)?.Launcher.LaunchUriAsync(new Uri("https://platform.claude.com/docs/en/about-claude/api-credits-for-subscribers"));
        link.HorizontalAlignment = HorizontalAlignment.Left;
        hint.Children.Add(link);
        return hint;
    }

    private Control JetBrainsForm(ProvidersReport report)
    {
        var form = new StackPanel { Spacing = 6, Name = "JetBrainsAiForm" };
        var licence = new TextBox { PlaceholderText = "licence ID", Name = "JetBrainsLicence" };
        var command = new TextBox { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Name = "JetBrainsCommand", IsVisible = false };
        licence.TextChanged += (_, _) =>
        {
            var id = licence.Text?.Trim() ?? "";
            command.IsVisible = JetBrainsAi.IsLicenceId(id);
            command.Text = command.IsVisible ? JetBrainsAi.TokenCommand(id) : "";
        };
        var token = new TextBox { PasswordChar = '•', PlaceholderText = "token", Name = "JetBrainsToken" };
        AutomationProperties.SetName(token, "JetBrains AI token");
        void Save()
        {
            var secret = (token.Text ?? "").Trim();
            token.Text = "";
            if (secret.Length == 0) return;
            if (!JetBrainsAi.IsToken(secret))
            {
                context.Notify(PluginNotificationKind.Error, "That is not a token", "Paste the token the command prints, three parts separated by dots.");
                return;
            }
            adding = null;
            _ = LoadAsync(() => context.RequestAsync(ProvidersContract.SetKey, new SetProviderKeyParams(agent, JetBrainsAi.Id, secret) { Name = "JetBrains AI" }).AsTask());
        }
        token.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Save(); e.Handled = true; } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(Ui.Button("Save", Save));
        buttons.Children.Add(Ui.Button("Cancel", () => { adding = null; Render(report); }));
        form.Children.Add(JetBrainsAi.Step("1. Your JetBrains AI licence ID, from account.jetbrains.com/licenses."));
        form.Children.Add(licence);
        form.Children.Add(JetBrainsAi.Step("2. Run this in a terminal on your computer (it needs Central, curl and jq) and paste the token it prints. Nothing is kept beyond an hour."));
        form.Children.Add(command);
        form.Children.Add(JetBrainsAi.Step("3. Paste the token."));
        form.Children.Add(token);
        form.Children.Add(buttons);
        return form;
    }

    private static string SourceTag(ProviderSource source) => source switch
    {
        ProviderSource.Key => "  (key stored)",
        ProviderSource.SignIn => "  (signed in)",
        ProviderSource.Environment => "  (from the environment)",
        _ => ""
    };

    private Control KeyForm(ProvidersReport report, string id, string name, string? variable, CustomFields? fields)
    {
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key", Name = "ProviderKey", MinWidth = 280 };
        AutomationProperties.SetName(key, "API key for " + name);
        // A grid, not a horizontal stack, so a long pasted key scrolls inside the box instead of widening it.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        void Save()
        {
            var secret = key.Text ?? "";
            key.Text = "";
            if (secret.Trim().Length == 0) return;
            adding = null;
            custom = false;
            var set = new SetProviderKeyParams(agent, fields?.Id() ?? id, secret) { Name = fields?.Name(), EnvVar = fields?.EnvVar() is { Length: > 0 } typed ? typed : null };
            _ = LoadAsync(() => context.RequestAsync(ProvidersContract.SetKey, set).AsTask());
        }
        key.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Save(); e.Handled = true; } };
        var save = Ui.Button("Save", Save);
        var cancel = Ui.Button("Cancel", () => { adding = null; custom = false; Render(report); });
        Grid.SetColumn(save, 1);
        Grid.SetColumn(cancel, 2);
        row.Children.Add(key);
        row.Children.Add(save);
        row.Children.Add(cancel);
        if (variable is not null && fields is null)
        {
            var hint = new StackPanel { Spacing = 4 };
            hint.Children.Add(row);
            hint.Children.Add(Ui.Text($"Stored as {variable}.", Ui.Muted, 12));
            return hint;
        }
        return row;
    }

    private sealed record CustomFields(Func<string> Id, Func<string> Name, Func<string> EnvVar);

    private Control CustomForm(ProvidersReport report)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Name = "ProviderCustom" };
        panel.Children.Add(Ui.Button(custom ? "Hide custom provider" : "Add a provider that is not listed", () => { custom = !custom; adding = null; Render(report); }));
        if (!custom) return panel;
        var id = new TextBox { PlaceholderText = "provider id, such as my-gateway", Name = "CustomId" };
        var name = new TextBox { PlaceholderText = "name (optional)", Name = "CustomName" };
        var variable = new TextBox { PlaceholderText = agent == ProviderAgent.Omp ? "environment variable that holds the key, such as MY_GATEWAY_API_KEY" : "environment variable (optional)", Name = "CustomVariable" };
        foreach (var box in new[] { id, name, variable }) panel.Children.Add(box);
        panel.Children.Add(KeyForm(report, "", "a custom provider", null, new CustomFields(() => id.Text?.Trim() ?? "", () => name.Text?.Trim() ?? "", () => variable.Text?.Trim() ?? "")));
        return panel;
    }
}