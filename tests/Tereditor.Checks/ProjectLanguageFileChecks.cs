using System.Net;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>The project's .sharprail/language.toml: validation, precedence, watching, atomic write, local/remote parity.</summary>
internal static class ProjectLanguageFileChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        foreach (var remote in new[] { false, true }) await Exercise(Path.Combine(fixture, "language-file", remote ? "remote" : "local"), remote);
        Console.WriteLine("PASS project language file checks");
    }

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        for (var i = 0; i < 100; i++) { if (await condition()) return; await Task.Delay(100); }
        throw new InvalidOperationException(message);
    }

    private static async Task Exercise(string root, bool remote)
    {
        var project = Path.Combine(root, "project");
        var child = Path.Combine(root, "child");
        foreach (var folder in new[] { project, child }) Directory.CreateDirectory(folder);
        var file = Path.Combine(project, ".sharprail", "language.toml");
        var storage = Path.Combine(root, "state");
        await using var server = remote ? RemoteServer.Create(project, IPAddress.Loopback, 0, "language-file", storage, plugins: seams => seams with { Builtins = SharpRail.Host.Core.Plugins.BuiltinPlugins.LanguageServerEntries }) : null;
        if (server is not null) await server.StartAsync();
        var state = server is null ? new HostStateStore(storage) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
        using var remoteHost = server is null ? null : new RemoteProjectAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "language-file");
        IProjectServices host = remoteHost ?? (IProjectServices)new LocalProjectAdapter(new ProjectServices(project, state));
        await host.OpenProjectAsync(project);
        state.ChangeWorkspaces(items => items.Append(new("project", project, WorkspaceKinds.Default, project, "", ""))
            .Append(new("child", project, WorkspaceKinds.External, child, "", "")));
        async Task<JsonObject> Config(string workspace, string id) => JsonNode.Parse((await host.LanguageAsync(new(workspace, Operation: "configuration", Server: id))).Configuration)!.AsObject();
        async Task<JsonObject> Status(string id = "python") => JsonNode.Parse((await host.LanguageAsync(new(project, Operation: "projectLanguageFile", Server: id))).Configuration)!.AsObject();

        // Nothing is written for a project the user did not touch; host-state configuration keeps working.
        Require(!Directory.Exists(Path.Combine(project, ".sharprail")), "Opening a project created .sharprail.");
        Require(!(await Status())["exists"]!.GetValue<bool>(), "A missing file was reported as existing.");
        await host.LanguageAsync(new(project, Operation: "configure", Server: "typescript", Configuration: """{"autoStart":true}"""));
        Require((await Config(project, "typescript"))["autoStart"]!.GetValue<bool>(), "Legacy host-state configuration stopped working.");
        Require(!Directory.Exists(Path.Combine(project, ".sharprail")), "A host-state save wrote into the project.");

        // Parse and validate: unknown keys tolerated, bad entries ignored with diagnostics, host-specific keys refused.
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, """
            version = 1
            note = "keep me"

            [servers.python]
            autoStart = true
            future = 7
            installationPath = "/x"
            [servers.python.settings.python.analysis]
            typeCheckingMode = "strict"

            [servers.go.settings.gopls]
            staticcheck = "yes"

            [servers.nonexistent]

            [servers.typescript.initializationOptions]
            maxTsServerMemory = 2048
            """);
        await Until(async () => (await Status())["servers"]!.AsArray().Count == 2, "The file was not picked up.");
        var status = await Status();
        var diagnostics = string.Join("\n", status["diagnostics"]!.AsArray().Select(item => item!.GetValue<string>()));
        Require(diagnostics.Contains("servers.go") && diagnostics.Contains("servers.nonexistent") && diagnostics.Contains("installationPath"), "Diagnostics missed bad entries: " + diagnostics);
        Require(status["origin"]!.GetValue<string>() == "file", "Origin should be the file.");
        var python = await Config(child, "python");
        Require(python["settings"]!["python"]!["analysis"]!["typeCheckingMode"]!.GetValue<string>() == "strict", "Other workspaces did not see the project file.");
        Require(python["installationPath"] is null, "A host-specific key leaked from the file.");

        // Precedence: a local key wins for that key only; other keys still follow the file.
        await host.LanguageAsync(new(child, Operation: "configure", Server: "python", Configuration: """{"autoStart":false,"settings":{"python":{"analysis":{"typeCheckingMode":"basic"}}}}"""));
        python = await Config(child, "python");
        Require(python["settings"]!["python"]!["analysis"]!["typeCheckingMode"]!.GetValue<string>() == "basic", "Local override did not win.");
        Require((await Config(project, "python"))["settings"]!["python"]!["analysis"]!["typeCheckingMode"]!.GetValue<string>() == "strict", "Local override leaked to another workspace.");
        Require((await host.LanguageAsync(new(child, Operation: "projectLanguageFile", Server: "python"))).Configuration.Contains("\"origin\":\"local\""), "Origin should be local.");
        var untouched = await File.ReadAllTextAsync(file);
        Require(untouched.Contains("keep me"), "A host-state save changed the file.");

        // A project file can never choose an executable or turn PATH use on, whatever the key is called.
        await File.WriteAllTextAsync(file, """
            version = 1
            [servers.python]
            command = "/tmp/planted"
            executable = "planted"
            allowPath = true
            managed = false
            autoStart = true
            arguments = ["--stdio"]
            """);
        await Until(async () => (await Status())["diagnostics"]!.AsArray().Count >= 4, "Executable keys in the file produced no diagnostics.");
        var executableDiagnostics = string.Join("\n", (await Status())["diagnostics"]!.AsArray().Select(item => item!.GetValue<string>()));
        Require(executableDiagnostics.Contains("servers.python.command") && executableDiagnostics.Contains("servers.python.allowPath") && executableDiagnostics.Contains("cannot do that"), "The executable keys were not diagnosed: " + executableDiagnostics);
        var planted = (await Config(project, "python")).ToJsonString();
        Require(!planted.Contains("planted") && !planted.Contains("allowPath") && planted.Contains("--stdio"), "An executable key reached the effective configuration: " + planted);
        var resolution = JsonNode.Parse((await host.LanguageAsync(new(project, Operation: "resolution", Server: "python"))).Configuration)!;
        Require(resolution["source"]!.GetValue<string>() == "managed" && !resolution["allowPath"]!.GetValue<bool>(), "A project file changed which executable would run: " + resolution);

        // Watching: an external edit applies without restart; a malformed file is a diagnostic, not a failure.
        await File.WriteAllTextAsync(file, "= not toml [");
        await Until(async () => (await Status())["diagnostics"]!.AsArray().Count == 1, "Malformed file produced no diagnostic.");
        Require((await host.LanguageAsync(new(project, Operation: "servers"))).Servers.Count > 0, "A malformed file broke the language service.");
        Require(File.ReadAllText(file) == "= not toml [", "A malformed file was rewritten by a read.");
        var refused = false;
        try { await host.LanguageAsync(new(project, Operation: "saveProjectLanguageFile", Server: "python", Configuration: "{}")); }
        catch (Exception) { refused = true; }
        Require(refused && File.ReadAllText(file) == "= not toml [", "A malformed file was overwritten.");
        await File.WriteAllTextAsync(file, """
            version = 1
            [servers.ruby.initializationOptions]
            formatter = "rubocop"
            """);
        await Until(async () => (await Config(project, "ruby"))["initializationOptions"]?["formatter"]?.GetValue<string>() == "rubocop", "A file edit was not applied.");

        // Explicit save: portable part in the file, unknown keys preserved, stable order, atomic, no other files.
        await File.WriteAllTextAsync(file, """
            # Team notes: keep this comment.
            version = 1
            [extra]
            a = 1 # trailing comment

            [servers.typescript]
            # keep the typescript note
            future = true
            autoStart = true

            [servers.zzz]
            keep = 1
            """);
        await Until(async () => (await Status("typescript"))["servers"]!.AsArray().Count == 1, "Edited file not read.");
        await host.LanguageAsync(new(project, Operation: "saveProjectLanguageFile", Server: "python",
            Configuration: """{"installationPath":"","settings":{"python":{"analysis":{"typeCheckingMode":"standard"}}},"autoStart":true}"""));
        await host.LanguageAsync(new(project, Operation: "saveProjectLanguageFile", Server: "typescript",
            Configuration: """{"autoStart":true,"initializationOptions":{"maxTsServerMemory":1024}}"""));
        var raw = await File.ReadAllTextAsync(file);
        Require(raw.Contains("# Team notes: keep this comment.") && raw.Contains("# trailing comment") && raw.Contains("# keep the typescript note"), "Comments were lost:\n" + raw);
        Require(raw.IndexOf("[servers.typescript]") < raw.IndexOf("[servers.zzz]") && raw.IndexOf("[servers.zzz]") < raw.IndexOf("[servers.python]"), "Existing layout was reordered:\n" + raw);
        var written = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(raw)!;
        var servers = (Tomlyn.Model.TomlTable)written["servers"];
        Require((bool)((Tomlyn.Model.TomlTable)servers["typescript"])["future"] && (long)((Tomlyn.Model.TomlTable)servers["zzz"])["keep"] == 1 && (long)((Tomlyn.Model.TomlTable)written["extra"])["a"] == 1, "Unknown keys were lost.");
        var typescript = (Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)servers["typescript"])["initializationOptions"];
        Require((long)typescript["maxTsServerMemory"] == 1024, "Existing entry was not updated.");
        var analysis = (Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)servers["python"])["settings"])["python"])["analysis"];
        Require((string)analysis["typeCheckingMode"] == "standard", "Portable options were not written.");
        Require(!((Tomlyn.Model.TomlTable)servers["python"]).ContainsKey("installationPath"), "A host-specific option reached the file.");
        Require(Directory.GetFileSystemEntries(Path.GetDirectoryName(file)!).Single() == file, "Saving wrote files other than language.toml.");
        Require((await Config(child, "python"))["settings"]!["python"]!["analysis"]!["typeCheckingMode"]!.GetValue<string>() == "standard", "A shared save did not replace workspace overrides.");

        // Project autostart: file choice applies until a local list exists, whose first change starts from the file's.
        var selected = (await host.LanguageAsync(new(child, Operation: "projectAutostart"))).Configuration;
        Require(selected == """["python","typescript"]""", "File autostart was not offered: " + selected);
        await host.LanguageAsync(new(child, Operation: "setProjectAutostart", Server: "python", Configuration: "false"));
        Require((await host.LanguageAsync(new(project, Operation: "projectAutostart"))).Configuration == """["typescript"]""", "Local autostart did not start from the file's choices.");
        if (server is not null)
        {
            using var remoteState = new RemoteStateAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "language-file");
            Require((await remoteState.GetStateAsync()).ProjectLanguageFiles.ContainsKey(project), "The file status was lost from remote snapshots.");
        }
        Require(!Directory.Exists(Path.Combine(project, ".sharprail", ".gitignore")) && !File.Exists(Path.Combine(project, ".sharprail", ".gitignore")), "A .gitignore appeared in .sharprail.");
        await state.StopLanguagesAsync();
    }
}