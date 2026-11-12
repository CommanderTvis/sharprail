using Avalonia.Controls;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Panels;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private IReadOnlyList<LanguageProject> fileProjects = [];
    private string fileProjectsWorkspace = "";
    private CancellationTokenSource? fileProjectRefresh;

    private IReadOnlyList<LanguageProject> ProjectsAt(string folder) => fileProjectsWorkspace != workspaceRoot ? [] :
        fileProjects.Where(project => project.Root == folder.Replace('\\', '/')).ToArray();

    private Control ProjectFileIcon(ProjectFile file)
    {
        var folder = FileIcon(file.Path, file.IsDirectory, file.IsDirectory ? "folder" : "fileText", file.GitIgnored ? Ui.Hint : null);
        if (!file.IsDirectory || ProjectsAt(file.Path).Count == 0) return folder;
        return new ProjectFolderIcon(folder);
    }

    private object ProjectFileIconKey(ProjectFile file) => (FileTreeIconKey(file),
        string.Join('|', ProjectsAt(file.Path).Select(project => project.Server + ":" + project.Manifest)));

    private string? ProjectFileTip(ProjectFile file)
    {
        var projects = file.IsDirectory ? ProjectsAt(file.Path) : [];
        var text = string.Join("\n\n", projects.Select(project =>
            $"{project.Kind}\n{(project.Manifest.Length > 0 ? "Build file: " + project.Manifest : "Folder: " + (project.Root.Length > 0 ? project.Root : "workspace"))}\nLanguage service: {LanguageServerNameConverter.Display(project.Server)}"));
        return text.Length > 0 ? (file.GitIgnored ? "Ignored by git\n" : "") + text : file.GitIgnored ? "Ignored by git" : null;
    }

    private async Task RefreshFileProjectsAsync(long request, bool force)
    {
        fileProjectRefresh?.Cancel();
        var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        fileProjectRefresh = current;
        var token = current.Token;
        var workspace = workspaceRoot;
        try
        {
            var projects = new List<LanguageProject>();
            var offset = 0;
            while (true)
            {
                var result = await host.LanguageAsync(new(workspace, Operation: "projects", Offset: offset, Limit: 200, Query: force && offset == 0 ? "refresh" : ""), token);
                if (request != projectRequest || workspace != workspaceRoot || token.IsCancellationRequested) return;
                if (result.State != "ready") return;
                projects.AddRange(result.Projects);
                offset += result.Projects.Count;
                if (result.Projects.Count == 0 || offset >= result.Total) break;
            }
            if (fileProjectsWorkspace == workspace && fileProjects.SequenceEqual(projects)) return;
            fileProjects = projects; fileProjectsWorkspace = workspace;
            ReconcileFilesPanel();
            if (toolContent.GetValueOrDefault("files") is Grid panel)
            {
                var tree = panel.Children.OfType<DrawnTree>().Single();
                tree.RefreshRows(forceIcons: true);
                foreach (var row in tree.GetVisualDescendants().OfType<DrawnTreeLabel>()) ToolTip.SetTip(row, row.Entry.Tip);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine("Language project discovery unavailable: " + error.Message); }
        finally { if (ReferenceEquals(fileProjectRefresh, current)) fileProjectRefresh = null; current.Dispose(); }
    }

    private void ProjectFileActions(ContextMenu menu, ProjectFile file)
    {
        if (fileProjectsWorkspace != workspaceRoot) return;
        var folder = file.IsDirectory ? file.Path : HostPaths.Parent(file.Path);
        // A folder row offers the language entries only when it is itself a project root; a file row, when it sits inside one.
        var candidates = file.IsDirectory ? ProjectsAt(folder)
            : fileProjects.Where(project => project.Root.Length == 0 || folder == project.Root || folder.StartsWith(project.Root + "/", StringComparison.Ordinal)).ToArray();
        var applicable = candidates.GroupBy(project => project.Server).SelectMany(group => group.Where(project => project.Root.Length == group.Max(item => item.Root.Length))).ToArray();
        if (applicable.Length == 0) return;
        menu.Items.Add(new Separator());
        foreach (var provider in applicable.GroupBy(project => project.Server))
        {
            var project = provider.First();
            var configure = Ui.Menu("Configure " + project.Server + " language service (workspace)…", () => _ = ConfigureProjectLanguageAsync(project));
            configure.Name = "FileNodeLanguage_" + project.Server;
            menu.Items.Add(configure);
        }
        foreach (var project in candidates.Where(project => project.Manifest.Length > 0).GroupBy(project => project.Server)
            .SelectMany(group => group.Where(project => project.Root.Length == group.Max(item => item.Root.Length))).DistinctBy(project => project.Manifest))
        {
            if (Path.GetExtension(project.Manifest) is ".xcodeproj" or ".xcworkspace") continue;
            var open = Ui.Menu("Open build definition · " + project.Manifest, () => _ = BrowseDocumentAsync(project.Manifest, true));
            open.Name = "FileNodeBuildDefinition";
            menu.Items.Add(open);
        }
    }

    private async Task ConfigureProjectLanguageAsync(LanguageProject project)
    {
        var workspace = workspaceRoot;
        try
        {
            var result = await host.LanguageAsync(new(workspace, Operation: "configuration", Server: project.Server), lifetime.Token);
            if (workspaceRoot != workspace || result.State != "ready") return;
            var row = new LanguageServerState(project.Server, project.Manifest, "idle", project.Detail, "", result.Configuration, result.ActiveConfiguration, true);
            await new LanguageConfigurationView(host, workspace, row).ShowSettingsAsync(this);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Report(error); }
    }
}