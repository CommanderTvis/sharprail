using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Blueprint.UI;

public sealed class BlueprintUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var store = new BlueprintStore(context);
        var opener = new BlueprintOpener(context, store);
        context.OnWorkspaceRemoved(store.Evict);
        void Follow(PluginHostProjection host)
        {
            foreach (var workspace in host.Terminals.Keys) store.Follow(workspace);
        }
        Follow(context.Host());
        context.WatchHost(host => host.Terminals, (_, _) => Follow(context.Host()));
        context.WatchHost(host => host.Workspaces, (_, _) => Follow(context.Host()));
        context.WatchHost(host => host.WorkspaceRevisions, (_, _) =>
        {
            foreach (var workspace in context.Host().Terminals.Keys) store.RefreshGraph(workspace);
        });
        async void Open(string workspace)
        {
            try { await opener.Open(workspace); }
            catch (Exception error) { context.Notify(PluginNotificationKind.Error, "Could not open Blueprint author", error.Message); }
        }
        context.Companion(new("blueprint", "Blueprint", "pencil-ruler-2", host =>
        {
            store.Follow(host.WorkspaceId);
            return store.Get(host.WorkspaceId)?.Author is BlueprintTerminalAuthor author && author.TabKey == host.TabKey;
        }, host => new BlueprintPane(context, store, host.WorkspaceId)));
        context.FileViewer(new(props =>
        {
            var redirect = new Border();
            redirect.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                foreach (var editor in context.Editors.List(props.WorkspaceId).Where(editor => editor.Kind == EditorKind.File && editor.Path == BlueprintContract.File))
                    context.Editors.Close(editor.Id);
                Open(props.WorkspaceId);
            });
            return redirect;
        })
        { Matches = path => path == BlueprintContract.File, Open = (workspace, _) => { Open(workspace); return true; } });

        async void Start(Control trigger, string project)
        {
            if (TopLevel.GetTopLevel(trigger) is not Window owner) return;
            var dialog = DialogWindow.Create("Draft a blueprint", 560);
            dialog.Tag = "BlueprintStart";
            dialog.FindControl<TextBlock>("DialogExplanation")!.Text = "Start from an idea, or take over something that already exists. Either way you get a workspace with the agent on the left, writing a spec you can change on the right.";
            var fields = new BlueprintStartFields(context, project);
            dialog.FindControl<StackPanel>("DialogFields")!.Children.Add(fields);
            var actions = dialog.FindControl<StackPanel>("DialogActions")!;
            actions.Children.Add(Ui.Button("Cancel", () => dialog.Close()));
            var label = Ui.Text("Draft it");
            var start = new Button { Name = "BlueprintStart", Content = label, Padding = new Thickness(10, 5), Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
            var busy = false;
            void Available()
            {
                var launcher = context.Launchers().FirstOrDefault(candidate => candidate.Id == "claude");
                fields.UpdateLauncher(launcher);
                var availability = launcher?.Availability();
                start.IsEnabled = !busy && fields.Source is not null && availability?.Available == true;
                label.Text = busy ? "Starting…" : fields.Idea ? "Draft it" : "Take it over";
                ToolTip.SetTip(start, availability?.Available == true ? null : availability?.Reason ?? "Enable the Claude Code plugin to author a blueprint in a terminal.");
                ToolTip.SetShowOnDisabled(start, true);
            }
            fields.Changed += Available;
            using var launcherWatch = context.OnLaunchersChanged(_ => Available());
            Available();
            start.Click += async (_, _) =>
            {
                if (busy) return;
                busy = true; Available(); fields.ShowError(null);
                try
                {
                    var workspace = await context.EnterDefaultWorkspaceAsync(project);
                    if (workspace is null) return;
                    var existing = (await context.RequestAsync(BlueprintContract.Get, new(workspace.Id))).State;
                    if (existing is not null) { store.Set(workspace.Id, existing); await opener.Open(workspace.Id); }
                    else await opener.Start(workspace.Id, fields.Source ?? throw new InvalidOperationException("Choose what this blueprint starts from."));
                    dialog.Close();
                }
                catch (Exception error) { context.Notify(PluginNotificationKind.Error, "Could not start the blueprint", error.Message); }
                finally { busy = false; Available(); }
            };
            actions.Children.Add(start);
            await dialog.ShowDialog(owner);
        }
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("draft-blueprint", (workspace, _) =>
        {
            var button = Ui.IconButton("pencilRuler", "Draft a blueprint", () => { });
            button.Name = "WorkspaceDraftBlueprint";
            button.Click += (_, _) =>
            {
                var project = context.Host().Workspaces.FirstOrDefault(pair => pair.Value.Any(known => known.Id == workspace)).Key;
                if (project is not null) Start(button, project);
            };
            return button;
        }));
        context.WorkspaceAction(new ProjectScopedActionRegistration("draft-blueprint-project", project =>
            new BlueprintProjectAction(button => Start(button, project))));
        return store.Dispose;
    }
}