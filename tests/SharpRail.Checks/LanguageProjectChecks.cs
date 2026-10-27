using System.Net;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.VisualTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Remote;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

internal static class LanguageProjectChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task Transport(IProjectServices host, string root)
    {
        foreach (var file in new[] { "library/Library.csproj", "consumer/Consumer.csproj", "native/CMakeLists.txt", "rust/Cargo.toml", "go/go.mod", "jvm/build.gradle.kts", "web/package.json", "scripts/pyproject.toml", "node_modules/hidden/Cargo.toml", ".git/hidden/Cargo.toml" })
        {
            var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "");
        }
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), Path.Combine(root, "rust"));
        var result = await host.LanguageAsync(new(root, Operation: "projects", Query: "refresh"));
        Require(result.State == "ready" && result.Freshness == "partial" && result.Projects.All(project => project.State == "detected"), "Detected roots claim confirmed import or failed transport.");
        foreach (var server in new[] { "csharp", "clangd", "rust", "go", "java", "kotlin", "typescript", "python" })
            Require(result.Projects.Any(project => project.Server == server), "Missing detected project for " + server);
        Require(result.Projects.All(project => !project.Manifest.StartsWith("node_modules/", StringComparison.Ordinal) && !project.Manifest.StartsWith(".git/", StringComparison.Ordinal) && !project.Manifest.StartsWith("linked/", StringComparison.Ordinal)), "Excluded or symlink directories leaked into project roots.");
        var page = await host.LanguageAsync(new(root, Operation: "projects", Offset: 1, Limit: 2));
        Require(page.Total == result.Total && page.Truncated && page.Projects.SequenceEqual(result.Projects.Skip(1).Take(2)), "Project-root pagination changed order or lost total count over transport.");
        var formatted = JsonNode.Parse(LanguageMcpResponse.Format(page, "projects", 1, 2, false))!;
        Require(formatted["projects"]!.AsArray().Count == 2, "Agent project-root results were paginated twice.");
        var filtered = await host.LanguageAsync(new(root, Operation: "projects", Server: "rust"));
        Require(filtered.Projects.Count == 1 && filtered.Projects[0].Root == "rust", "Provider filter lost the nested root.");
        var empty = Path.Combine(root, "empty-roots"); Directory.CreateDirectory(empty);
        Require((await host.LanguageAsync(new(empty, Operation: "projects"))).Projects.Count == 0, "Project metadata leaked between workspaces.");
        var inventory = await host.LanguageAsync(new(root, Operation: "servers"));
        Require(inventory.Servers.All(server => server.State == "idle"), "Project discovery started or installed a server.");
        var metadataRoot = Path.Combine(root, ".tools", "directory-metadata");
        Directory.CreateDirectory(Path.Combine(metadataRoot, "jvm/src/main/java"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = await host.LanguageAsync(new(metadataRoot, Operation: "retry", Server: "java"), deadline.Token);
        while (started.State is "installing" or "initializing")
        {
            await Task.Delay(20, deadline.Token);
            started = await host.LanguageAsync(new(metadataRoot, Operation: "status", Server: "java"), deadline.Token);
        }
        Require(started.State == "ready", "Fixture JDT did not start.");
        var imported = await host.LanguageAsync(new(metadataRoot, Operation: "projects", Query: "refresh"), deadline.Token);
        Require(imported.Projects.Count(project => project.State == "reported") == 1 &&
            imported.Projects.Any(project => project.Root == "jvm/src/main/java" && project.Kind == "Source root" && project.State == "reported"),
            "JDT source roots were lost, duplicated or allowed outside the workspace.");
        await host.LanguageAsync(new(metadataRoot, Operation: "stop", Server: "java"), deadline.Token);
        Require((await host.LanguageAsync(new(metadataRoot, Operation: "projects"), deadline.Token)).Projects.All(project => project.State == "detected"),
            "Stopped providers retained stale source-root marks.");
        Console.WriteLine("PASS project-root transport: multiple providers, nested roots, exclusions, paging, provenance, workspace isolation and no server startup");
    }

    internal static async Task Routing(string root)
    {
        var workspace = Path.Combine(root, "root-routing"); Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "pom.xml"), "");
        var selected = false;
        await using var service = new LanguageService(Path.Combine(root, "root-routing-state"), () => true,
            workspaceConfiguration: (_, id) => id == "intellij" && selected ? """{"useForJavaAndKotlin":true}""" : "{}");
        var defaults = await service.ExecuteAsync(new(workspace, Operation: "projects"));
        Require(defaults.Projects.Select(project => project.Server).Order().SequenceEqual(new[] { "java", "kotlin", "scala" }), "Default roots silently selected IntelliJ.");
        selected = true;
        var intellij = await service.ExecuteAsync(new(workspace, Operation: "projects"));
        Require(intellij.Projects.Any(project => project.Server == "intellij") && intellij.Projects.All(project => project.Server is not ("java" or "kotlin")), "IntelliJ roots compete with existing JVM providers.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.ExecuteAsync(new(workspace, Operation: "projects", Query: "refresh"), cancelled.Token); throw new InvalidOperationException("Cancelled discovery continued."); }
        catch (OperationCanceledException) { }
    }

    internal static void Ui(string fixture)
    {
        var previous = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE");
        Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        try
        {
            foreach (var remote in new[] { false, true })
            {
                var root = Path.Combine(fixture, "project-folders-" + (remote ? "remote" : "local"));
                foreach (var file in new[] { "library/Library.csproj", "rust/Cargo.toml", "jvm/build.gradle.kts", "plain/notes.txt" })
                {
                    var path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "");
                }
                var server = remote ? RemoteServer.Create(root, IPAddress.Loopback, 0, "project-folders", root + "-state") : null;
                try
                {
                    if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
                    using var app = server is null ? new E2eWorkspace(root, openFiles: false) : new E2eWorkspace(
                        new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "project-folders", root, root + "-profile", root);
                    E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
                    app.ShowTool("files");
                    var tree = app.Find<DrawnTree>("FilesTree");
                    E2eWorkspace.Until(() => tree.Rows.SingleOrDefault(entry => entry.Id == "library")?.Tip?.Contains("MSBuild project", StringComparison.Ordinal) == true);
                    foreach (var folder in new[] { "library", "rust", "jvm" })
                    {
                        var row = (DrawnTreeLabel)app.FileRow(folder);
                        Require(row.GetVisualDescendants().OfType<ProjectFolderIcon>().Any() && !row.Entry.Tip!.Contains("Detected from", StringComparison.Ordinal), "Project folder lacks its compact marker or retains redundant detection text.");
                    }
                    Require(!((DrawnTreeLabel)app.FileRow("plain")).GetVisualDescendants().OfType<ProjectFolderIcon>().Any(), "An ordinary folder received a module badge.");
                    var library = (DrawnTreeLabel)app.FileRow("library"); library.OpenMenu();
                    var menu = library.ContextMenu!;
                    Require(menu.Items.OfType<MenuItem>().Count() > 8 && menu.Items.OfType<MenuItem>().All(item => item.Icon is null), "A Files tree menu item carries an icon, so its labels misalign.");
                    E2eWorkspace.Until(() => menu.Items.OfType<MenuItem>().First().GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Any(part => part.Name == "PART_HeaderPresenter" && part.Bounds.Width > 0));
                    foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.IsVisible))
                    {
                        var label = item.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(part => part.Name == "PART_HeaderPresenter");
                        var left = Avalonia.VisualExtensions.TranslatePoint(label, default, item)!.Value.X;
                        Require(left is >= 11.5 and <= 13.5, $"The Files tree menu leaves a gutter before '{item.Header}': its label starts at x={left} instead of the item padding (12).");
                    }
                    var configure = menu.Items.OfType<MenuItem>().Single(item => item.Name == "FileNodeLanguage_csharp");
                    app.Click(configure);
                    E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<LanguageSettingsWindow>().Any());
                    var settings = app.Window.OwnedWindows.OfType<LanguageSettingsWindow>().Single();
                    Require(settings.Title == "C# (Roslyn)" && settings.FindControl<TextBlock>("SettingsWorkspace")!.Text == root, "Folder settings target the wrong provider/workspace or misrepresent their scope.");
                    settings.Close();
                    var rust = (DrawnTreeLabel)app.FileRow("rust"); rust.OpenMenu();
                    var open = rust.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "FileNodeBuildDefinition");
                    app.Click(open);
                    E2eWorkspace.Until(() => app.Window.Layout.Tabs(app.Center).Any(tab => tab.Path == "rust/Cargo.toml"));
                    Require(File.ReadAllText(Path.Combine(root, "rust/Cargo.toml")) == "", "Opening a build definition changed it.");
                    if (!remote) { app.ShowTool("files"); LanguageChecks.CaptureProjectFolders(app.Window); }
                    var other = root + "-other"; Directory.CreateDirectory(Path.Combine(other, "library"));
                    File.WriteAllText(Path.Combine(other, "library", "notes.txt"), "");
                    var switching = app.Window.OpenProjectAsync(other);
                    E2eWorkspace.Until(() => switching.IsCompleted); switching.GetAwaiter().GetResult();
                    app.ShowTool("files");
                    var otherRow = (DrawnTreeLabel)app.FileRow("library");
                    Require(otherRow.Entry.Tip is null && !otherRow.GetVisualDescendants().OfType<ProjectFolderIcon>().Any(), "Folder markers leaked across workspace switches.");
                    Console.WriteLine("PASS project folders " + (remote ? "remote" : "local") + ": vector badges, provenance, provider configuration and build-file navigation");
                }
                finally { if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult(); }
            }
            var reportedRoot = Path.Combine(fixture, "reported-source-folder");
            Directory.CreateDirectory(Path.Combine(reportedRoot, "sources"));
            File.WriteAllText(Path.Combine(reportedRoot, "sources", "Example.java"), "class Example {}\n");
            using var reportedApp = new E2eWorkspace(reportedRoot, openFiles: false, prepare: host =>
            {
                host.LanguageReply = request => request.Operation == "projects"
                    ? new LanguageResult("ready") { Projects = [new("sources", "", "java", "Source root", "reported", "Reported by Eclipse JDT.")], Total = 1 }
                    : null;
            });
            E2eWorkspace.Until(() => reportedApp.Window.WorkspaceMounted);
            reportedApp.ShowTool("files");
            E2eWorkspace.Until(() => reportedApp.Find<DrawnTree>("FilesTree").Rows.Any(row => row.Id == "sources" && row.Tip?.StartsWith("Source root", StringComparison.Ordinal) == true));
            var sourceRow = (DrawnTreeLabel)reportedApp.FileRow("sources");
            Require(sourceRow.GetVisualDescendants().OfType<ProjectFolderIcon>().Single().Bounds.Width == 14 && sourceRow.Entry.Tip!.StartsWith("Source root", StringComparison.Ordinal),
                "Provider-reported source folders lost their role or compact folder marker.");
            sourceRow.OpenMenu();
            Require(sourceRow.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Name == "FileNodeLanguage_java") &&
                !sourceRow.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Name == "FileNodeBuildDefinition"),
                "Source-root actions invented an empty build definition or lost their provider.");
            sourceRow.ContextMenu.Close();
            LanguageChecks.CaptureLsp(reportedApp.Window, "reported-source-root");
            Console.WriteLine("PASS reported source-root UI: readable provider icon, explicit provenance and source-folder context actions");
        }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", previous); }
    }
}