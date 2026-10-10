using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintStartFields : UserControl
{
    private readonly IPluginUIContext context;
    private readonly string project;
    private string? path;
    private AgentLauncher? shownLauncher;
    private AgentLauncher[] launchers = [];
    private bool updatingLaunchers;
    public BlueprintAgentId AgentId { get; private set; } = BlueprintAgentId.Claude;
    public event Action? Changed;

    public BlueprintStartFields(IPluginUIContext context, string project)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context; this.project = project;
        this.FindControl<ComboBox>("AgentChoice")!.SelectionChanged += (_, _) =>
        {
            if (updatingLaunchers) return;
            if (this.FindControl<ComboBox>("AgentChoice")!.SelectedIndex is var index && index >= 0 && index < launchers.Length)
            {
                AgentId = launchers[index].Id == "codex" ? BlueprintAgentId.Codex : BlueprintAgentId.Claude;
                UpdateLauncher(launchers[index]);
                Changed?.Invoke();
            }
        };
        this.FindControl<RadioButton>("Idea")!.IsCheckedChanged += (_, _) => Render();
        this.FindControl<RadioButton>("Product")!.IsCheckedChanged += (_, _) => Render();
        this.FindControl<RadioButton>("Spec")!.IsCheckedChanged += (_, _) => Render();
        this.FindControl<TextBox>("Brief")!.TextChanged += (_, _) => Changed?.Invoke();
        this.FindControl<Button>("Pick")!.Click += async (_, _) =>
        {
            try
            {
                if (await context.PickFileAsync(new() { WorkspaceId = project }) is not { Length: > 0 } selected) return;
                path = selected;
                this.FindControl<TextBlock>("Path")!.Text = path;
                Changed?.Invoke();
            }
            catch (Exception error) { ShowError(error.Message); }
        };
        this.FindControl<TextBlock>("Error")!.Foreground = Ui.Danger;
        this.FindControl<TextBlock>("ProductNote")!.Foreground = Ui.Muted;
    }

    public BlueprintSource? Source => this.FindControl<RadioButton>("Product")!.IsChecked == true ? new BlueprintProduct()
        : this.FindControl<RadioButton>("Spec")!.IsChecked == true ? path is null ? null : new BlueprintSpec(path)
        : this.FindControl<TextBox>("Brief")!.Text?.Trim() is { Length: > 0 } brief ? new BlueprintIdea(brief) : null;

    public bool Idea => this.FindControl<RadioButton>("Idea")!.IsChecked == true;

    public AgentLauncher? UpdateLaunchers(IEnumerable<AgentLauncher> registered)
    {
        var next = registered.Where(launcher => launcher.Id is "claude" or "codex").OrderBy(launcher => launcher.Id == "claude" ? 0 : 1).ToArray();
        var selected = next.FirstOrDefault(launcher => launcher.Id == BlueprintOpener.LauncherId(AgentId))
            ?? next.FirstOrDefault(launcher => launcher.Availability().Available) ?? next.FirstOrDefault();
        if (selected is not null) AgentId = selected.Id == "codex" ? BlueprintAgentId.Codex : BlueprintAgentId.Claude;
        var choice = this.FindControl<ComboBox>("AgentChoice")!;
        updatingLaunchers = true;
        if (!launchers.SequenceEqual(next))
        {
            launchers = next;
            choice.ItemsSource = next.Select(launcher => launcher.Label).ToArray();
        }
        choice.IsVisible = next.Length > 1;
        choice.SelectedIndex = selected is null ? -1 : Array.IndexOf(next, selected);
        updatingLaunchers = false;
        UpdateLauncher(selected);
        return selected;
    }

    private void UpdateLauncher(AgentLauncher? launcher)
    {
        var agent = this.FindControl<Button>("Agent")!;
        agent.IsVisible = launcher is not null;
        if (launcher is null) { shownLauncher = null; this.FindControl<ContentControl>("AgentIcon")!.Content = null; return; }
        var available = launcher.Availability();
        agent.IsEnabled = available.Available;
        agent.Opacity = available.Available ? 1 : .6;
        agent.Background = Ui.PrimarySubtle;
        agent.BorderBrush = Ui.PrimaryMuted;
        var label = this.FindControl<TextBlock>("AgentLabel")!;
        label.Text = launcher.Label;
        label.Foreground = Ui.Accent;
        Avalonia.Automation.AutomationProperties.SetName(agent, launcher.Label);
        ToolTip.SetTip(agent, available.Reason);
        ToolTip.SetShowOnDisabled(agent, true);
        if (!ReferenceEquals(shownLauncher, launcher))
            this.FindControl<ContentControl>("AgentIcon")!.Content = launcher.CreateIcon?.Invoke(14, Ui.Accent) ?? Ui.Icon("puzzle", Ui.Accent, 14);
        shownLauncher = launcher;
    }

    public void ShowError(string? message)
    {
        var error = this.FindControl<TextBlock>("Error")!;
        error.Text = message; error.IsVisible = message is { Length: > 0 };
    }

    private void Render()
    {
        this.FindControl<TextBox>("Brief")!.IsVisible = this.FindControl<RadioButton>("Idea")!.IsChecked == true;
        this.FindControl<TextBlock>("ProductNote")!.IsVisible = this.FindControl<RadioButton>("Product")!.IsChecked == true;
        this.FindControl<Grid>("SpecPick")!.IsVisible = this.FindControl<RadioButton>("Spec")!.IsChecked == true;
        Changed?.Invoke();
    }
}