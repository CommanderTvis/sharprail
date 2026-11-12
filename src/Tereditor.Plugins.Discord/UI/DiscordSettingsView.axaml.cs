using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Discord.UI;

/// <summary>
/// Settings › Discord: the application id, the file-name switch and the blocked projects, all in the plugin's
/// settings namespace, plus the host's status line. The plugin's own on/off is Settings › Plugins.
/// </summary>
internal sealed partial class DiscordSettingsView : UserControl
{
    private readonly IPluginUIContext context;
    private readonly TextBlock status;
    private readonly TextBox applicationId;
    private readonly ToggleSwitch shareFileName;
    private readonly StackPanel blocked;
    private readonly StackPanel blockedProjects;
    private readonly List<IDisposable> observers = [];
    private string projectsSignature = "\0";
    private bool applying;

    public DiscordSettingsView(IPluginUIContext context)
    {
        this.context = context;
        AvaloniaXamlLoader.Load(this);
        status = this.FindControl<TextBlock>("DiscordStatus")!;
        applicationId = this.FindControl<TextBox>("DiscordApplicationId")!;
        shareFileName = this.FindControl<ToggleSwitch>("DiscordShareFileName")!;
        blocked = this.FindControl<StackPanel>("DiscordBlocked")!;
        blockedProjects = this.FindControl<StackPanel>("DiscordBlockedProjects")!;
        applicationId.LostFocus += (_, _) => SaveApplicationId(applicationId.Text ?? "");
        applicationId.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter) return;
            shareFileName.Focus();
            args.Handled = true;
        };
        shareFileName.IsCheckedChanged += (_, _) =>
        {
            if (!applying) Update(Settings with { ShareFileName = shareFileName.IsChecked == true });
        };
        Apply(Settings);
        AttachedToVisualTree += (_, _) =>
        {
            if (observers.Count > 0) return;
            observers.Add(context.OnSettings<DiscordSettings>(Apply));
            observers.Add(context.WatchHost(ProjectsSignature, (_, _) => Apply(Settings)));
            observers.Add(context.Subscribe(DiscordContract.StatusChannel, next => status.Text = StatusLine(next), new DiscordStatusParams()));
        };
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (var observer in observers) observer.Dispose();
            observers.Clear();
        };
    }

    private DiscordSettings Settings => context.Settings<DiscordSettings>();

    private static string ProjectsSignature(PluginHostProjection host) =>
        string.Join("\n", host.Projects.Select(project => project.Id + "\t" + project.Name));

    internal static string StatusLine(DiscordStatus? status) => status switch
    {
        null => "Checking…",
        { State: DiscordConnectionState.Unconfigured } => status.Detail ?? "Add an application id to start publishing.",
        { State: DiscordConnectionState.Unavailable } => status.Detail ?? "Couldn't reach Discord.",
        { State: DiscordConnectionState.Connecting } => "Connecting to Discord…",
        { Published: null } => status.Detail ?? "Connected — nothing published right now.",
        { Published.Details: { } details } => $"On Discord: \"{details}\" — {status.Published.State}",
        _ => $"On Discord: {status.Published.State}"
    };

    private void Apply(DiscordSettings settings)
    {
        applying = true;
        try
        {
            if (!applicationId.IsFocused) applicationId.Text = settings.ApplicationId;
            shareFileName.IsChecked = settings.ShareFileName;
            var host = context.Host();
            var projects = host.Projects;
            var signature = ProjectsSignature(host) + "\n\n" + string.Join("\n", settings.BlockedProjectIds);
            if (signature == projectsSignature) return;
            projectsSignature = signature;
            blocked.IsVisible = projects.Count > 0;
            blockedProjects.Children.Clear();
            foreach (var project in projects)
            {
                var box = new CheckBox
                {
                    Name = "DiscordBlock",
                    Tag = project.Id,
                    Content = Ui.Text(project.Name, Ui.TextBrush),
                    IsChecked = settings.BlockedProjectIds.Contains(project.Id)
                };
                box.IsCheckedChanged += (_, _) => { if (!applying) ToggleBlocked(project.Id); };
                blockedProjects.Children.Add(box);
            }
        }
        finally { applying = false; }
    }

    private void SaveApplicationId(string next)
    {
        var trimmed = next.Trim();
        var current = Settings;
        if (trimmed == current.ApplicationId) return;
        // An id that is not a snowflake stays in the field and is never sent; empty is a choice, the silent one.
        if (trimmed.Length > 0 && !DiscordValues.IsApplicationId(trimmed)) return;
        Update(current with { ApplicationId = trimmed });
    }

    private void ToggleBlocked(string projectId)
    {
        var current = Settings;
        var ids = current.BlockedProjectIds.Contains(projectId)
            ? current.BlockedProjectIds.Where(id => id != projectId).ToArray()
            : [.. current.BlockedProjectIds, projectId];
        Update(current with { BlockedProjectIds = ids });
    }

    private async void Update(DiscordSettings settings)
    {
        try { await context.UpdateSettingsAsync(settings); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't change the Discord setting", error.Message);
            Apply(Settings);
        }
    }
}