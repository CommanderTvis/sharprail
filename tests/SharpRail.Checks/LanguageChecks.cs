using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class LanguageChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "language"); Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE");
        Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        try
        {
            await IntelliJChecks.Distribution(root);
            ProjectModelPolicies(root);
            LanguageOptionChecks.Run();
            await LanguageProjectChecks.Routing(root);
            await ProjectInitialization(root);
            var boundedLog = new LanguageLog();
            boundedLog.Add("stderr", new string('x', 40_000));
            var boundedSnapshot = boundedLog.Read("fixture");
            Require(boundedSnapshot.Truncated && boundedSnapshot.Detail.Length <= 32 * 1024, "Server log retention is unbounded.");
            var formattedLog = JsonNode.Parse(LanguageMcpResponse.Format(boundedSnapshot, "logs", 0, 200, true))!;
            Require(formattedLog["truncated"]!.GetValue<bool>() && formattedLog["logs"]!.ToString().Length <= 12_000, "Agent logs ignored the output limit.");
            Require(LanguageServers.Catalog.SelectMany(server => server.Extensions).Distinct().Count() >= 40, "Language coverage regressed.");
            var cache = new LanguageServers(Path.Combine(root, "cache-test"));
            var go = LanguageServers.Catalog.Single(server => server.Id == "go");
            var java = LanguageServers.Catalog.Single(server => server.Id == "java");
            var left = LanguageServers.Start("fixture", []); var right = LanguageServers.Start("fixture", []);
            cache.ConfigureCaches(left, go, root + "/a"); cache.ConfigureCaches(right, go, root + "/b");
            Require(left.Environment["GOPLSCACHE"] == right.Environment["GOPLSCACHE"], "gopls workspaces do not share its supported index cache.");
            cache.ConfigureCaches(left, java, root + "/a"); cache.ConfigureCaches(right, java, root + "/b");
            Require(left.ArgumentList.Last() != right.ArgumentList.Last(), "JDT workspaces share exclusive mutable data.");
            var launcher = Path.Combine(root, "npm", "node_modules", "typescript-language-server", "lib", "cli.mjs");
            Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
            await File.WriteAllTextAsync(launcher, "");
            Require(!LanguageServers.HasTypeScript(launcher, root), "A standalone TypeScript LSP must use managed provisioning.");
            var compiler = Path.Combine(root, "npm", "node_modules", "typescript", "lib");
            Directory.CreateDirectory(compiler);
            foreach (var file in new[] { "tsserver.js", "typescript.js", "../package.json" }) await File.WriteAllTextAsync(Path.Combine(compiler, file), "{}");
            Require(LanguageServers.HasTypeScript(launcher, root), "A bundled TypeScript installation was not found.");
            var link = Path.Combine(root, "typescript-language-server");
            File.CreateSymbolicLink(link, launcher);
            Require(LanguageServers.HasTypeScript(link, root), "A symlinked launcher lost its bundled TypeScript installation.");
            foreach (var remote in new[] { false, true }) await Exercise(Path.Combine(root, remote ? "remote" : "local"), remote);
            var large = new LanguageResult("ready")
            {
                Freshness = "partial",
                Coverage = "Explicit files",
                Files = Enumerable.Range(0, 400).Select(i => new LanguageFileResult($"file{i:D3}.ts", i == 0 ? "pending" : "checked", i == 0 ? "pending" : "current", "")).ToArray(),
                Diagnostics = Enumerable.Range(0, 400).Select(i => new LanguageItem($"file{i:D3}.ts", 0, 0, 0, 1, new string('x', 3_000))).ToArray()
            };
            var compact = LanguageMcpResponse.Format(large, "diagnostics", 0, 200, false);
            var formatted = JsonNode.Parse(compact)!;
            Require(compact.Length < 32_000 && formatted["freshness"]!.ToString() == "partial" && formatted["summary"]!["pending"]!.GetValue<int>() == 1 && formatted["diagnosticsPage"]!["total"]!.GetValue<int>() == 400 && formatted["truncated"]!.GetValue<bool>(), "Compact diagnostics hid partial coverage or exceeded the response budget.");
            Require(formatted["tokens"] is null && formatted["locations"] is null, "Diagnostics emitted irrelevant empty fields.");
            var styles = EditorFrame.SemanticStyles("😀 target\nnext", [new(0, 3, 6, "variable"), new(50, 0, 4, "type")]);
            Require(styles.Take(5).All(value => value == byte.MaxValue) && styles.Skip(5).Take(6).All(value => value == Array.IndexOf(TextMateHighlighter.Roles, "variable")), "Semantic UTF-16 to UTF-8 mapping corrupted an astral character.");
            var roleOf = new Dictionary<string, string>
            {
                ["class"] = "type",
                ["struct"] = "type",
                ["interface"] = "type",
                ["enum"] = "type",
                ["typeParameter"] = "type",
                ["delegate"] = "type",
                ["parameter"] = "variable",
                ["variable"] = "variable",
                ["field"] = "property",
                ["property"] = "property",
                ["method"] = "function"
            };
            foreach (var (kind, role) in roleOf)
                Require(EditorFrame.SemanticStyles("x", [new(0, 0, 1, kind)])[0] == Array.IndexOf(TextMateHighlighter.Roles, role), $"Semantic token '{kind}' did not map to the {role} role.");
            Console.WriteLine("PASS language local/remote parity, drafts, routing, diagnostics freshness, semantic tokens, navigation, cancellation, lifecycle and failure recovery");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", previous);
            Directory.Delete(root, true);
        }
    }

    private static async Task Exercise(string root, bool remote)
    {
        Directory.CreateDirectory(root);
        await GitRepository.RunAsync(root, default, "init");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".tools/\nignored.ts\n");
        var stateDirectory = Path.Combine(root, ".tools", "state");
        Directory.CreateDirectory(Path.Combine(stateDirectory, "language"));
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet"
            ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        var pushArguments = arguments.Select(argument => argument == "--fake-lsp" ? "--fake-lsp-push" : argument).ToArray();
        await File.WriteAllTextAsync(Path.Combine(stateDirectory, "language", "servers.json"), JsonSerializer.Serialize(new
        {
            typescript = new { command = executable, arguments },
            java = new { command = executable, arguments },
            python = new { command = Path.Combine(root, "missing-server"), arguments = Array.Empty<string>() },
            clangd = new { command = executable, arguments = pushArguments }
        }));
        await File.WriteAllTextAsync(Path.Combine(root, "code.ts"), "😀 target");
        await File.WriteAllTextAsync(Path.Combine(root, "bad.py"), "x");
        await File.WriteAllTextAsync(Path.Combine(root, "push.cpp"), "😀 broken");
        var unrelatedSession = Path.Combine(stateDirectory, "language", "workspaces", "0123456789abcdef01234567", "powershell");
        Directory.CreateDirectory(unrelatedSession);
        await File.WriteAllTextAsync(Path.Combine(unrelatedSession, "session.json"), "{}");
        await using var server = remote ? RemoteServer.Create(root, IPAddress.Loopback, 0, "language-test", stateDirectory,
            plugins: seams => seams with { Builtins = [] }, headlessLanguageMcp: true) : null;
        if (server is not null) await server.StartAsync();
        var state = server is null ? new HostStateStore(stateDirectory) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
        using var remoteHost = server is null ? null : new RemoteProjectAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "language-test");
        IProjectServices host = remoteHost ?? (IProjectServices)new LocalProjectAdapter(new ProjectServices(root, state));
        try
        {
            await host.OpenProjectAsync(root);
            await LanguageProjectChecks.Transport(host, root);
            foreach (var provider in LanguageServers.Catalog)
            {
                var schema = await host.LanguageAsync(new(root, Operation: "configurationSchema", Server: provider.Id));
                var value = JsonNode.Parse(schema.Configuration)!;
                Require(Uri.TryCreate(value["website"]?.ToString(), UriKind.Absolute, out var website) && website.Scheme == "https" &&
                    Uri.TryCreate(value["repository"]?.ToString(), UriKind.Absolute, out var repository) && repository.Scheme == "https",
                    "Provider website/repository links were lost over transport.");
                Require(value["version"]!.ToString() == provider.Version && value["fields"] is JsonArray && !string.IsNullOrWhiteSpace(value["source"]?.ToString()), "Configuration schema lost its provider version or managed provisioning source over transport.");
            }
            if (server is not null)
            {
                using var headlessHttp = new HttpClient { BaseAddress = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                const string request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
                using var refused = await headlessHttp.PostAsync("/mcp", new StringContent(request, Encoding.UTF8, "application/json"));
                Require(refused.StatusCode == HttpStatusCode.Unauthorized, "Headless MCP accepted an unauthenticated request.");
                headlessHttp.DefaultRequestHeaders.Authorization = new("Bearer", "language-test");
                using var accepted = await headlessHttp.PostAsync("/mcp", new StringContent(request, Encoding.UTF8, "application/json"));
                var headlessTools = JsonNode.Parse(await accepted.Content.ReadAsStringAsync())!["result"]!["tools"]!.AsArray();
                Require(accepted.IsSuccessStatusCode && headlessTools.Any(tool => tool!["name"]!.ToString() == "lsp_navigate") && headlessTools.All(tool => !tool!["name"]!.ToString().Contains("accept", StringComparison.OrdinalIgnoreCase)), "Headless MCP did not expose production language tools or exposed acceptance.");
            }
            await IntelliJTermsChecks.Run(host, root, remote);
            await IntelliJChecks.Provider(host, root);
            var catalog = await host.LanguageAsync(new(root, Operation: "servers"));
            Require(catalog.Servers.Count == LanguageServers.Catalog.Length && catalog.Servers.All(item => item.State == "idle") &&
                !File.Exists(Path.Combine(root, ".tools", "initialize.json")), "Listing servers started analysis or lost detected languages.");
            var implementations = await host.LanguageAsync(new(root, Operation: "catalog"));
            Require(implementations.Servers.All(item => item.Detail.Length > 0) && implementations.Servers.Single(item => item.Id == "java").Detail.Contains("Eclipse JDT", StringComparison.Ordinal),
                "Catalog must identify each implementation over both transports.");
            var emptyProblems = await host.LanguageAsync(new(root, Operation: "diagnostics"));
            Require(emptyProblems.Freshness == "unavailable" && !File.Exists(Path.Combine(root, ".tools", "initialize.json")),
                "Workspace Problems started a server.");
            var emptyWorkspace = Path.Combine(root, "empty"); Directory.CreateDirectory(emptyWorkspace);
            var manual = await Ready(host, new(emptyWorkspace, Operation: "retry", Server: "typescript"));
            Require(manual.State == "ready" && (await host.LanguageAsync(new(emptyWorkspace, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").Path == "",
                "A server cannot start without sources or reports an invented detection file.");
            await host.LanguageAsync(new(emptyWorkspace, Operation: "stop", Server: "typescript"));
            Require((await host.LanguageAsync(new(emptyWorkspace, Operation: "diagnostics"))).Freshness == "unavailable", "Problems restarted a stopped server.");
            await host.LanguageAsync(new(emptyWorkspace, Operation: "restart", Server: "typescript", Configuration: """{"settings":{"fixtureExit":true}}"""));
            using (var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                while (!(await host.LanguageAsync(new(emptyWorkspace, Operation: "logs", Server: "typescript"), exitDeadline.Token)).Detail.Contains("exit: Exited with code 7", StringComparison.Ordinal))
                    await Task.Delay(30, exitDeadline.Token);
            await host.LanguageAsync(new(emptyWorkspace, Operation: "stop", Server: "typescript"));
            var onDemand = Path.Combine(root, "on-demand"); Directory.CreateDirectory(onDemand);
            await File.WriteAllTextAsync(Path.Combine(onDemand, "code.ts"), "😀 target");
            var beforeStart = await host.LanguageAsync(new(onDemand, Operation: "diagnostics", Scope: "files", Paths: ["code.ts"]));
            Require(beforeStart.Files is [{ State: "checked" }] && beforeStart.Freshness == "current", "Explicit diagnostics did not start the idle server that owns the file.");
            await host.LanguageAsync(new(onDemand, Operation: "stop", Server: "typescript"));
            using var remoteState = server is null ? null : new RemoteStateAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "language-test");
            IHostStateService settingsHost = remoteState ?? (IHostStateService)new LocalStateAdapter(state);
            await settingsHost.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Disabled: true), JsonSerializerOptions.Web))]);
            Require((await host.LanguageAsync(new(root, "code.ts"))).State == "disabled" &&
                (await host.LanguageAsync(new(root, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State == "disabled", "Disabled server still starts or is advertised as idle.");
            await settingsHost.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            try
            {
                await settingsHost.ChangeAsync([HostStateChange.Setting("language-server:typescript", "{\"arguments\":[]}")]);
                throw new InvalidOperationException("Arguments without an executable were silently accepted.");
            }
            catch (ArgumentException) { }
            catch (SharpRail.Host.Protocol.RpcException) when (remote) { }
            var persisted = (await settingsHost.GetStateAsync()).Settings.LanguageServers;
            Require(persisted.Contains(executable, StringComparison.Ordinal) && new HostStateStore(stateDirectory).Current.Settings.LanguageServers == persisted, "LSP settings did not round-trip or persist.");
            var config = """{"autoStart":true,"initializationOptions":{"fixture":"${workspaceFolder}/lib"},"settings":{"fixture":{"enabled":false,"level":0,"mode":"compact","explicitNull":null},"python":{"analysis":{"extraPaths":["${workspaceFolder}/lib"]}}}}""";
            var savedConfig = await host.LanguageAsync(new(root, Operation: "configure", Server: "typescript", Configuration: config));
            Require(savedConfig.State == "ready" && !File.Exists(Path.Combine(root, ".tools", "initialize.json")), "Saving workspace configuration started a server.");
            var snapshot = await settingsHost.GetStateAsync();
            Require(snapshot.WorkspaceLanguages.ContainsKey(root) && new HostStateStore(stateDirectory).Current.WorkspaceLanguages[root] == snapshot.WorkspaceLanguages[root], "Workspace configuration failed persistence or transport parity.");
            var legacyStatePath = Path.Combine(stateDirectory, HostStateStore.FileName);
            var legacyState = JsonNode.Parse(await File.ReadAllTextAsync(legacyStatePath))!.AsObject();
            legacyState.Remove("WorkspaceLanguages");
            await File.WriteAllTextAsync(legacyStatePath, legacyState.ToJsonString());
            var reopenedState = new HostStateStore(stateDirectory);
            Require(reopenedState.Current.WorkspaceLanguages[root] == snapshot.WorkspaceLanguages[root],
                "An older host rewriting state.json erased persisted workspace LSP settings.");
            var otherHost = new LocalProjectAdapter(new ProjectServices(root, reopenedState));
            await otherHost.LanguageAsync(new(root, Operation: "configure", Server: "clangd", Configuration: """{"compilationDatabasePath":".tools/clangd"}"""));
            var observed = await host.LanguageAsync(new(root, Operation: "configuration", Server: "clangd"));
            Require(JsonNode.Parse(observed.Configuration)!["compilationDatabasePath"]!.GetValue<string>() == ".tools/clangd",
                "Another host's saved compilation database was invisible to the live host.");
            await GitRepository.RunAsync(root, default, "add", ".gitignore", "code.ts");
            await GitRepository.RunAsync(root, default, "commit", "-m", "language fixture");
            var childRecord = (await host.ApplyWorkspaceActionAsync(WorkspaceAction.Create(root, "Inherited LSP")))!;
            var child = childRecord.Path;
            using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                while ((await host.LanguageAsync(new(child, Operation: "servers"), startup.Token)).Servers.Single(item => item.Id == "typescript").State != "ready")
                    await Task.Delay(30, startup.Token);
            var childInit = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(child, ".tools", "initialize.json")))!;
            Require(childInit["initializationOptions"]!["fixture"]!.ToString() == child + "/lib", "Inherited startup did not rebase workspace-relative paths.");
            var inherited = await host.LanguageAsync(new(child, Operation: "configuration", Server: "typescript"));
            Require(inherited.Configuration == savedConfig.Configuration, "New workspace did not inherit Default configuration.");
            await host.LanguageAsync(new(child, Operation: "configure", Server: "typescript", Configuration: "{}"));
            Require((await host.LanguageAsync(new(root, Operation: "configuration", Server: "typescript"))).Configuration == savedConfig.Configuration, "Editing the child changed Default configuration.");
            await host.LanguageAsync(new(child, Operation: "stop", Server: "typescript"));
            await host.ApplyWorkspaceActionAsync(WorkspaceAction.Remove(childRecord.Id));
            Require(!state.Current.WorkspaceLanguages.ContainsKey(child), "Removing a workspace leaked its configuration.");
            var idleLogs = await host.LanguageAsync(new(root, Operation: "logs", Server: "typescript"));
            Require(idleLogs.Detail.Contains("No logs yet", StringComparison.Ordinal) && !File.Exists(Path.Combine(root, ".tools", "initialize.json")), "Viewing logs started a server.");
            var initial = await Ready(host, new(root, "code.ts"));
            using (var configurationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                while (!File.Exists(Path.Combine(root, ".tools", "configuration-response.json"))) await Task.Delay(30, configurationDeadline.Token);
            var configurationResponse = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".tools", "configuration-response.json")))!;
            Require(configurationResponse[4]!.GetValue<bool>() == false && configurationResponse[5]!.GetValue<int>() == 0 && configurationResponse[6]!.GetValue<string>() == "compact",
                "Configuration replies changed false, zero or string settings into objects.");
            var serverLogs = await host.LanguageAsync(new(root, Operation: "logs", Server: "typescript"));
            Require(serverLogs.Detail.Contains("fixture stderr", StringComparison.Ordinal) && serverLogs.Detail.Contains("fixture server message", StringComparison.Ordinal) &&
                serverLogs.Detail.Contains("Workspace: " + root, StringComparison.Ordinal) && serverLogs.Detail.Contains(executable, StringComparison.Ordinal),
                "Workspace logs omitted stderr, server messages or launch details over the host adapter.");
            var configuredInit = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".tools", "initialize.json")))!;
            Require(configuredInit["initializationOptions"]!["fixture"]!.ToString() == root + "/lib", "Initialization options did not expand workspace paths.");
            await host.LanguageAsync(new(root, Operation: "configure", Server: "typescript", Configuration: """{"initializationOptions":{"fixture":"changed"}}"""));
            var pendingConfig = await host.LanguageAsync(new(root, Operation: "configuration", Server: "typescript"));
            Require(pendingConfig.Configuration != pendingConfig.ActiveConfiguration && pendingConfig.ActiveConfiguration.Contains("workspaceFolder", StringComparison.Ordinal), "Saving settings changed the active snapshot without restart.");
            await host.LanguageAsync(new(root, Operation: "restart", Server: "typescript", Configuration: """{"autoStart":false,"initializationOptions":{"fixture":"changed"}}"""));
            Require(!JsonNode.Parse((await host.LanguageAsync(new(root, Operation: "configuration", Server: "typescript"))).Configuration)!["autoStart"]!.GetValue<bool>(), "Restart overwrote an explicit inheritance choice.");
            await Ready(host, new(root, "code.ts"));
            configuredInit = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".tools", "initialize.json")))!;
            Require(configuredInit["initializationOptions"]!["fixture"]!.ToString() == "changed", "Restart failed to apply saved workspace configuration.");
            await File.WriteAllTextAsync(Path.Combine(root, "unopened.ts"), "😀 broken");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.ts"), "😀 broken");
            var scan = await host.LanguageAsync(new(root, Operation: "diagnostics", Scope: "workspace"));
            Require(scan.Diagnostics.Any(item => item.Path == "unopened.ts") && scan.Files.Any(file => file.Path == "unopened.ts" && file.State == "checked") &&
                scan.Files.Any(file => file.Path == "bad.py" && file.State == "skipped") && scan.Files.All(file => file.Path != "ignored.ts") && scan.Freshness == "partial",
                "Project diagnostics missed unopened files, included ignored files, or hid skipped coverage.");
            var isolated = await host.LanguageAsync(new(root, Operation: "diagnostics", Paths: ["code.ts"]));
            Require(isolated.Diagnostics.Count == 0 && isolated.Files.Count == 1 && isolated.Freshness == "current", "File selection leaked another open file’s diagnostics.");
            await File.WriteAllTextAsync(Path.Combine(root, "unopened.ts"), "😀 target");
            var edited = await host.LanguageAsync(new(root, Operation: "diagnostics", Scope: "files", Paths: ["unopened.ts", "code.ts", "unopened.ts"]));
            Require(edited.Files.Count == 2 && edited.Files.All(file => file.State == "checked") && edited.Diagnostics.Count == 0 && edited.Freshness == "current",
                "Explicit edited-file scan did not refresh disk content or isolate its results.");
            var missing = await host.LanguageAsync(new(root, Operation: "diagnostics", Scope: "files", Paths: ["missing.ts", "notes.txt"]));
            Require(missing.Files.Any(file => file.State == "error") && missing.Files.Any(file => file.State == "skipped") && missing.Freshness == "partial", "Missing/unsupported files were reported clean.");
            try { await host.LanguageAsync(new(root, Operation: "diagnostics", Scope: "files", Paths: ["../escape.ts"])); throw new InvalidOperationException("Batch escaped its workspace."); }
            catch (UnauthorizedAccessException) { }
            catch (SharpRail.Host.Protocol.RpcException) when (remote) { }
            Require((await host.LanguageAsync(new(root, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State == "ready",
                "Server listing did not reflect initialization.");
            Require(initial.SemanticColoring && initial.Tokens is [{ Column: 3, EndColumn: 9, Kind: "variable", Modifiers: "static" }] && initial.Freshness == "current", "Initial semantic/diagnostic result missing.");
            var init = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".tools", "initialize.json")))!;
            Require(init["cwd"]!.ToString() == root && init["root"]!.ToString() == new Uri(root + "/").AbsoluteUri, "Server was not pointed at this workspace.");
            var draft = new LanguageRequest(root, "code.ts", Text: "😀 broken", DocumentId: "editor", Version: 2, Dirty: true);
            var inspected = await Ready(host, draft);
            Require(inspected.Diagnostics.Single().Text == "broken" && inspected.Version == 2, "Unsaved text was not analyzed.");
            var batchDraft = await host.LanguageAsync(new(root, Operation: "diagnostics", Paths: ["code.ts"]));
            Require(batchDraft.Diagnostics.Single().Text == "broken" && batchDraft.Files.Single().Detail.Contains("unsaved", StringComparison.Ordinal), "Batch lost dirty draft provenance.");
            var pid = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".tools", "initialize.json")))!["pid"]!.GetValue<int>();
            await settingsHost.ChangeAsync([HostStateChange.Setting("lsp-enabled", "false")]);
            Require(!(await settingsHost.GetStateAsync()).Settings.LspEnabled && !new HostStateStore(stateDirectory).Current.Settings.LspEnabled,
                "Master LSP setting did not persist across transports.");
            bool exited;
            try { using var process = System.Diagnostics.Process.GetProcessById(pid); exited = process.HasExited; }
            catch (ArgumentException) { exited = true; }
            Require(exited && (await host.LanguageAsync(new(root, "code.ts", "retry"))).State == "disabled" &&
                (await host.LanguageAsync(new(root, Operation: "diagnostics", Scope: "workspace"))).State == "disabled",
                "Disabling LSP failed to stop processes or block language requests.");
            Require((await host.LanguageAsync(new(root, Operation: "servers"))).Servers.Single(item => item.Id == "typescript") is { State: "disabled", Configuration.Length: > 0 }, "Disabling LSP hid saved workspace configuration.");
            Require((await host.LanguageAsync(new(root, Operation: "logs", Server: "typescript"))).Detail.Contains("fixture stderr", StringComparison.Ordinal), "Disabling LSP hid failure evidence.");
            Require((await host.LanguageAsync(new("", Operation: "catalog"))).Servers.Count == LanguageServers.Catalog.Length,
                "Disabling LSP hid its settings catalog.");
            await settingsHost.ChangeAsync([HostStateChange.Setting("lsp-enabled", "true")]);
            var agent = await Ready(host, new(root, "code.ts", "diagnostics"));
            Require(agent.Diagnostics.Single().Text == "broken" && agent.Detail.Contains("unsaved", StringComparison.Ordinal), "Agent lost the editor draft or its provenance.");
            await host.LanguageAsync(new(root, "code.ts", "stop"));
            await host.LanguageAsync(new(root, "code.ts", "retry"));
            Require((await Ready(host, new(root, "code.ts", "diagnostics"))).Diagnostics.Single().Text == "broken", "Restart lost a dirty editor's analysis state.");
            Require((await host.LanguageAsync(draft with { Version = 1 })).State == "stale", "Old client version accepted.");
            Require((await host.LanguageAsync(draft with { DocumentId = "other", Text = "different" })).State == "conflict", "Different drafts contaminated each other.");
            var nav = await Ready(host, draft with { Operation = "definition", Line = 0, Column = 3 });
            Require(nav.Locations is [{ Path: "code.ts", Column: 3 }] && nav.Locations[0].Preview.Contains("broken", StringComparison.Ordinal) && nav.Locations[0].Preview.Contains("Unsaved", StringComparison.Ordinal), "Definition lost its location or dirty source excerpt.");
            var outlineText = "class Suite {\n  run() {\n    return '😀';\n  }\n}\n";
            await File.WriteAllTextAsync(Path.Combine(root, "outline.ts"), outlineText);
            var outline = await Ready(host, new(root, "outline.ts", "symbols", Limit: 1));
            Require(outline.Total == 2 && outline.Truncated && outline.Locations.Single().Text == "Suite", "Outline pagination lost hierarchy or totals.");
            var next = await Ready(host, new(root, "outline.ts", "symbols", Offset: 1, Limit: 1));
            Require(!next.Truncated && next.Locations.Single().Text == "Suite/run", "Outline pagination lost qualified names.");
            var bySymbol = await Ready(host, new(root, "outline.ts", "definition", Query: "Suite/run", Line: 99, Column: 99));
            Require(bySymbol.Locations.Single() is { Line: 1, Column: 2 }, "Symbol navigation guessed a position instead of using the selection range.");
            Require((await Ready(host, new(root, "outline.ts", "definition", Query: "missing"))).State == "notFound", "Missing navigation symbol fell back to a guessed cursor.");
            // Names instead of positions: a simple or partly qualified outline name, and a symbol name without a path.
            Require((await Ready(host, new(root, "outline.ts", "definition", Query: "run"))).Locations.Single() is { Line: 1, Column: 2 }, "A simple name did not select its unique outline symbol.");
            Require((await Ready(host, new(root, "outline.ts", "definition", Query: "Suite.run"))).Locations.Single() is { Line: 1, Column: 2 }, "A dotted qualified name did not select its outline symbol.");
            var missingInFile = await Ready(host, new(root, "outline.ts", "references", Query: "missing"));
            Require(missingInFile.State == "notFound" && missingInFile.Detail.Contains("\"operation\":\"symbols\",\"path\":\"outline.ts\"", StringComparison.Ordinal), "A name missing from a file did not name the outline call: " + missingInFile.Detail);
            await File.WriteAllTextAsync(Path.Combine(root, "runner.ts"), "function run() {}");
            var byName = await Ready(host, new(root, Operation: "references", Query: "Suite"));
            Require(byName.State == "ready" && byName.Locations.Single().Path == "outline.ts" && byName.Detail.Contains("Resolved \"Suite\" to outline.ts line 0, column 6", StringComparison.Ordinal),
                "References by name did not run at the declaration: " + byName.State + " " + byName.Detail);
            var sameName = await Ready(host, new(root, Operation: "definition", Query: "run"));
            Require(sameName.State == "ambiguous" && sameName.Total == 2 && sameName.Locations.Select(item => item.Path).Order().SequenceEqual(["outline.ts", "runner.ts"]) && sameName.Detail.Contains("\"operation\":\"definition\",\"path\":", StringComparison.Ordinal),
                "A shared name did not list its candidates with a working call: " + sameName.Detail);
            var qualified = await Ready(host, new(root, Operation: "definition", Query: "Suite/run"));
            Require(qualified.State == "ready" && qualified.Locations.Single() is { Path: "outline.ts", Line: 1, Column: 2 }, "A qualified name did not choose among same-named symbols: " + qualified.Detail);
            var unknown = await Ready(host, new(root, Operation: "implementation", Query: "Nowhere"));
            Require(unknown.State == "notFound" && unknown.Detail.Contains("\"operation\":\"workspaceSymbols\"", StringComparison.Ordinal), "An unknown name did not name the search call: " + unknown.Detail);
            await File.WriteAllTextAsync(Path.Combine(root, "ambiguous.ts"), "function overload() {}");
            Require((await Ready(host, new(root, "ambiguous.ts", "definition", Query: "overload"))).State == "ambiguous", "Overloads selected an arbitrary definition.");
            var overloadPage = await Ready(host, new(root, "ambiguous.ts", "definition", Query: "overload", Offset: 1, Limit: 1));
            Require(overloadPage.State == "ambiguous" && overloadPage.Total == 2 && overloadPage.Locations.Count == 1 && !overloadPage.Truncated, "Ambiguous symbol paging lost candidates or totals.");
            await File.WriteAllTextAsync(Path.Combine(root, "selected.ts"), "const target = 1;");
            var selectedDraft = new LanguageRequest(root, "selected.ts", "definition", Text: "// inserted\nconst target = 1;", DocumentId: "symbol-editor", Version: 2, Query: "target", Dirty: true);
            Require((await Ready(host, selectedDraft)).Locations.Single().Line == 1, "Symbol selection did not resolve against the dirty buffer.");
            Require((await Ready(host, selectedDraft with { Text = "// inserted\n// again\nconst target = 1;", Version = 3 })).Locations.Single().Line == 2, "Symbol selection reused a stale position after an edit.");
            Require((await Ready(host, selectedDraft)).State == "stale", "Symbol selection accepted an old buffer version.");
            await host.LanguageAsync(selectedDraft with { Operation = "close" });
            var bodyResult = await Ready(host, new(root, "outline.ts", "symbolBody", Query: "Suite/run"));
            Require(bodyResult.Locations.Single().Body == "run() {\n    return '😀';\n  }", "Symbol body did not use its full UTF-16 range.");
            Require((await Ready(host, new(root, "outline.ts", "symbolBody", Line: 2, Column: 4))).Locations.Single().Text == "Suite/run", "Enclosing symbol selected the class instead of the method.");
            var largeDraft = new LanguageRequest(root, "outline.ts", "symbolBody", Text: outlineText.Replace("return '😀';", new string('x', 13_000)), DocumentId: "body-editor", Version: 1, Query: "Suite/run", Dirty: true);
            var boundedBody = await Ready(host, largeDraft);
            Require(boundedBody.Locations.Single().Body.Length == 12_000 && boundedBody.Locations.Single().Truncated && boundedBody.Locations.Single().Preview.Contains("Unsaved", StringComparison.Ordinal), "Large dirty symbol body was not bounded or lost provenance.");
            await host.LanguageAsync(largeDraft with { Operation = "close" });
            Require((await Ready(host, new(root, "outline.ts", "symbolBody", Query: "missing"))).State == "notFound", "Missing symbol was reported ready.");
            await File.WriteAllTextAsync(Path.Combine(root, "many.ts"), string.Join('\n', Enumerable.Range(0, 250).Select(index => "const value" + index + " = 1;")));
            var references = await Ready(host, new(root, "many.ts", "references", Offset: 200, Limit: 50));
            Require(references.Total == 250 && !references.Truncated && references.Locations.Count == 50 && references.Locations[0].Line == 200 && references.Locations[0].Preview.Contains("value200", StringComparison.Ordinal), "Reference paging lost source excerpts or omitted locations.");
            var fileReferences = await Ready(host, new(root, "many.ts", "references", Scope: "file", Offset: 200, Limit: 1));
            Require(fileReferences.Total == 250 && fileReferences.Locations.Single().Line == 200, "File scope changed paging totals.");
            var dependencyReferences = await Ready(host, new(root, "many.ts", "references", Scope: "dependencies", Limit: 1));
            Require(dependencyReferences.Total == 250, "Dependency scope failed transport validation.");
            var unsupportedModule = await Ready(host, new(root, "many.ts", "references", Scope: "module"));
            Require(unsupportedModule.State == "unsupported" && unsupportedModule.Locations.Count == 0, "A manifest folder was presented as semantic module ownership.");
            foreach (var operation in new[] { "hover", "typeDefinition", "implementation", "references", "symbols", "workspaceSymbols" })
                Require((await Ready(host, draft with { Operation = operation })).Locations.Count > 0, operation + " returned no locations.");
            var dependency = Path.GetFullPath(Path.Combine(root, "..", "library.ts"));
            await File.WriteAllTextAsync(dependency, "export type Library = string;");
            var library = await Ready(host, draft with { Operation = "typeDefinition" });
            Require(library.Locations[0].Path == dependency && (await host.ReadFileAsync(dependency)).ReadOnly, "Dependency navigation did not expose the exact read-only file.");
            try { await host.SaveFileAsync(new(root, dependency, "export type Library = string;", "edited")); throw new InvalidOperationException("Dependency write accepted."); }
            catch (UnauthorizedAccessException) { }
            catch (SharpRail.Host.Protocol.RpcException) when (remote) { }
            await host.LanguageAsync(draft with { Operation = "close" });
            Require((await Ready(host, new(root, "code.ts"))).Diagnostics.Count == 0, "Closing a draft did not restore disk analysis.");
            await File.WriteAllTextAsync(Path.Combine(root, "code.ts"), "😀 broken");
            Require((await Ready(host, new(root, "code.ts"))).Diagnostics.Count == 1, "Agent disk edits were not synchronized.");
            await Ready(host, new(root, "code.ts", Text: "😀 target", DocumentId: "editor", Version: 3));
            await File.WriteAllTextAsync(Path.Combine(root, "code.ts"), "😀 broken");
            Require((await Ready(host, new(root, "code.ts"))).Diagnostics.Count == 1, "Clean editor buffer hid newer disk text from agents.");
            var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
            await File.WriteAllTextAsync(Path.Combine(other, "code.ts"), "😀 target");
            var parallel = await Task.WhenAll(Ready(host, new(other, "code.ts")), Ready(host, new(root, "code.ts")));
            Require(parallel[0].Diagnostics.Count == 0 && parallel[1].Diagnostics.Count == 1, "Workspace diagnostics leaked across roots.");
            try { await host.LanguageAsync(new(root, "../escape.ts")); throw new InvalidOperationException("Escaped workspace accepted."); }
            catch (UnauthorizedAccessException) { }
            catch (SharpRail.Host.Protocol.RpcException) when (remote) { }
            using (var cancellation = new CancellationTokenSource(100))
            {
                try { await host.LanguageAsync(new(root, "code.ts", "references"), cancellation.Token); throw new InvalidOperationException("Cancellation ignored."); }
                catch (OperationCanceledException) { }
            }
            var stopped = await host.LanguageAsync(new(root, "code.ts", "stop"));
            Require(stopped.State == "stopped", "Stop did not cancel the session.");
            Require((await host.LanguageAsync(new(root, Operation: "logs", Server: "typescript"))).Detail.Contains("fixture stderr", StringComparison.Ordinal), "Stopping discarded server logs.");
            Require((await host.LanguageAsync(new(root, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State == "stopped",
                "Server listing restarted a stopped session.");
            await host.LanguageAsync(new(root, "code.ts", "retry"));
            await Ready(host, new(root, "code.ts"));
            var failed = await Ready(host, new(root, "bad.py"));
            Require(failed.State == "error" && failed.Freshness == "unavailable", "Provision/start failure reported clean results.");
            Require((await host.LanguageAsync(new(root, Operation: "logs", Server: "python"))).Detail.Contains("startup:", StringComparison.Ordinal), "Failed startup had no log evidence.");
            await File.WriteAllTextAsync(Path.Combine(stateDirectory, "language", "servers.json"), JsonSerializer.Serialize(new
            {
                typescript = new { command = executable, arguments },
                python = new { command = executable, arguments },
                clangd = new { command = executable, arguments = pushArguments }
            }));
            await host.LanguageAsync(new(root, "bad.py", "retry"));
            Require((await Ready(host, new(root, "bad.py"))).State == "ready", "Retry did not reload a repaired launcher.");
            await Ready(host, new(root, "push.cpp"));
            Require((await Ready(host, new(root, "push.cpp"))).Diagnostics.Count == 1, "Versioned push diagnostics are missing.");
            var stalePush = new LanguageRequest(root, "push.cpp", Text: "😀 stale", DocumentId: "push", Version: 1, Dirty: true);
            await Ready(host, stalePush);
            var ignored = await Ready(host, stalePush);
            Require(ignored.Diagnostics.Count == 0 && ignored.Freshness == "pending", "Old push diagnostics were accepted for a newer buffer.");
            var unversioned = stalePush with { Text = "😀 unversioned broken", Version = 2 };
            await Ready(host, unversioned);
            Require((await Ready(host, unversioned)).Freshness == "unversioned", "Unversioned diagnostics were advertised as current.");
            var pushScan = await host.LanguageAsync(new(root, Operation: "diagnostics", Paths: ["push.cpp"]));
            Require(pushScan.Freshness == "partial" && pushScan.Files is [{ State: "pending", Freshness: "unversioned" }], "Batch advertised unversioned push reports as checked.");
            var tools = LanguageMcpTools.Create(state, root);
            var (_, body) = await McpServer.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"path":"code.ts"}}}"""), tools, default);
            Require(body?.ToJsonString().Contains("broken", StringComparison.Ordinal) == true, "MCP diagnostics did not reach agents.");
            await settingsHost.ChangeAsync([HostStateChange.Setting("language-server:python", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            await File.WriteAllTextAsync(Path.Combine(root, "agent.py"), "broken = missing");
            await using var terminals = new PtyTerminalService();
            await using var loopback = new LoopbackServer(terminals, state);
            using var http = new HttpClient();
            foreach (var agentKind in new[] { "codex", "claude" })
            {
                var endpoint = loopback.BaseUrl + "/mcp/" + terminals.Token(new(root, agentKind));
                async Task<JsonNode> Request(string method, object? parameters = null)
                {
                    using var response = await http.PostAsync(endpoint, new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json"));
                    response.EnsureSuccessStatusCode();
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                }
                var listed = (await Request("tools/list"))["result"]!["tools"]!.AsArray();
                Require(new[] { "lsp_navigate", "lsp_diagnostics", "lsp_status" }.All(name => listed.Any(item => item!["name"]!.ToString() == name)),
                    agentKind + " terminal MCP does not expose all language tools.");
                var schema = listed.Single(item => item!["name"]!.ToString() == "lsp_diagnostics")!["inputSchema"]!["properties"]!;
                Require(schema["configuration"] is null && schema["query"] is null && schema["server"] is null, "Diagnostics repeats unrelated tool schema.");
                var navigationSchema = listed.Single(item => item!["name"]!.ToString() == "lsp_navigate")!["inputSchema"]!["properties"]!;
                Require(navigationSchema["configuration"] is null && navigationSchema["operation"]!["enum"]!.AsArray().Any(item => item!.ToString() == "source"), "Navigation lacks bounded source retrieval or repeats configuration schema.");
                Require(schema["paths"] is not null && schema["scope"] is not null, "Agent cannot discover diagnostic scopes.");
                async Task<LanguageResult> Language(string tool, object arguments)
                {
                    var response = await Request("tools/call", new { name = tool, arguments });
                    return JsonSerializer.Deserialize<LanguageResult>(response["result"]!["content"]![0]!["text"]!.GetValue<string>(), JsonSerializerOptions.Web)!;
                }
                var available = await Language("lsp_status", new { operation = "catalog" });
                Require(available.Servers.Count == LanguageServers.Catalog.Length && available.Servers.All(item => item.Detail.Length > 0), "Agent cannot discover language implementations.");
                var started = await Language("lsp_status", new { operation = "retry", server = "python" });
                using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (started.State is "installing" or "initializing")
                {
                    await Task.Delay(30, startupDeadline.Token);
                    started = await Language("lsp_status", new { server = "python" });
                }
                Require(started.State == "ready", "Agent cannot start a second language server without the UI.");
                foreach (var path in new[] { "code.ts", "agent.py" })
                {
                    var navigation = await Language("lsp_navigate", new { path, line = 0, column = 3 });
                    Require(navigation.State == "ready" && navigation.Locations.Any(item => item.Path == path), "Agent navigation crossed language sessions.");
                }
                var configured = await Request("tools/call", new { name = "lsp_status", arguments = new { operation = "configure", server = "python", configuration = new { settings = new { python = new { analysis = new { extraPaths = new[] { "${workspaceFolder}/lib" } } } } } } });
                Require(configured["result"]!["content"]!.ToJsonString().Contains("extraPaths", StringComparison.Ordinal), "Agent configuration did not return saved settings.");
                var restart = await Language("lsp_status", new { operation = "restart", server = "python" });
                while (restart.State is "installing" or "initializing")
                {
                    await Task.Delay(30, startupDeadline.Token);
                    restart = await Language("lsp_status", new { server = "python" });
                }
                Require(restart.State == "ready", "Agent cannot restart its configured server.");
                var hover = await Language("lsp_navigate", new { path = "code.ts", operation = "hover", line = 0, column = 3 });
                Require(hover.Locations.Single().Text.Contains("target: string", StringComparison.Ordinal), "Agent hover lost inferred type information.");
                var symbol = await Language("lsp_navigate", new { path = "outline.ts", operation = "symbolBody", query = "Suite/run" });
                Require(symbol.Locations.Single().Body.Contains("return '😀'", StringComparison.Ordinal), "Agent did not receive the selected symbol body.");
                var mixed = await Language("lsp_diagnostics", new { paths = new[] { "code.ts", "agent.py" } });
                Require(mixed.Files.Count == 2 && mixed.Files.All(file => file.State == "checked") && mixed.Diagnostics.Select(item => item.Path).Distinct().Count() == 2,
                    "Agent mixed-language scan lost diagnostics or coverage.");
                var running = await Language("lsp_status", new { operation = "servers" });
                Require(new[] { "typescript", "python" }.All(id => running.Servers.Any(item => item.Id == id && item.State == "ready")), "Starting a language displaced another session.");
                var called = await Request("tools/call", new { name = "lsp_diagnostics", arguments = new { paths = new[] { "code.ts" } } });
                Require(called["result"]!["content"]!.ToJsonString().Contains("broken", StringComparison.Ordinal), "Terminal-authenticated diagnostics lost the workspace.");
            }
        }
        finally
        {
            if (server is not null) await server.StopAsync();
            else await state.StopLanguagesAsync();
        }
    }

    private static async Task<LanguageResult> Ready(IProjectServices host, LanguageRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var result = await host.LanguageAsync(request, deadline.Token);
            if (result.State is not ("installing" or "initializing")) return result;
            if (request.Operation == "retry") request = request with { Operation = "status" };
            await Task.Delay(30, deadline.Token);
        }
    }

    private static void ProjectModelPolicies(string fixture)
    {
        var root = Path.Combine(fixture, "project-models"); Directory.CreateDirectory(root);
        var examples = new Dictionary<string, string>
        {
            ["csharp"] = "App.csproj",
            ["typescript"] = "tsconfig.json",
            ["python"] = "pyrightconfig.json",
            ["clangd"] = "compile_commands.json",
            ["java"] = "pom.xml",
            ["kotlin"] = "settings.gradle.kts",
            ["intellij"] = "pom.xml",
            ["go"] = "go.mod",
            ["rust"] = "Cargo.toml",
            ["scala"] = "build.sbt",
            ["ruby"] = "Gemfile",
            ["php"] = "composer.json",
            ["swift"] = "Package.swift",
            ["dart"] = "pubspec.yaml",
            ["r"] = "DESCRIPTION",
            ["powershell"] = "Tools.psd1",
            ["shell"] = ".bash-language-serverrc",
            ["css"] = "package.json"
        };
        Require(examples.Keys.Order().SequenceEqual(LanguageServers.Catalog.Select(server => server.Id).Order()), "Every provisionable server needs a project-resolution policy.");
        foreach (var (server, manifest) in examples)
        {
            var module = Path.Combine(root, server, "module"); Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, manifest), "{}");
            var dependency = Path.Combine(root, server, "node_modules", "dependency"); Directory.CreateDirectory(dependency);
            File.WriteAllText(Path.Combine(dependency, manifest), "{}");
            var model = LanguageProjectModel.Discover(Path.Combine(root, server), server, default);
            Require(model.Manifests.SequenceEqual(["module/" + manifest]) && model.Resolution.Length > 0,
                server + " did not discover its project manifest or leaked a dependency/build folder into the project model.");
        }
        var go = LanguageProjectModel.Discover(Path.Combine(root, "go"), "go", default);
        Require(go.WorkspaceFolders.SequenceEqual([Path.Combine(root, "go", "module")]), "gopls did not receive a nested independent module as a workspace folder.");
        var rust = LanguageProjectModel.Discover(Path.Combine(root, "rust"), "rust", default);
        var options = new JsonObject(); rust.ConfigureInitialization(options);
        Require(options["linkedProjects"]![0]!.ToString() == Path.Combine(root, "rust", "module", "Cargo.toml"), "Nested Cargo projects were not linked for rust-analyzer.");
        Require(WorkspaceLanguageConfiguration.Settings(new(), root, "scala")["metals"]!["autoImportBuilds"]!.ToString() == "all", "Metals build imports remained silently disabled.");
        Console.WriteLine("PASS project-resolution policies for all catalog servers, ignored dependencies, nested module roots and native Go/Rust/Metals setup");
    }

    private static async Task ProjectInitialization(string fixture)
    {
        var root = Path.Combine(fixture, "project-initialization"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        var settings = JsonSerializer.Serialize(new { csharp = new { command = executable, arguments } });
        await using var service = new LanguageService(Path.Combine(root, ".tools", "state"), () => true, () => settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        LanguageResult status;
        do
        {
            status = await service.ExecuteAsync(new(root, Operation: "status", Server: "csharp"), timeout.Token);
            if (status.State is "installing" or "initializing") await Task.Delay(20, timeout.Token);
        } while (status.State is "installing" or "initializing");
        Require(status.State == "ready" && File.Exists(Path.Combine(root, ".tools", "project-ready")),
            "A C# server became ready before its project graph initialized.");
        Require(status.Coverage.Contains("Fixture.csproj", StringComparison.Ordinal), "Project model coverage was not exposed.");
    }

    internal static async Task RealCSharp(string fixture)
    {
        var root = Path.Combine(fixture, "real-csharp"); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Library")); Directory.CreateDirectory(Path.Combine(root, "Consumer"));
        await File.WriteAllTextAsync(Path.Combine(root, "Fixture.slnx"), "<Solution><Project Path=\"Library/Library.csproj\" /><Project Path=\"Consumer/Consumer.csproj\" /></Solution>");
        await File.WriteAllTextAsync(Path.Combine(root, "Library", "Library.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(root, "Consumer", "Consumer.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Library/Library.csproj\" /></ItemGroup></Project>");
        const string declaration = "namespace Sample; public sealed class Widget { public int Value => 42; }\n";
        await File.WriteAllTextAsync(Path.Combine(root, "Library", "Widget.cs"), declaration);
        await File.WriteAllTextAsync(Path.Combine(root, "Consumer", "Usage.cs"), "namespace Consumer; public static class Usage { public static int Read() => new Sample.Widget().Value; }\n");
        var dotnet = Path.GetFullPath(".tools/dotnet/dotnet");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var restore = LanguageServers.Start(dotnet, ["restore", Path.Combine(root, "Fixture.slnx")]);
        restore.WorkingDirectory = root; restore.RedirectStandardOutput = restore.RedirectStandardError = true;
        using (var process = System.Diagnostics.Process.Start(restore)!)
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Require(process.ExitCode == 0, "C# fixture restore failed: " + await error + await output);
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail", "language", "servers", "csharp");
        var engine = Directory.EnumerateFiles(installed, "Microsoft.CodeAnalysis.LanguageServer.dll", SearchOption.AllDirectories).First();
        var settings = JsonSerializer.Serialize(new { csharp = new { command = dotnet, arguments = new[] { engine, "--stdio" } } });
        await using var service = new LanguageService(Path.Combine(root, ".tools", "language"), () => true, () => settings);
        var request = new LanguageRequest(root, "Library/Widget.cs", "references", Column: declaration.IndexOf("Widget", StringComparison.Ordinal));
        LanguageResult result;
        do
        {
            result = await service.ExecuteAsync(request, timeout.Token);
            if (result.State is "installing" or "initializing") await Task.Delay(50, timeout.Token);
        } while (result.State is "installing" or "initializing");
        Require(result.State == "ready" && result.Locations.Any(item => item.Path == "Consumer/Usage.cs"),
            "The first ready C# reference query missed an unopened dependent project: " + result.Detail);
        var file = await service.ExecuteAsync(request with { Scope = "file" }, timeout.Token);
        var module = await service.ExecuteAsync(request with { Scope = "module" }, timeout.Token);
        Require(file.Locations.All(item => item.Path == "Library/Widget.cs") && module.State == "unsupported",
            "Reference file scope leaked results or module scope fabricated ownership.");
        var page = await service.ExecuteAsync(request with { Scope = "file", Limit = 1 }, timeout.Token);
        Require(page.Total == file.Total && page.Locations.Count == 1, "Scopes were applied after pagination.");
        Console.WriteLine("PASS real Roslyn solution graph, unopened dependent-project references on first ready query, SDK environment and scoped pagination");
        var checkout = Directory.GetCurrentDirectory();
        const string source = "src/SharpRail.Plugins.UI.Kit/FileDrop.cs";
        var lines = await File.ReadAllLinesAsync(Path.Combine(checkout, source), timeout.Token);
        var declarationLine = Array.FindIndex(lines, line => line.Contains("public sealed class FileDrop", StringComparison.Ordinal));
        var projectRequest = new LanguageRequest(checkout, source, "references", Line: declarationLine, Column: lines[declarationLine].IndexOf("FileDrop", StringComparison.Ordinal));
        do
        {
            result = await service.ExecuteAsync(projectRequest, timeout.Token);
            if (result.State is "installing" or "initializing") await Task.Delay(100, timeout.Token);
        } while (result.State is "installing" or "initializing");
        Require(result.State == "ready" && result.Locations.Any(item => item.Path.StartsWith("src/SharpRail.UI/", StringComparison.Ordinal)),
            "SharpRail FileDrop references missed unopened files in the UI project: " + result.Detail + " " + string.Join(", ", result.Locations.Select(item => item.Path)));
        Console.WriteLine($"PASS real SharpRail solution: FileDrop references span {result.Locations.Select(item => item.Path).Distinct().Count()} files without opening their editors");
    }

    internal static async Task Real(string fixture)
    {
        var root = Path.Combine(fixture, "real-languages"); Directory.CreateDirectory(root);
        var storage = Path.Combine(Directory.GetCurrentDirectory(), ".tools", "language-check");
        Directory.CreateDirectory(storage);
        await File.WriteAllTextAsync(Path.Combine(storage, "servers.json"), """{}""");
        var configurations = new Dictionary<string, string>();
        var service = new LanguageService(storage, () => true, workspaceConfiguration: (_, id) => configurations.GetValueOrDefault(id) ?? "{}",
            saveConfiguration: (_, id, json) => configurations[id] = json);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "modules")); Directory.CreateDirectory(Path.Combine(root, "consumer"));
            Directory.CreateDirectory(Path.Combine(root, "pythonpkg")); Directory.CreateDirectory(Path.Combine(root, "native"));
            await File.WriteAllTextAsync(Path.Combine(root, "tsconfig.json"), """{"compilerOptions":{"strict":true,"noEmit":true,"baseUrl":".","paths":{"@model/*":["modules/*"]}},"include":["**/*.ts"]}""");
            await File.WriteAllTextAsync(Path.Combine(root, "modules", "lib.ts"), "export function shared() { return 42; }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "consumer", "use.ts"), "import { shared } from '@model/lib'; export const answer = shared();\n");
            await File.WriteAllTextAsync(Path.Combine(root, "pyrightconfig.json"), """{"include":["."]}""");
            await File.WriteAllTextAsync(Path.Combine(root, "pythonpkg", "__init__.py"), "");
            await File.WriteAllTextAsync(Path.Combine(root, "pythonpkg", "lib.py"), "class Shared: pass\n");
            await File.WriteAllTextAsync(Path.Combine(root, "consumer", "use.py"), "from pythonpkg.lib import Shared\nvalue = Shared()\n");
            await File.WriteAllTextAsync(Path.Combine(root, "native", "shared.h"), "int shared();\n");
            await File.WriteAllTextAsync(Path.Combine(root, "native", "definition.cpp"), "#include \"shared.h\"\nint shared() { return 42; }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "native", "usage.cpp"), "#include \"shared.h\"\nint use() { return shared(); }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "compile_commands.json"), JsonSerializer.Serialize(new[] { "code.cpp", "native/definition.cpp", "native/usage.cpp" }
                .Select(file => new { directory = root, file = Path.Combine(root, file), arguments = new[] { "clang++", "-std=c++17", "-I" + Path.Combine(root, "native"), "-c", Path.Combine(root, file) } })));
            await File.WriteAllTextAsync(Path.Combine(root, "code.ts"), "const value: string = 42;\nconsole.log(value);\n");
            await File.WriteAllTextAsync(Path.Combine(root, "code.py"), "value: str = 42\n");
            await File.WriteAllTextAsync(Path.Combine(root, "code.cpp"), "int value = \"bad\";\n");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            async Task CrossFile(string source, int line, int column, string expected)
            {
                LanguageResult references;
                do
                {
                    references = await service.ExecuteAsync(new(root, source, "references", Line: line, Column: column), deadline.Token);
                    if (references.State is "installing" or "initializing") await Task.Delay(100, deadline.Token);
                } while (references.State is "installing" or "initializing");
                Require(references.State == "ready" && references.Locations.Any(item => item.Path == expected),
                    source + " missed an unopened import/project reference: " + references.Detail + " " + string.Join(", ", references.Locations.Select(item => item.Path)));
                Console.WriteLine("PASS real cross-file module resolution: " + source + " -> " + expected);
            }
            foreach (var path in new[] { "code.ts", "code.py", "code.cpp" })
            {
                LanguageResult result;
                do
                {
                    result = await service.ExecuteAsync(new(root, path), deadline.Token);
                    if (result.State == "error") throw new InvalidOperationException(path + ": " + result.Detail);
                    if (result.Diagnostics.Count == 0) await Task.Delay(200, deadline.Token);
                } while (result.Diagnostics.Count == 0);
                Require(result.Diagnostics.Any(item => item.Kind == "1"), path + " did not report its intentional type error.");
                if (path == "code.ts")
                {
                    var navigation = await service.ExecuteAsync(new(root, path, "definition", Line: 1, Column: 13), deadline.Token);
                    Require(navigation.Locations.Any(item => item.Line == 0), "Real TypeScript definition failed.");
                    Require(result.Tokens.Count > 0, "Real TypeScript semantic tokens missing.");
                    Require(navigation.Locations.Any(item => item.Preview.Contains("value", StringComparison.Ordinal)), "Real TypeScript navigation omitted its source excerpt.");
                    var hover = await service.ExecuteAsync(new(root, path, "hover", Line: 0, Column: 7), deadline.Token);
                    Require(hover.Locations.Any(item => item.Text.Contains("string", StringComparison.Ordinal)), "Real TypeScript hover omitted its declared type.");
                    var outline = await service.ExecuteAsync(new(root, path, "symbols"), deadline.Token);
                    Require(outline.Locations.Any(item => item.Text == "value"), "Real TypeScript outline omitted its declaration.");
                    var body = await service.ExecuteAsync(new(root, path, "symbolBody", Query: "value"), deadline.Token);
                    Require(body.State == "ready" && body.Locations.Any(item => item.Body.Contains("42", StringComparison.Ordinal)), "Real TypeScript body did not use its declaration range.");
                }
                if (path == "code.ts") await CrossFile("modules/lib.ts", 0, 16, "consumer/use.ts");
                if (path == "code.py") await CrossFile("pythonpkg/lib.py", 0, 6, "consumer/use.py");
                if (path == "code.cpp")
                {
                    await CrossFile("native/shared.h", 0, 4, "native/usage.cpp");
                    var fallbackRoot = Path.Combine(root, "fallback");
                    Directory.CreateDirectory(fallbackRoot);
                    await File.WriteAllTextAsync(Path.Combine(fallbackRoot, ".clangd"), "CompileFlags:\n  CompilationDatabase: None\n");
                    Directory.CreateDirectory(Path.Combine(fallbackRoot, "includes"));
                    await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "includes", "fixture_library.h"), "#define LIBRARY_VALUE 42\n");
                    await File.WriteAllTextAsync(Path.Combine(fallbackRoot, "library.cpp"), "#include <fixture_library.h>\nint library = LIBRARY_VALUE;\n");
                    LanguageResult missingLibrary;
                    do
                    {
                        missingLibrary = await service.ExecuteAsync(new(fallbackRoot, "library.cpp"), deadline.Token);
                        if (missingLibrary.Diagnostics.Count == 0) await Task.Delay(100, deadline.Token);
                    } while (missingLibrary.Diagnostics.Count == 0);
                    await service.ExecuteAsync(new(fallbackRoot, Operation: "restart", Server: "clangd", Configuration: """{"includePaths":["includes"],"compilerFlags":["-std=c++17"]}"""), deadline.Token);
                    LanguageResult fixedLibrary;
                    do
                    {
                        fixedLibrary = await service.ExecuteAsync(new(fallbackRoot, "library.cpp"), deadline.Token);
                        if (fixedLibrary.State == "error") throw new InvalidOperationException(fixedLibrary.Detail);
                        if (fixedLibrary.State != "ready" || fixedLibrary.Freshness == "pending" || fixedLibrary.Diagnostics.Count > 0) await Task.Delay(100, deadline.Token);
                    } while (fixedLibrary.State != "ready" || fixedLibrary.Freshness == "pending" || fixedLibrary.Diagnostics.Count > 0);
                    Console.WriteLine("PASS real clangd workspace include paths applied by restart and resolved missing library diagnostics");
                }
                Console.WriteLine($"PASS real {result.Server}: diagnostics ({result.Freshness}), tokens={result.Tokens.Count}");
            }
        }
        finally { await service.DisposeAsync(); Directory.Delete(root, true); }
    }

    internal static void CaptureProjectFolders(Window window) => CaptureLsp(window, "project-folders");

    internal static void SettingsChecks(string fixture)
    {
        ExternalColoringAndNavigationUi(fixture);
        MenuAvailabilityUi(fixture);
        SettingsUi(fixture);
        DocumentUi(fixture);
    }

    internal static void UiChecks(string fixture)
    {
        ProviderIconsUi();
        DependencySourceUi(fixture);
        EmptyNavigationUi(fixture);
        InitialColoringUi(fixture);
        LanguageProjectChecks.Ui(fixture);
        var frame = new EditorFrame("// 😀\nconst target = 1;", "LanguageEditor");
        frame.Highlight("code.ts");
        var window = new Avalonia.Controls.Window { Width = 640, Height = 300, Content = frame };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            E2E.E2eWorkspace.Until(() => frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, 0) >= 40);
            Require(frame.HighlightingReady.IsCompleted, "Editor was revealed before initial syntax colors completed.");
            var text = frame.Editor.Text;
            var apply = frame.ApplySemanticTokensAsync(text, [new(1, 6, 6, "type")]);
            E2E.E2eWorkspace.Until(() => apply.IsCompleted); apply.GetAwaiter().GetResult();
            Require(frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, 3) == 40 + Array.IndexOf(TextMateHighlighter.Roles, "comment"), "Sparse semantic tokens erased syntax comments.");
            var keyword = Encoding.UTF8.GetByteCount(text.AsSpan(0, text.IndexOf("const", StringComparison.Ordinal)));
            Require(frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, keyword) == 40 + Array.IndexOf(TextMateHighlighter.Roles, "keyword"), "Sparse semantic tokens erased syntax keywords.");
            var offset = text.IndexOf("target", StringComparison.Ordinal);
            var position = Encoding.UTF8.GetByteCount(text.AsSpan(0, offset));
            Require(frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, position) == 40 + Array.IndexOf(TextMateHighlighter.Roles, "type"), "Semantic role did not override TextMate.");
            var italic = frame.ApplySemanticTokensAsync(text, [new SemanticToken(1, 6, 6, "method") { Italic = true }]);
            E2E.E2eWorkspace.Until(() => italic.IsCompleted); italic.GetAwaiter().GetResult();
            using (var capture = window.CaptureRenderedFrame())
            {
                var style = frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, position);
                Require(frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.StyleGetItalic, style) == 1,
                    "A semantic italic token did not reach the native font style.");
            }
            var upright = frame.ApplySemanticTokensAsync(text, [new(1, 6, 6, "method")]);
            E2E.E2eWorkspace.Until(() => upright.IsCompleted); upright.GetAwaiter().GetResult();
            using (var capture = window.CaptureRenderedFrame())
            {
                var style = frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, position);
                Require(frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.StyleGetItalic, style) == 0,
                    "Italic styling leaked into an ordinary semantic token.");
            }
            frame.Editor.SetDiagnostics([new(position, 6, 1)], Ui.Danger.Color, Ui.Warning.Color, Ui.Accent.Color);
            Require(frame.Editor.Document.Send((SharpRail.Scintilla.ScintillaMessage)2507, 8, position) != 0 && !frame.Editor.IsModified, "Diagnostic underline is missing or modified source text.");
            var stale = frame.ApplySemanticTokensAsync(text, [new(1, 6, 6, "comment")]);
            frame.Editor.Text = "const newest = 2;";
            E2E.E2eWorkspace.Until(() => stale.IsCompleted);
            Require(frame.Editor.Text == "const newest = 2;", "Semantic application changed document text.");
            window.Content = null; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            frame.Editor.Text = "// short"; window.Content = frame;
            E2E.E2eWorkspace.Until(() => frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, 3) == 40 + Array.IndexOf(TextMateHighlighter.Roles, "comment"));
            Console.WriteLine("PASS semantic editor overlay, Unicode diagnostic squiggles, stale results and detach/reattach fallback");
        }
        finally { window.Close(); frame.Editor.Dispose(); SynchronizationContext.SetSynchronizationContext(previous); }
        DocumentUi(fixture);
        SettingsUi(fixture);
        LanguagePaneNarrowChecks.Run(fixture);
        using var toolWorkspace = new E2E.E2eWorkspace(Path.Combine(fixture, "language-tool-rail"), openFiles: false);
        toolWorkspace.Click(toolWorkspace.Find<Button>("RailUnplaced_lsp"));
        E2E.E2eWorkspace.Until(() => toolWorkspace.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "LspPanel" && control.IsEffectivelyVisible));
        Require(toolWorkspace.Window.Layout.State.Groups.Any(group => group.Tools.Any(tool => tool.Id == "lsp")),
            "The unplaced LSP rail action did not restore the workspace pane.");
        Console.WriteLine("PASS LSP tool-rail restoration in a non-Git workspace");
    }

    private static void ExternalColoringAndNavigationUi(string fixture)
    {
        var root = Path.Combine(fixture, "external-coloring-ui"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Example.cs"), string.Concat(Enumerable.Repeat("public class Example {}\n", 200)));
        using var app = new E2E.E2eWorkspace(root, openFiles: false, prepare: host =>
            host.LanguageReply = request => request.Operation == "status" ? new("ready", Server: "csharp") { SemanticColoring = true }
                : new("unavailable", "No semantic result for external source", "csharp"));
        E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        using var external = new SharpRail.UI.Editor.CodeDocumentView(new(Path.Combine(fixture, "CancellationToken.cs"), "public readonly struct CancellationToken {}") { ReadOnly = true },
            root, app.Host, () => { }, _ => { }, () => { }, error => throw error);
        var sourceWindow = new Window { Width = 640, Height = 300, Content = external };
        sourceWindow.Show(app.Window);
        try
        {
            E2E.E2eWorkspace.Until(() => external.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, 0) >= 40);
            Require(external.Editor.IsReadOnly, "Highlighting changed external-source editability.");
        }
        finally { sourceWindow.Close(); }
        var open = app.Window.OpenDocumentAsync("Example.cs", true, line: 100, column: 1);
        E2E.E2eWorkspace.Until(() => open.IsCompleted); open.GetAwaiter().GetResult();
        var editor = app.Window.GetVisualDescendants().OfType<SharpRail.UI.Editor.CodeDocumentView>().Single().Editor;
        E2E.E2eWorkspace.Until(() => editor.Selection.StartLine == 100);
        var visible = editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.LinesOnScreen);
        var target = editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.VisibleFromDocLine, 99);
        Require(Math.Abs(target - editor.FirstVisibleLine - visible / 2) <= 2, "Declaration target was not centered in the editor viewport.");
        Console.WriteLine("PASS external C# lexical fallback and centered positioned navigation");
    }

    private static void MenuAvailabilityUi(string fixture)
    {
        var root = Path.Combine(fixture, "language-menu-ui"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Example.ts"), "const example = 1;");
        var serverState = "idle";
        var statuses = 0;
        using var app = new E2E.E2eWorkspace(root, openFiles: false, prepare: host =>
            host.LanguageReply = request =>
            {
                if (request.Operation == "status") statuses++;
                return new(serverState, "Fixture status", "typescript");
            });
        E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        var open = app.Window.OpenDocumentAsync("Example.ts", true);
        E2E.E2eWorkspace.Until(() => open.IsCompleted); open.GetAwaiter().GetResult();
        var view = app.Window.GetVisualDescendants().OfType<SharpRail.UI.Editor.CodeDocumentView>().Single();
        var menu = view.Editor.ContextMenu!;
        foreach (var state in new[] { "idle", "ready", "stopped", "disabled", "unsupported", "unavailable", "initializing" })
        {
            serverState = state;
            var before = statuses;
            view.Editor.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            E2E.E2eWorkspace.Until(() => statuses > before);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            E2E.E2eWorkspace.Until(() => menu.Items.OfType<Control>().Any(item => Equals(item.Tag, "lsp")) == (state is "ready" or "initializing"));
            menu.Close();
        }
        Console.WriteLine("PASS editor language menu follows file-specific running server status");
    }

    private static void EmptyNavigationUi(string fixture)
    {
        var root = Path.Combine(fixture, "empty-navigation-ui"); Directory.CreateDirectory(root);
        var unavailable = false;
        File.WriteAllText(Path.Combine(root, "Example.ts"), "const example = 1;\n");
        using var app = new E2E.E2eWorkspace(root, openFiles: false, prepare: host =>
            host.LanguageReply = request => request.Operation is "definition" or "inspect" ? new(unavailable ? "unavailable" : "ready", unavailable ? "Fixture server unavailable" : "Ready", "typescript") { Freshness = "current" } : null);
        E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        var open = app.Window.OpenDocumentAsync("Example.ts", true);
        E2E.E2eWorkspace.Until(() => open.IsCompleted); open.GetAwaiter().GetResult();
        var view = app.Window.GetVisualDescendants().OfType<SharpRail.UI.Editor.CodeDocumentView>().Single();
        view.Editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F12 });
        E2E.E2eWorkspace.Until(() => view.FindControl<TextBlock>("LanguageHeading")!.Text == "No declaration found" && view.FindControl<Avalonia.Controls.Primitives.Popup>("LanguagePopup")!.IsOpen);
        app.Window.UpdateLayout();
        Require(!view.FindControl<TextBox>("LanguageQuery")!.IsVisible && !view.FindControl<ListBox>("LanguageItems")!.IsVisible &&
            !view.FindControl<TextBlock>("LanguageKeys")!.IsVisible && view.FindControl<Border>("LanguageResults")!.Bounds.Height < 140,
            "Empty declaration navigation retained a bulky chooser.");
        CaptureLsp(app.Window, "empty-navigation");
        view.FindControl<Avalonia.Controls.Primitives.Popup>("LanguagePopup")!.IsOpen = false;
        unavailable = true;
        view.Editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F12 });
        // The status text is written before the result list and heading; the popup reopening marks the finished search.
        E2E.E2eWorkspace.Until(() => view.FindControl<TextBlock>("LanguageStatus")!.Text!.Contains("Fixture server unavailable", StringComparison.Ordinal) &&
            view.FindControl<Avalonia.Controls.Primitives.Popup>("LanguagePopup")!.IsOpen);
        Require(view.FindControl<TextBlock>("LanguageStatus")!.IsVisible && view.FindControl<TextBlock>("LanguageHeading")!.Text != "No declaration found",
            "Unavailable analysis was misreported as a completed empty declaration search.");
        Console.WriteLine("PASS empty declaration chooser: compact message, no filter, list or keyboard footer");
    }

    private static void InitialColoringUi(string fixture)
    {
        var root = Path.Combine(fixture, "initial-coloring-ui"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Example.java"), "public class Example {}\n");
        using var app = new E2E.E2eWorkspace(root, openFiles: false, prepare: host =>
            host.LanguageReply = request => request.Operation is "inspect" or "status" ? new("initializing", "Importing fixture project", "java") { SemanticColoring = true } : null);
        E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        var open = app.Window.OpenDocumentAsync("Example.java", true);
        E2E.E2eWorkspace.Until(() => open.IsCompleted); open.GetAwaiter().GetResult();
        var frame = app.Window.GetVisualDescendants().OfType<EditorFrame>().Single();
        E2E.E2eWorkspace.Until(() => frame.Opacity == 1 && frame.HighlightingReady.IsCompleted);
        Require(frame.DeferSyntaxColors && frame.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.GetStyleAt, 0) == 0,
            "An initializing semantic server exposed an intermediate TextMate pass.");
        Require(!app.Window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "ShowSyntaxColors"),
            "The removed coloring escape hatch is still visible.");
        Console.WriteLine("PASS initial coloring: plain text while semantic analysis starts, no TextMate flash or loading prompt");
    }

    private static void DependencySourceUi(string fixture)
    {
        const string uri = "jar:file:///library%20space-sources.jar!/jvmMain/kotlin/String.kt";
        var source = "package kotlin\nclass String\n" + new string(' ', 13_000);
        var requests = new List<LanguageRequest>();
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "dependency-source-ui"), openFiles: false, prepare: host =>
        {
            host.LanguageReply = request =>
            {
                if (request.Operation != "source") return null;
                requests.Add(request);
                Require(request.Path == uri && request.Server == "intellij" && request.Text is null && !request.Dirty,
                    "Dependency source lost its exact URI, owner or read-only request.");
                var body = source.Substring(request.Offset, Math.Min(12_000, source.Length - request.Offset));
                return new("ready", Server: "intellij")
                {
                    Locations = [new(uri, 0, 0, 0, 0, Body: body)],
                    Total = source.Length,
                    Truncated = request.Offset + body.Length < source.Length
                };
            };
        });
        E2E.E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
        var open = app.Window.OpenLanguageDocumentAsync(new(uri, 1, 6, 1, 12, Source: "intellij"), true);
        E2E.E2eWorkspace.Until(() => open.IsCompleted); open.GetAwaiter().GetResult();
        E2E.E2eWorkspace.Until(() => app.Window.GetVisualDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Any(editor => editor.Text == source));
        var editor = app.Window.GetVisualDescendants().OfType<SharpRail.Scintilla.ScintillaEditor>().Single(editor => editor.Text == source);
        Require(editor.IsReadOnly && requests.Count == 2 && requests[1].Offset == 12_000 && !app.Host.Reads.ContainsKey(uri),
            "Dependency opening must page source through the host and never use workspace file I/O.");
        Require(app.Tabs.Any(tab => tab.Path == uri), "Dependency tab lost URI identity.");
        E2E.E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ExternalSourceMark"));
        Console.WriteLine("PASS read-only source-JAR navigation: exact URI, owner, pagination, complete text and no workspace file read");
    }

    internal static void ProviderIconsUi()
    {
        foreach (var theme in SharpRail.UI.Rendering.Themes.All)
            foreach (var other in new[] { "variable", "property", "function" })
                Require(theme.Syntax["type"] != theme.Syntax[other], $"Theme '{theme.Id}' paints types and {other} identically.");
        using (var png = Avalonia.Platform.AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/LanguageIcons/csharp-provider.png")))
        using (var bitmap = SkiaSharp.SKBitmap.Decode(png))
            Require(bitmap.GetPixel(0, 0).Alpha == 0 && bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).Alpha == 0,
                "Roslyn provider artwork must have a transparent background.");
        var original = Ui.Theme;
        var icons = LanguageServers.Catalog.Select(server => SharpRail.UI.Panels.LanguageServerIconConverter.Create(server.Id)).ToArray();
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8) };
        foreach (var icon in icons) panel.Children.Add(icon);
        var window = new Window { Width = 620, Height = 64, WindowDecorations = WindowDecorations.None, Content = panel };
        window.Show();
        try
        {
            Avalonia.Media.IImage? previous = null;
            foreach (var name in new[] { "dark", "light" })
            {
                Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve(name));
                window.Background = Ui.Workspace;
                E2E.E2eWorkspace.Until(() => icons.All(icon => icon.GetVisualDescendants().OfType<Image>().SingleOrDefault()?.Source is not null));
                var roslyn = icons.Single(icon => Equals(icon.Tag, "language:csharp"));
                Require(roslyn.GetVisualDescendants().OfType<Image>().Single().Source is Avalonia.Media.Imaging.Bitmap,
                    "Roslyn must use its supplied provider bitmap rather than a generic language mark.");
                var intellij = icons.Single(icon => Equals(icon.Tag, "language:intellij"));
                var image = intellij.GetVisualDescendants().OfType<SvgAsset>().Single();
                Require(previous is null || !ReferenceEquals(previous, image.Source), "IntelliJ's vector mark did not follow the theme foreground.");
                Require(icons.All(icon => icon.Width == 24 && icon.Height == icon.Width), "Provider marks must retain compact dimensions and the official IntelliJ icon’s minimum size.");
                previous = image.Source;
                CaptureLsp(window, "server-icons-" + name);
            }
            Require(SharpRail.UI.Panels.LanguageServerIconConverter.Create("unknown-provider") is Border { OpacityMask: not null },
                "An unknown provider lost its generic vector fallback.");
        }
        finally { window.Close(); Ui.Apply(original); }
        Console.WriteLine("PASS every language provider renders its publisher mark in dark/light themes, including transparent Roslyn artwork");
    }

    internal static void CaptureLsp(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("SHARPRAIL_LSP_CAPTURE") != "1") return;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(CheckRoot.Artifact("lsp-ui", name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void SettingsUi(string fixture)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "lsp-settings"), openFiles: false);
        var settings = new SharpRail.UI.Panels.SettingsWindow(app.Window, () => { }, section: "Code Intelligence");
        settings.Show(app.Window);
        T Find<T>(string name) where T : Avalonia.Controls.Control => settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        try
        {
            E2E.E2eWorkspace.Until(() => Find<Avalonia.Controls.ListBox>("LspServer").ItemCount == LanguageServers.Catalog.Length);
            Require(Find<ComboBox>("JvmProvider") is not null, "Settings omitted workspace provider selection.");
            Require(Find<TextBlock>("LspLifetimeNote").Text == SharpRail.UI.Panels.LanguageNotes.Lifetime && Find<TextBlock>("LspLifetimeNote").Text!.Contains("lifetime of their workspace", StringComparison.Ordinal),
                "Settings did not tell the user that servers run for their workspace's lifetime and that agents start them.");
            Require(!settings.GetLogicalDescendants().OfType<CheckBox>().Any(control => control.Name == "ShowAirContext"),
                "Code Intelligence still offers an Air Context switch; the plugin is turned off in Settings › Plugins.");
            var rail = Find<Avalonia.Controls.ListBox>("LspServer");
            Require(!settings.GetLogicalDescendants().OfType<ComboBox>().Any(control => control.Name == "LspServer"), "Server settings must use navigation, not a dropdown.");
            rail.SelectedItem = "python";
            Require(Find<TextBlock>("LspServerHeading").Text == "python", "Server rail did not navigate.");
            Require(Find<TextBlock>("LspImplementation").Text == "Pyright · Microsoft", "Settings hid the implementation provider.");
            E2E.E2eWorkspace.Until(() => Find<WrapPanel>("LspProviderLinks").IsVisible && Find<Button>("LspProviderRepository").Tag is Uri repository && repository.AbsoluteUri.Contains("microsoft/pyright", StringComparison.Ordinal));
            foreach (var provider in LanguageServers.Catalog)
            {
                rail.SelectedItem = provider.Id;
                var metadata = JsonNode.Parse(LanguageConfigurationCatalog.Read(provider.Id).Configuration)!;
                E2E.E2eWorkspace.Until(() => Find<WrapPanel>("LspProviderLinks").IsVisible && Find<Button>("LspProviderRepository").Tag is Uri repository && repository.AbsoluteUri == metadata["repository"]!.ToString());
            }
            rail.SelectedItem = "java";
            Require(Find<TextBlock>("LspImplementation").Text!.Contains("Eclipse JDT Language Server", StringComparison.Ordinal), "Java implementation is unclear.");
            Require(!settings.GetLogicalDescendants().OfType<CheckBox>().Any(control => control.Content?.ToString()?.StartsWith("Prefer the managed", StringComparison.Ordinal) == true), "The global prefer-managed switch still exists.");
            E2E.E2eWorkspace.Until(() => Find<TextBlock>("LspResolved").Text!.StartsWith("Managed: ", StringComparison.Ordinal) && !Find<TextBlock>("LspPathWarning").IsVisible);
            Require(Find<CheckBox>("LspAllowPath").IsChecked == false, "PATH use is not off by default.");
            rail.SelectedItem = "swift";
            E2E.E2eWorkspace.Until(() => Find<TextBlock>("LspResolved").Text!.Contains("needs an installation", StringComparison.Ordinal));
            rail.SelectedItem = "typescript";
            var feature = Find<CheckBox>("LspFeatureEnabled");
            feature.IsChecked = false;
            feature.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => !app.State!.Current.Settings.LspEnabled && feature.IsEnabled);
            feature.IsChecked = true;
            feature.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => app.State!.Current.Settings.LspEnabled && feature.IsEnabled);
            Find<Avalonia.Controls.CheckBox>("LspEnabled").IsChecked = false;
            Find<Avalonia.Controls.TextBox>("LspCommand").Text = "/custom/language-server";
            Find<Avalonia.Controls.CheckBox>("LspDefaultArguments").IsChecked = false;
            Find<Avalonia.Controls.TextBox>("LspArguments").Text = "--stdio\n--fixture";
            Find<Avalonia.Controls.Button>("LspSave").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => app.State!.Current.Settings.LanguageServers.Contains("/custom/language-server", StringComparison.Ordinal));
            var saved = JsonSerializer.Deserialize<Dictionary<string, LanguageServerOptions>>(app.State!.Current.Settings.LanguageServers, JsonSerializerOptions.Web)!["typescript"];
            Require(saved.Disabled && saved.Arguments is ["--stdio", "--fixture"], "LSP settings lost launcher choices.");
            var update = Task.Run(async () => await app.State.ChangeAsync([HostStateChange.Setting("language-server:typescript", "{}")]));
            E2E.E2eWorkspace.Until(() => update.IsCompleted && Find<Avalonia.Controls.CheckBox>("LspEnabled").IsChecked == true);
            update.GetAwaiter().GetResult();
            Require(Find<Avalonia.Controls.TextBox>("LspCommand").Text == "", "Another client’s LSP settings did not refresh the page.");
            Require(!Find<Avalonia.Controls.CheckBox>("LspDefaultArguments").IsEnabled && !Find<Avalonia.Controls.TextBox>("LspArguments").IsEnabled,
                "Automatic launchers must not expose ineffective argument overrides.");
            CaptureLsp(settings, "settings");
        }
        finally { settings.Close(); }
        Console.WriteLine("PASS LSP Settings category, launcher persistence and shared-state refresh");
    }

    private static void DocumentUi(string fixture)
    {
        var root = Path.Combine(fixture, "language-ui"); Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, ".tools", "state"); Directory.CreateDirectory(Path.Combine(statePath, "language"));
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        File.WriteAllText(Path.Combine(statePath, "language", "servers.json"), JsonSerializer.Serialize(new { typescript = new { command = executable, arguments }, python = new { command = Path.Combine(root, "missing-server"), arguments = Array.Empty<string>() } }));
        File.WriteAllText(Path.Combine(root, "code.ts"), "😀 target");
        File.WriteAllText(Path.Combine(root, "z-other.ts"), "target(); // unopened file");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = new HostStateStore(statePath);
        var view = new SharpRail.UI.Editor.CodeDocumentView(new("code.ts", "😀 target"), root, new LocalProjectAdapter(new ProjectServices(root, state)),
            () => { }, _ => { }, () => { }, error => throw error);
        LanguageItem? navigated = null;
        view.NavigateLanguage = item => { navigated = item; return Task.CompletedTask; };
        var window = new Avalonia.Controls.Window
        {
            Width = 800,
            Height = 400,
            Content = view,
            RequestedThemeVariant = SharpRail.Plugins.UI.Kit.Ui.Theme.IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark
        };
        window.Show();
        try
        {
            var status = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(view, "LanguageStatus")!;
            E2E.E2eWorkspace.Until(() => status.Text?.Contains("current", StringComparison.Ordinal) == true);
            view.Editor.ReplaceRange(3, 6, "broken");
            view.Editor.ReplaceRange(view.Editor.Text.Length, 0, " // preview comment");
            E2E.E2eWorkspace.Until(() => status.Text?.Contains("1 problems", StringComparison.Ordinal) == true);
            var diagnosticByte = Encoding.UTF8.GetByteCount("😀 ");
            var hoverPoint = new Point((double)view.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.PointXFromPosition, 0, diagnosticByte) + 2,
                (double)view.Editor.Document.Send(SharpRail.Scintilla.ScintillaMessage.PointYFromPosition, 0, diagnosticByte) + 4);
            window.MouseMove(view.Editor.TranslatePoint(hoverPoint, window)!.Value);
            E2E.E2eWorkspace.Until(() => ToolTip.GetIsOpen(view.Editor));
            Require(ToolTip.GetTip(view.Editor) is TextBlock hover && hover.Text!.Contains("Error · fixture", StringComparison.Ordinal) && hover.Text.Contains("broken", StringComparison.Ordinal),
                "Hovering the Unicode diagnostic underline did not show its severity, source and message.");
            window.MouseMove(view.Editor.TranslatePoint(new Point(view.Editor.Bounds.Width - 8, hoverPoint.Y), window)!.Value);
            Require(!ToolTip.GetIsOpen(view.Editor), "Diagnostic hover remained over blank space.");
            var openedWith = -1;
            view.Editor.ContextMenu!.Opened += (_, _) => openedWith = view.Editor.ContextMenu.Items.Count;
            view.Editor.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            E2E.E2eWorkspace.Until(() => view.Editor.ContextMenu!.IsOpen && view.Editor.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Name == "Language_diagnostics"));
            Require(openedWith == view.Editor.ContextMenu.Items.Count,
                $"The editor context menu opened with {openedWith} items and grew to {view.Editor.ContextMenu.Items.Count} afterwards, so its native popup clipped them.");
            var problems = view.Editor.ContextMenu!.Items.OfType<Avalonia.Controls.MenuItem>().Single(item => item.Name == "Language_diagnostics");
            var menuRoot = (Avalonia.Visual)TopLevel.GetTopLevel(problems)!;
            E2E.E2eWorkspace.Until(() => menuRoot.Bounds.Height > 0 && problems.IsArrangeValid);
            Require(view.Editor.ContextMenu.Items.OfType<MenuItem>().All(item => item.TranslatePoint(new Point(0, item.Bounds.Height), menuRoot) is { } bottom && bottom.Y <= menuRoot.Bounds.Height + 0.5),
                $"The editor context menu's popup ({menuRoot.Bounds.Height:0}px tall) clipped its items.");
            problems.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
            view.Editor.ContextMenu.Close();
            var list = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.ListBox>(view, "LanguageItems")!;
            E2E.E2eWorkspace.Until(() => list.ItemCount == 1);
            Require(list.Items[0]?.ToString()?.Contains("broken", StringComparison.Ordinal) == true, "Problems did not show the unsaved diagnostic.");
            var popup = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Primitives.Popup>(view, "LanguagePopup")!;
            Require(popup.IsOpen, "Problems did not open a popup.");
            var codePreview = list.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == "LanguagePreview");
            E2E.E2eWorkspace.Until(() => codePreview.FontFamily == Ui.CodeFont && codePreview.Inlines!.OfType<Avalonia.Controls.Documents.Run>()
                .Any(run => run.Text!.Contains("preview comment", StringComparison.Ordinal) && run.Foreground is Avalonia.Media.SolidColorBrush color && color.Color == Ui.Theme.Syntax["comment"]));
            Require(codePreview.FontFamily == Ui.CodeFont && codePreview.Inlines!.OfType<Avalonia.Controls.Documents.Run>()
                .Any(run => run.Text!.Contains("preview comment", StringComparison.Ordinal) && run.Foreground is Avalonia.Media.SolidColorBrush color && color.Color == Ui.Theme.Syntax["comment"]),
                "LSP result code previews must use the code font and syntax-colored runs.");
            if (Environment.GetEnvironmentVariable("SHARPRAIL_LSP_CAPTURE") == "1")
            {
                var results = view.FindControl<Border>("LanguageResults")!;
                using var capture = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Math.Ceiling(results.Bounds.Width), (int)Math.Ceiling(results.Bounds.Height)), new Vector(96, 96));
                capture.Render(results);
                capture.Save(CheckRoot.Artifact("lsp-ui", "code-preview.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            popup.IsOpen = false;
            view.Editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F12, KeyModifiers = Avalonia.Input.KeyModifiers.Shift });
            E2E.E2eWorkspace.Until(() => popup.IsOpen && list.ItemCount == 3 && Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(view, "LanguageHeading")!.Text!.StartsWith("Usages", StringComparison.Ordinal));
            Require(list.Items[0]!.ToString()!.Contains("broken", StringComparison.Ordinal), "Usages popup lost its source preview.");
            Require(list.ItemCount == 3, "Usages did not include the unopened file.");
            var filter = view.FindControl<TextBox>("LanguageQuery")!;
            filter.Text = "z-other.ts";
            E2E.E2eWorkspace.Until(() => list.ItemCount == 1 && view.FindControl<TextBlock>("LanguageSelection")!.Text?.StartsWith("z-other.ts:", StringComparison.Ordinal) == true);
            filter.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            E2E.E2eWorkspace.Until(() => navigated is { Path: "z-other.ts" });
            Require(!popup.IsOpen, "Enter from the filter did not navigate and dismiss.");
            navigated = null;
            window.Width = 360;
            window.UpdateLayout();
            view.Editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.F7, KeyModifiers = Avalonia.Input.KeyModifiers.Alt });
            E2E.E2eWorkspace.Until(() => popup.IsOpen && list.ItemCount == 3);
            Require(view.FindControl<Border>("LanguageResults")!.Width <= 328, "The usages chooser overflowed a narrow owner window.");
            filter.Text = "nonexistent-result";
            E2E.E2eWorkspace.Until(() => list.ItemCount == 0 && view.FindControl<TextBlock>("LanguageEmpty")!.IsVisible);
            filter.Text = "";
            E2E.E2eWorkspace.Until(() => list.ItemCount == 3);
            var scope = view.FindControl<ComboBox>("LanguageScope")!;
            Require(scope.SelectedIndex == 1 && scope.ItemCount == 3 && scope.IsVisible, "Usages scopes did not default to workspace or exposed an unsupported module scope.");
            scope.SelectedIndex = 0;
            E2E.E2eWorkspace.Until(() => view.FindControl<TextBlock>("LanguageStatus")!.GetValue(ToolTip.TipProperty)?.ToString()?.Contains("Search scope: file", StringComparison.Ordinal) == true);
            scope.SelectedIndex = 2;
            E2E.E2eWorkspace.Until(() => view.FindControl<TextBlock>("LanguageStatus")!.GetValue(ToolTip.TipProperty)?.ToString()?.Contains("Search scope: dependencies", StringComparison.Ordinal) == true);
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Require(!popup.IsOpen, "Escape did not dismiss usages.");
            window.Width = 800;
            view.Editor.SelectRange(3, 0);
            view.Editor.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.B, KeyModifiers = SharpRail.Plugins.UI.Kit.CommandKeys.Modifier });
            E2E.E2eWorkspace.Until(() => navigated is not null);
            Require(navigated is { Path: "code.ts", Column: 3 }, "Go to Declaration did not reach the workbench callback.");
            Console.WriteLine("PASS code-document diagnostics, IDEA navigation shortcuts, grouped usages, filtering and narrow popup sizing");
            using var shared = new SharpRail.UI.State.SharedState(new LocalStateAdapter(state), new(), state.Current);
            shared.Start();
            var pane = new SharpRail.UI.Panels.LanguagePanel(new LocalProjectAdapter(new ProjectServices(root, state)), root,
                item => { navigated = item; return Task.CompletedTask; }, shared);
            window.Content = pane;
            T Find<T>(string name) where T : Avalonia.Controls.Control => Avalonia.Controls.ControlExtensions.FindControl<T>(pane, name)!;
            void Click(string name) => Find<Avalonia.Controls.Button>(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var servers = Find<Avalonia.Controls.ListBox>("Servers");
            string? RowStatus(int index) => Avalonia.Automation.AutomationProperties.GetName(((ListBoxItem)servers.ContainerFromIndex(index)!).GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "ServerStatusIcon"));
            E2E.E2eWorkspace.Until(() => servers.ItemCount == 1 && Find<ComboBox>("AddServer").ItemCount == LanguageServers.Catalog.Length - 1);
            var firstRow = (ListBoxItem)servers.ContainerFromIndex(0)!;
            var compactHeight = firstRow.Bounds.Height;
            servers.SelectedIndex = 0;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var selectedPresenter = firstRow.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Single(control => control.Name == "PART_ContentPresenter" && control.TemplatedParent == firstRow);
            Require(Equals(selectedPresenter.Background, Ui.Hover) && Equals(selectedPresenter.Foreground, Ui.TextBrush),
                "Selected LSP rows must retain neutral backgrounds and normal text colors.");
            CaptureLsp(window, "selected-server");
            Require(compactHeight < 130 && !firstRow.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerVersion").IsVisible,
                "Server rows must start compact.");
            window.MouseDown(firstRow.TranslatePoint(new Avalonia.Point(40, 12), window)!.Value, Avalonia.Input.MouseButton.Left);
            window.MouseUp(firstRow.TranslatePoint(new Avalonia.Point(40, 12), window)!.Value, Avalonia.Input.MouseButton.Left);
            E2E.E2eWorkspace.Until(() => (servers.ContainerFromIndex(0) as ListBoxItem)?.GetLogicalDescendants().OfType<TextBlock>().SingleOrDefault(text => text.Name == "ServerVersion")?.IsVisible == true);
            servers.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Space });
            E2E.E2eWorkspace.Until(() => (servers.ContainerFromIndex(0) as ListBoxItem)?.GetLogicalDescendants().OfType<TextBlock>().SingleOrDefault(text => text.Name == "ServerVersion")?.IsVisible == false);
            firstRow = (ListBoxItem)servers.ContainerFromIndex(0)!;
            var settingsAction = firstRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerSettingsAction");
            var serverTitle = firstRow.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerTitle");
            Require(Math.Abs(settingsAction.TranslatePoint(new Avalonia.Point(0, settingsAction.Bounds.Height / 2), firstRow)!.Value.Y -
                serverTitle.TranslatePoint(new Avalonia.Point(0, serverTitle.Bounds.Height / 2), firstRow)!.Value.Y) < 2,
                "Workspace settings action must align with the server header.");
            settingsAction.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var settingsWindow = window.OwnedWindows.OfType<SharpRail.UI.Panels.LanguageSettingsWindow>().Single();
            var configurationView = (SharpRail.UI.Panels.LanguageConfigurationView)settingsWindow.FindControl<ContentControl>("SettingsBody")!.Content!;
            T Config<T>(string name) where T : Control => configurationView.FindControl<T>(name)!;
            E2E.E2eWorkspace.Until(() => settingsWindow.IsVisible);
            Require(firstRow.Bounds.Height <= compactHeight + 2, "Settings popup expanded the server row.");
            E2E.E2eWorkspace.Until(() => Config<ItemsControl>("ProviderFields").ItemsSource is SharpRail.UI.Panels.LanguageOptionField[] { Length: > 0 });
            Require(Config<TextBlock>("ProviderSource").Text!.StartsWith("Source: npm", StringComparison.Ordinal), "Provider source label must be concise and preserve registry details.");
            Require(Config<ContentControl>("ProviderSourceIcon").Content is Control { Tag: "file-type:npm" }, "npm source must use its bundled Material icon.");
            Require(serverTitle.Text == SharpRail.UI.Panels.LanguageServerNameConverter.Display("typescript"), "Server title must omit inline runtime status.");
            var statusMark = firstRow.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "ServerStatusIcon");
            Require(!string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(statusMark)), "Icon-only server status must remain accessible.");
            var fields = (SharpRail.UI.Panels.LanguageOptionField[])Config<ItemsControl>("ProviderFields").ItemsSource!;
            var memory = fields.Single(field => field.Path == "maxTsServerMemory");
            memory.Value = "not a number";
            var beforeInvalid = state.Current.WorkspaceLanguages.GetValueOrDefault(root);
            Config<Button>("SaveConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(state.Current.WorkspaceLanguages.GetValueOrDefault(root) == beforeInvalid && Config<TextBlock>("ConfigurationMessage").Text!.Contains("Correct invalid", StringComparison.Ordinal), "Invalid named setting was silently saved as its previous value.");
            memory.Number = 4096;
            Require(configurationView.GetLogicalDescendants().OfType<NumericUpDown>().Any(input => input.IsVisible), "Integer settings must have a visible numeric control.");
            Require(JsonNode.Parse(Config<TextBox>("InitializationOptions").Text!)!["maxTsServerMemory"]!.GetValue<int>() == 4096, "Named setting did not write a typed integer.");
            Config<TextBox>("InitializationOptions").Text = "{\"fixture\":\"pane saved\"}";
            Config<Button>("SaveConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => Config<Button>("SaveConfiguration").IsEnabled && Config<TextBlock>("ConfigurationMessage").Text!.Contains("Restart", StringComparison.Ordinal));
            Require(state.Current.WorkspaceLanguages[root].Contains("pane saved", StringComparison.Ordinal), "Pane failed to persist workspace settings.");
            Require(Config<Expander>("ActiveConfigurationExpander").IsVisible, "Pane hid the active configuration snapshot.");
            Config<TextBox>("InitializationOptions").Text = "{\"fixture\":\"unsaved pane edit\"}";
            Click("Refresh");
            E2E.E2eWorkspace.Until(() => Find<Button>("Refresh").IsEnabled);
            Require(Config<TextBox>("InitializationOptions").Text!.Contains("unsaved pane edit", StringComparison.Ordinal), "Polling discarded unsaved configuration edits.");
            Config<Button>("ReloadConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => Config<TextBox>("InitializationOptions").Text!.Contains("pane saved", StringComparison.Ordinal));
            var beforeAgent = JsonNode.Parse(state.Current.WorkspaceLanguages[root])!["typescript"]!.ToJsonString();
            var agentSave = Task.Run(async () => await new LocalProjectAdapter(new ProjectServices(root, state)).LanguageAsync(new(root, Operation: "configure", Server: "typescript",
                Configuration: """{"arguments":["--agent-library-path"],"settings":{"library":"saved by agent"}}""")));
            E2E.E2eWorkspace.Until(() => agentSave.IsCompleted);
            agentSave.GetAwaiter().GetResult();
            E2E.E2eWorkspace.Until(() => Config<TextBox>("Arguments").Text == "--agent-library-path");
            Require(state.Current.WorkspaceLanguages[root].Contains("--agent-library-path", StringComparison.Ordinal), "Agent arguments were not persisted in workspace metadata.");
            var restore = Task.Run(async () => await new LocalProjectAdapter(new ProjectServices(root, state)).LanguageAsync(
                new(root, Operation: "configure", Server: "typescript", Configuration: beforeAgent)));
            E2E.E2eWorkspace.Until(() => restore.IsCompleted && Config<TextBox>("Arguments").Text != "--agent-library-path");
            restore.GetAwaiter().GetResult();
            Config<Expander>("AdvancedConfigurationExpander").IsExpanded = true;
            Config<Expander>("ActiveConfigurationExpander").IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); settingsWindow.UpdateLayout();
            var scopePosition = Config<TextBlock>("ConfigurationScope").TranslatePoint(default, settingsWindow)!.Value;
            Require(scopePosition.Y < 160, "Settings form must begin below the workspace heading, without a centered gap.");
            var savePosition = Config<Button>("SaveConfiguration").TranslatePoint(default, settingsWindow)!.Value;
            Require(savePosition.Y > settingsWindow.Bounds.Height - 100 && Config<Button>("CloseConfiguration").IsEffectivelyVisible,
                "Settings actions must remain visible below the scrollable form.");
            CaptureLsp(settingsWindow, "workspace-configuration");
            settingsWindow.Close();
            Find<ComboBox>("AddServer").SelectedItem = "python";
            E2E.E2eWorkspace.Until(() => servers.ItemCount == 2);
            Require(servers.Items[1]!.ToString() == "Python (Pyright)" && RowStatus(1) == "idle", "Adding a server should expose its row without installing it.");
            Require(pane.GetLogicalDescendants().OfType<Avalonia.Controls.ContentControl>().Any(control => control.Name == "ServerStatusIcon" && control.Content is Avalonia.Controls.Border { OpacityMask: not null }),
                "Server status icons were not rendered.");
            Require(servers.Bounds.Height < 260 &&
                Avalonia.Controls.ScrollViewer.GetVerticalScrollBarVisibility(servers) == Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                "Server list must expand inside the pane scroller.");
            Require(Find<Avalonia.Controls.TextBlock>("Detail") is null &&
                ((Avalonia.Controls.ListBoxItem)servers.ContainerFromIndex(0)!).GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>()
                    .Any(text => text.Text is "Suggested by file: code.ts" or "Suggested by file: z-other.ts"), "Server details must be inside their row.");
            Require(Find<Avalonia.Controls.Button>("Start") is null && Find<Avalonia.Controls.Button>("Stop") is null,
                "Server lifecycle buttons must live in the row context menu.");
            void ServerAction(int index, string action)
            {
                // The menu is built from the row's state at the time it opens, which a refresh may still be settling.
                Avalonia.Controls.ContextMenu menu = null!;
                Avalonia.Controls.MenuItem item = null!;
                E2E.E2eWorkspace.Until(() =>
                {
                    var row = (Avalonia.Controls.ListBoxItem)servers.ContainerFromIndex(index)!;
                    row.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
                    menu = row.ContextMenu!;
                    item = menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(entry => entry.Header?.ToString() == action);
                    if (item.IsEnabled) return true;
                    menu.Close();
                    return false;
                }, "Server context action was disabled: " + action);
                menu.Close();
                item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.MenuItem.ClickEvent));
            }
            servers.SelectedIndex = 1;
            ServerAction(0, "Stop");
            E2E.E2eWorkspace.Until(() => RowStatus(0) == "stopped");
            Require(RowStatus(1) == "idle", "Context menu targeted the previously selected server.");
            servers.SelectedIndex = 1;
            var stoppedRow = (ListBoxItem)servers.ContainerFromIndex(0)!;
            Require(stoppedRow.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Start ▷"),
                "Stopped servers must advertise the left-click start action.");
            var startPoint = stoppedRow.TranslatePoint(new Avalonia.Point(40, 12), window)!.Value;
            window.MouseDown(startPoint, Avalonia.Input.MouseButton.Left);
            window.MouseUp(startPoint, Avalonia.Input.MouseButton.Left);
            E2E.E2eWorkspace.Until(() => RowStatus(0) == "ready");
            Require(!pane.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "Inspect"), "LSP pane retained Workspace Problems.");
            Require(pane.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "LifetimeNote" && text.Text == SharpRail.UI.Panels.LanguageNotes.Lifetime && text.IsVisible),
                "The Code Intelligence pane does not tell the user that servers run for their workspace's lifetime and that agents start them.");
            E2E.E2eWorkspace.Until(() =>
            {
                if (Find<Button>("Refresh").IsEnabled) Click("Refresh"); return ((ListBoxItem)servers.ContainerFromIndex(0)!).GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ServerProvenance" && text.IsVisible &&
                text.Text!.StartsWith("started by human", StringComparison.Ordinal) && text.Text.Contains("Agent calls: none yet", StringComparison.Ordinal));
            });
            navigated = null;
            ((ListBoxItem)servers.ContainerFromIndex(0)!).GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerSourceAction").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => navigated is not null);
            Require(File.Exists(Path.Combine(root, navigated!.Path)) && navigated.Line == 0, "Suggested file link failed to navigate.");
            var pythonIndex = Enumerable.Range(0, servers.ItemCount).Single(index => servers.Items[index]!.ToString()!.StartsWith("Python (Pyright)", StringComparison.Ordinal));
            servers.SelectedIndex = pythonIndex;
            servers.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            E2E.E2eWorkspace.Until(() => { if (Find<Button>("Refresh").IsEnabled) Click("Refresh"); return RowStatus(pythonIndex) == "error"; });
            servers.SelectedIndex = 0;
            var failedRow = (Avalonia.Controls.ListBoxItem)servers.ContainerFromIndex(pythonIndex)!;
            Require(failedRow.GetLogicalDescendants().OfType<Avalonia.Controls.TextBlock>().Any(text => text.Name == "ServerDetail" &&
                text.Text!.Contains("missing-server", StringComparison.Ordinal) && text.TextWrapping == Avalonia.Media.TextWrapping.Wrap),
                "An unselected failed server must display its error inside its row.");
            Require(failedRow.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Retry ▷"),
                "Failed servers must advertise the left-click retry action.");
            ServerAction(pythonIndex, "Start / Retry");
            E2E.E2eWorkspace.Until(() => Find<Button>("Refresh").IsEnabled);
            window.Height = 640;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            failedRow.BringIntoView();
            var errorRow = (ListBoxItem)servers.ContainerFromIndex(1)!;
            var logsAction = errorRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerLogsAction");
            Require(logsAction.IsVisible, "Failed server row hid its logs action.");
            logsAction.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var logsWindow = window.OwnedWindows.OfType<SharpRail.UI.Panels.LanguageLogsWindow>().Single();
            E2E.E2eWorkspace.Until(() => logsWindow.FindControl<TextBox>("ServerOutput")!.Text?.Contains("startup:", StringComparison.Ordinal) == true);
            Require(logsWindow.Title == "python · LSP logs" && logsWindow.WindowDecorations == WindowDecorations.None &&
                logsWindow.FindControl<TextBlock>("LogHeading")!.Text == logsWindow.Title,
                "Log viewer must identify its server with an in-app heading and no native title bar.");
            CaptureLsp(logsWindow, "server-logs");
            logsWindow.Close();
            CaptureLsp(window, "pane");
            // A server that dies unexpectedly says why in its row, with copyable details, and a requested stop does not.
            var dying = new LanguageRequest(root, Operation: "restart", Server: "typescript", Configuration: """{"settings":{"fixtureDie":{"code":137,"stderr":3}}}""");
            void Lang(LanguageRequest request) => Task.Run(async () => await new LocalProjectAdapter(new ProjectServices(root, state)).LanguageAsync(request, new CancellationTokenSource(20000).Token)).GetAwaiter().GetResult();
            Lang(dying);
            E2E.E2eWorkspace.Until(() => { Lang(new(root, "code.ts", "hover", 0, 3)); if (Find<Button>("Refresh").IsEnabled) Click("Refresh"); return RowStatus(0) == "error"; });
            var deadRow = (ListBoxItem)servers.ContainerFromIndex(0)!;
            E2E.E2eWorkspace.Until(() => { if (Find<Button>("Refresh").IsEnabled) Click("Refresh"); return ((ListBoxItem)servers.ContainerFromIndex(0)!).GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "ServerDetail" && text.Text!.StartsWith("Stopped unexpectedly: Exited with code 137", StringComparison.Ordinal)); });
            deadRow = (ListBoxItem)servers.ContainerFromIndex(0)!;
            var exitDetails = deadRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerExitDetailsAction");
            Require(exitDetails.IsVisible && deadRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerExitCopyAction").IsVisible &&
                deadRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerLogsAction").IsVisible &&
                !deadRow.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.Name == "ServerExitText").IsVisible, "A dead server row lacks Details, Copy details or logs, or opens its details unasked.");
            exitDetails.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            SelectableTextBlock ExitText() => ((ListBoxItem)servers.ContainerFromIndex(0)!).GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.Name == "ServerExitText");
            E2E.E2eWorkspace.Until(() => { try { return ExitText().IsVisible; } catch (InvalidOperationException) { return false; } });
            Require(ExitText() is { IsVisible: true } shown &&
                shown.Text!.Contains("Exit code: 137", StringComparison.Ordinal) && shown.Text.Contains("fixture stderr", StringComparison.Ordinal), "Details did not show the exit code and stderr.");
            Lang(new(root, Operation: "restart", Server: "typescript", Configuration: "{}"));
            E2E.E2eWorkspace.Until(() => { if (Find<Button>("Refresh").IsEnabled) Click("Refresh"); return RowStatus(0) == "ready"; });
            Find<ComboBox>("AddServer").SelectedItem = "intellij";
            E2E.E2eWorkspace.Until(() => servers.Items.Cast<object>().Any(item => item.ToString()!.StartsWith("Java / Kotlin (IntelliJ)", StringComparison.Ordinal)));
            var intellijIndex = Enumerable.Range(0, servers.ItemCount).Single(index => servers.Items[index]!.ToString()!.StartsWith("Java / Kotlin (IntelliJ)", StringComparison.Ordinal));
            var intellijRow = (ListBoxItem)servers.ContainerFromIndex(intellijIndex)!;
            Require(intellijRow.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Start ▷"), "IntelliJ lacked an explicit start/review action.");
            intellijRow.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ServerSettingsAction").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var intellijWindow = window.OwnedWindows.OfType<SharpRail.UI.Panels.LanguageSettingsWindow>().Single();
            T IJ<T>(string name) where T : Control => intellijWindow.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            Require(IJ<StackPanel>("IntelliJOptions").IsVisible && IJ<Button>("RestartConfiguration").IsEnabled && IJ<CheckBox>("AutoStart").IsEnabled,
                "IntelliJ configuration hid its availability or enabled execution.");
            Require(!pane.GetLogicalDescendants().OfType<ComboBox>().Any(control => control.Name == "JvmProvider"), "Provider routing must live in Settings, not Code Intelligence.");
            var providerView = new SharpRail.UI.Panels.LanguageProviderView(new LocalProjectAdapter(new ProjectServices(root, state)), root);
            var providerWindow = new Window { Content = providerView };
            providerWindow.Show(window);
            T Provider<T>(string name) where T : Control => providerView.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            E2E.E2eWorkspace.Until(() => Provider<ComboBox>("JvmProvider").SelectedIndex >= 0);
            Provider<ComboBox>("JvmProvider").SelectedIndex = 1;
            Provider<Button>("ApplyJvmProvider").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => state.Current.Settings.JavaKotlinProvider == "intellij" && Provider<ComboBox>("JvmProvider").IsEnabled);
            IJ<ComboBox>("IntelliJBuildTool").SelectedItem = "gradle";
            IJ<TextBox>("IntelliJProjectJdk").Text = "/host/project-jdk";
            IJ<Button>("SaveConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => state.Current.WorkspaceLanguages[root].Contains("gradle", StringComparison.Ordinal) && IJ<Button>("SaveConfiguration").IsEnabled);
            Require(IJ<Button>("RestartConfiguration").IsEnabled && state.Current.Settings.JavaKotlinProvider == "intellij", "Saving lost the review/start action or provider selection.");
            var termsInstallation = IntelliJTermsChecks.Installation(Path.Combine(root, ".tools", "ui-terms"));
            IJ<TextBox>("IntelliJInstallation").Text = termsInstallation;
            IJ<Button>("SaveConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => IJ<Button>("SaveConfiguration").IsEnabled && state.Current.WorkspaceLanguages[root].Contains("ui-terms", StringComparison.Ordinal));
            IJ<Button>("RestartConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Any());
            var startupTerms = intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Single();
            E2E.E2eWorkspace.Until(() => startupTerms.FindControl<CheckBox>("TermsConsent")!.IsEnabled);
            Require(Equals(startupTerms.FindControl<Button>("AcceptTerms")!.Content, "Accept and start") && !startupTerms.FindControl<Button>("AcceptTerms")!.IsEnabled,
                "Starting IntelliJ did not visibly require unchecked EULA acknowledgement.");
            CaptureLsp(startupTerms, "intellij-start-eula");
            startupTerms.Close();
            void ReviewTerms()
            {
                IJ<Button>("ReviewIntelliJTerms").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                E2E.E2eWorkspace.Until(() => intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Any());
            }
            ReviewTerms();
            var termsWindow = intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Single();
            E2E.E2eWorkspace.Until(() => termsWindow.FindControl<TextBox>("TermsText")!.Text?.Contains("Synthetic", StringComparison.Ordinal) == true);
            Require(termsWindow.FindControl<CheckBox>("TermsConsent")!.IsChecked == false && !termsWindow.FindControl<Button>("AcceptTerms")!.IsEnabled, "EULA consent defaulted to accepted or enabled acceptance before acknowledgement.");
            termsWindow.Close();
            Require(!Directory.Exists(Path.Combine(statePath, "language", "agreements", "intellij")), "Cancelling terms review recorded acceptance.");
            ReviewTerms();
            termsWindow = intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Single();
            E2E.E2eWorkspace.Until(() => termsWindow.FindControl<CheckBox>("TermsConsent")!.IsEnabled);
            termsWindow.FindControl<CheckBox>("TermsConsent")!.IsChecked = true;
            Require(termsWindow.FindControl<Button>("AcceptTerms")!.IsEnabled, "Explicit EULA acknowledgement did not enable Accept.");
            File.AppendAllText(Path.Combine(termsInstallation, "EULA.txt"), "\nChanged while reviewing.");
            termsWindow.FindControl<Button>("AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => termsWindow.FindControl<TextBox>("TermsText")!.Text?.Contains("Changed while reviewing", StringComparison.Ordinal) == true);
            Require(termsWindow.FindControl<CheckBox>("TermsConsent")!.IsChecked == false && !termsWindow.FindControl<Button>("AcceptTerms")!.IsEnabled && !termsWindow.Result!.Accepted, "Changed terms were accepted without fresh acknowledgement.");
            termsWindow.FindControl<CheckBox>("TermsConsent")!.IsChecked = true;
            termsWindow.FindControl<Button>("AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => termsWindow.Result?.Accepted == true);
            Require(termsWindow.StartRequested && !termsWindow.IsVisible, "Accept and start did not close review and request startup.");
            E2E.E2eWorkspace.Until(() => IJ<Button>("RestartConfiguration").IsEnabled && IJ<TextBlock>("ConfigurationMessage").Text!.Length > 0);
            try { E2E.E2eWorkspace.Until(() => IJ<TextBlock>("ConfigurationMessage").Text!.Contains("intellij-server", StringComparison.OrdinalIgnoreCase)); }
            catch (Exception error) { throw new InvalidOperationException("Startup message: " + IJ<TextBlock>("ConfigurationMessage").Text, error); }
            Require(state.Current.Settings.JavaKotlinProvider == "intellij", "Setup did not persist IntelliJ provider selection.");
            ReviewTerms();
            termsWindow = intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Single();
            E2E.E2eWorkspace.Until(() => termsWindow.Result?.Accepted == true);
            Require(Equals(termsWindow.FindControl<Button>("AcceptTerms")!.Content, "Start IntelliJ") && termsWindow.FindControl<Button>("AcceptTerms")!.IsEnabled,
                "Already accepted terms left no direct start action.");
            Require(termsWindow.FindControl<CheckBox>("TermsConsent")!.IsChecked == true && !termsWindow.FindControl<CheckBox>("TermsConsent")!.IsEnabled,
                "Saved acceptance must be visibly checked and read-only, not look like an unchecked blocked consent control.");
            CaptureLsp(termsWindow, "intellij-eula");
            termsWindow.Close();
            Require(!termsWindow.StartRequested, "Closing accepted terms requested startup.");
            IJ<Button>("RestartConfiguration").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => IJ<Button>("RestartConfiguration").IsEnabled);
            Require(!intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Any(), "Acknowledged IntelliJ settings restart reopened consent.");
            ServerAction(intellijIndex, "Start / Retry");
            E2E.E2eWorkspace.Until(() => Find<Button>("Refresh").IsEnabled);
            Require(!window.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Any(), "Acknowledged IntelliJ row startup reopened consent.");
            ReviewTerms();
            termsWindow = intellijWindow.OwnedWindows.OfType<SharpRail.UI.Panels.IntelliJTermsWindow>().Single();
            E2E.E2eWorkspace.Until(() => termsWindow.Result?.Accepted == true);
            termsWindow.FindControl<Button>("AcceptTerms")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(termsWindow.StartRequested && !termsWindow.IsVisible, "Existing acceptance did not support explicit start.");
            try { E2E.E2eWorkspace.Until(() => IJ<Button>("RestartConfiguration").IsEnabled && IJ<TextBlock>("ConfigurationMessage").Text!.Contains("intellij-server", StringComparison.OrdinalIgnoreCase)); }
            catch (Exception error) { throw new InvalidOperationException("Repeated startup message: " + IJ<TextBlock>("ConfigurationMessage").Text, error); }
            Console.WriteLine("PASS IntelliJ setup saves selection, accepts and starts, reports launch failure, reuses acceptance and never starts on close");
            CaptureLsp(intellijWindow, "intellij-configuration");
            Provider<ComboBox>("JvmProvider").SelectedIndex = 0;
            Provider<Button>("ApplyJvmProvider").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => IJ<Button>("SaveConfiguration").IsEnabled && state.Current.Settings.JavaKotlinProvider == "standard");
            providerWindow.Close();
            intellijWindow.Close();
            Console.WriteLine("PASS IntelliJ workspace configuration window, explicit review/start actions, saved selection/JDK/importer and explicit opt-out");
            Console.WriteLine("PASS LSP tool pane inline errors, server listing, stop/restart and suggested-file navigation");
        }
        finally
        {
            window.Close(); view.Dispose();
            var stop = Task.Run(async () => await state.StopLanguagesAsync());
            E2E.E2eWorkspace.Until(() => stop.IsCompleted); stop.GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
            Directory.Delete(root, true);
        }
    }

    internal static async Task Fake(bool push = false)
    {
        await Console.Error.WriteLineAsync("fixture stderr");
        var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
        var documents = new Dictionary<string, (string Text, int Version)>();
        JsonObject? die = null;
        while (true)
        {
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { if (await input.ReadAsync(one) == 0) return; header.Append((char)one[0]); }
            var length = int.Parse(header.ToString().Split("\r\n")[0][15..]);
            var bytes = new byte[length]; await input.ReadExactlyAsync(bytes);
            var request = JsonNode.Parse(bytes)!; var method = request["method"]?.ToString(); var parameters = request["params"];
            if (method is null && request["id"]?.ToString() == "fixture-configuration")
            {
                var values = request["result"]!.AsArray();
                if (values.Count != 7 || values[0] is not null || values[1] is not null || values[2] is not null || values[3] is not JsonObject)
                {
                    await Console.Error.WriteLineAsync("Invalid workspace configuration: unset scalar settings must be null.");
                    Environment.Exit(8);
                }
                await File.WriteAllTextAsync(Path.Combine(".tools", "configuration-response.json"), values.ToJsonString());
                continue;
            }
            if (method is null) continue;
            var uri = parameters?["textDocument"]?["uri"]?.ToString() ?? "";
            if (method is "textDocument/didOpen" or "textDocument/didChange")
            {
                var text = method == "textDocument/didOpen" ? parameters!["textDocument"]!["text"]!.ToString() : parameters!["contentChanges"]![0]!["text"]!.ToString();
                documents[uri] = (text, parameters!["textDocument"]!["version"]!.GetValue<int>());
                if (push)
                {
                    int? version = text.Contains("unversioned", StringComparison.Ordinal) ? null : documents[uri].Version - (text.Contains("stale", StringComparison.Ordinal) ? 1 : 0);
                    await Send(new
                    {
                        jsonrpc = "2.0",
                        method = "textDocument/publishDiagnostics",
                        @params = new
                        {
                            uri,
                            version,
                            diagnostics = new[] { new { range = new { start = new { line = 0, character = 3 }, end = new { line = 0, character = 9 } }, message = "broken", severity = 1 } }
                        }
                    });
                }
                continue;
            }
            if (method == "workspace/didChangeConfiguration")
            {
                await File.WriteAllTextAsync(Path.Combine(".tools", "configuration.json"), parameters!.ToJsonString());
                die = parameters["settings"]?["fixtureDie"] as JsonObject;
                if (parameters["settings"]?["fixtureExit"]?.GetValue<bool>() == true)
                {
                    await Console.Error.WriteLineAsync("fixture exiting with code 7");
                    Environment.Exit(7);
                    return;
                }
            }
            if (method == "initialized")
                await Send(new { jsonrpc = "2.0", id = "fixture-format-warning", method = "window/showMessageRequest", @params = new { type = 2, message = "Formatting needs a dialect setting", actions = new[] { new { title = "Don't show again" } } } });
            if (method == "initialized" && File.Exists("Fixture.csproj"))
            {
                await Send(new { jsonrpc = "2.0", method = "$/progress", @params = new { token = "project-import", value = new { kind = "begin", title = "Loading projects" } } });
                await Task.Delay(500);
                await Send(new { jsonrpc = "2.0", id = "fixture-restore", method = "workspace/_roslyn_projectNeedsRestore", @params = new { projectFilePaths = new[] { Path.GetFullPath("Fixture.csproj") } } });
            }
            if (request["id"] is not { } id) continue;
            if (die is not null && method != "initialize")
            {
                // Dies (or hangs) on the first request after the configuration says so; the burst proves the stderr tail is bounded.
                for (var line = 0; line < (die["stderr"]?.GetValue<int>() ?? 0); line++) await Console.Error.WriteLineAsync($"fixture stderr line {line}");
                if (die["hang"]?.GetValue<bool>() == true) await Task.Delay(Timeout.Infinite);
                Environment.Exit(die["code"]?.GetValue<int>() ?? 1);
            }
            object? result;
            var range = new { start = new { line = 0, character = 3 }, end = new { line = 0, character = 9 } };
            if (method == "initialize")
            {
                await Send(new { jsonrpc = "2.0", method = "window/logMessage", @params = new { type = 3, message = "fixture server message" } });
                Directory.CreateDirectory(".tools");
                await Send(new { jsonrpc = "2.0", id = "fixture-configuration", method = "workspace/configuration", @params = new { items = new object[] { new { section = "fixture.unsetBoolean" }, new { section = "fixture.unsetEnum" }, new { section = "fixture.explicitNull" }, new { section = "" }, new { section = "fixture.enabled" }, new { section = "fixture.level" }, new { section = "fixture.mode" } } } });
                await File.WriteAllTextAsync(Path.Combine(".tools", "initialize.json"), JsonSerializer.Serialize(new { cwd = Environment.CurrentDirectory, root = parameters!["rootUri"]!.ToString(), pid = Environment.ProcessId, initializationOptions = parameters["initializationOptions"] }));
                result = new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16",
                        textDocumentSync = 2,
                        diagnosticProvider = push ? null : new { workspaceDiagnostics = false },
                        hoverProvider = true,
                        definitionProvider = true,
                        typeDefinitionProvider = true,
                        implementationProvider = true,
                        referencesProvider = true,
                        documentSymbolProvider = true,
                        workspaceSymbolProvider = true,
                        executeCommandProvider = new { commands = new[] { "decompile", "java.project.listSourcePaths" } },
                        semanticTokensProvider = new { full = true, legend = new { tokenTypes = new[] { "variable" }, tokenModifiers = new[] { "static" } } }
                    }
                };
            }
            else if (method == "workspace/executeCommand" && parameters?["command"]?.ToString() == "java.project.listSourcePaths")
                result = new { status = true, data = new[] { new { path = Path.GetFullPath("jvm/src/main/java") }, new { path = Path.GetFullPath("jvm/src/main/java") }, new { path = Path.GetTempPath() } } };
            else if (method == "workspace/executeCommand" && parameters?["command"]?.ToString() == "decompile")
            {
                var sourceUri = parameters["arguments"]![0]!.ToString();
                result = new { code = "// " + sourceUri + "\nclass Library {}\n" + new string('x', 13_000), language = "java" };
            }
            else if (method == "textDocument/definition" && uri.EndsWith("Sample.java", StringComparison.Ordinal))
                result = new[] { "jar:file:///library%20space.jar!/Library.class", "jrt:/java.base/java/lang/String.class", "unsupported:/Library" }.Select(target => new { uri = target, range }).ToArray();
            else if (method == "textDocument/references" && uri.EndsWith("Sample.java", StringComparison.Ordinal))
                result = new[] { uri, new Uri(Path.GetFullPath("Unopened.java")).AbsoluteUri, "jar:file:///library%20space.jar!/Library.class" }.Select(target => new { uri = target, range }).ToArray();
            else if (method == "textDocument/definition" && (uri.EndsWith("outline.ts", StringComparison.Ordinal) || uri.EndsWith("selected.ts", StringComparison.Ordinal)))
                result = new[] { new { uri, range = new { start = parameters!["position"], end = parameters["position"] } } };
            else if (method == "workspace/_roslyn_restore")
            {
                await File.WriteAllTextAsync(Path.Combine(".tools", "project-ready"), "restored");
                await Send(new { jsonrpc = "2.0", method = "workspace/projectInitializationComplete", @params = new { } });
                await Send(new { jsonrpc = "2.0", method = "$/progress", @params = new { token = "project-import", value = new { kind = "end" } } });
                result = Array.Empty<object>();
            }
            else if (method == "textDocument/diagnostic") result = new
            {
                kind = "full",
                items = documents[uri].Text.Contains("broken", StringComparison.Ordinal)
                ? new[] { new { range, message = "broken", severity = 1, source = "fixture" } } : []
            };
            else if (method == "textDocument/semanticTokens/full") result = new { data = new[] { 0, 3, 6, 0, 1 } };
            else if (method == "textDocument/hover") result = new { contents = new { kind = "markdown", value = "```typescript\nconst target: string\n```" }, range };
            else if (method == "textDocument/documentSymbol" && uri.EndsWith("outline.ts", StringComparison.Ordinal))
                result = new[] { new { name = "Suite", kind = 5, range = new { start = new { line = 0, character = 0 }, end = new { line = 4, character = 1 } }, children = new[] { new { name = "run", kind = 6, range = new { start = new { line = 1, character = 2 }, end = new { line = 3, character = 3 } }, selectionRange = new { start = new { line = 1, character = 2 }, end = new { line = 1, character = 5 } } } } } };
            else if (method == "textDocument/documentSymbol" && uri.EndsWith("selected.ts", StringComparison.Ordinal))
            {
                var line = documents[uri].Text.Split('\n').Select((text, index) => (text, index)).Single(item => item.text.Contains("target", StringComparison.Ordinal)).index;
                result = new[] { new { name = "target", kind = 13, range, selectionRange = new { start = new { line, character = 6 }, end = new { line, character = 12 } } } };
            }
            else if (method == "textDocument/documentSymbol" && uri.EndsWith("ambiguous.ts", StringComparison.Ordinal))
                result = new[] { new { name = "overload", kind = 6, range, selectionRange = range }, new { name = "overload", kind = 6, range, selectionRange = range } };
            else if (method == "textDocument/documentSymbol") result = new[] { new { name = "target", kind = 13, range } };
            else if (method == "workspace/symbol" && File.Exists("outline.ts") && parameters?["query"]?.ToString() is "Suite" or "run")
            {
                var outlineUri = new Uri(Path.GetFullPath("outline.ts")).AbsoluteUri;
                object At(string target, int line, int character) => new { uri = target, range = new { start = new { line, character }, end = new { line, character = character + 3 } } };
                result = parameters["query"]!.ToString() == "Suite"
                    ? new object[] { new { name = "Suite", kind = 5, location = At(outlineUri, 0, 6) }, new { name = "SuiteHelper", kind = 5, location = At(outlineUri, 4, 0) } }
                    : [new { name = "run", kind = 6, location = At(outlineUri, 1, 2) }, new { name = "run()", kind = 6, location = At(new Uri(Path.GetFullPath("runner.ts")).AbsoluteUri, 0, 9) }];
            }
            else if (method == "workspace/symbol") result = new[] { new { name = "target", kind = 13, location = new { uri = documents.Keys.First(), range } } };
            else if (method == "textDocument/typeDefinition") result = new[] { new { uri = new Uri(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "library.ts"))).AbsoluteUri, range } };
            else if (method == "textDocument/references")
            {
                await Task.Delay(250);
                result = uri.EndsWith("code.ts", StringComparison.Ordinal) && documents[uri].Text.Contains("// preview comment", StringComparison.Ordinal)
                    ? new[] { new { uri, range }, new { uri, range = new { start = new { line = 0, character = 10 }, end = new { line = 0, character = 12 } } }, new { uri = new Uri(Path.Combine(Environment.CurrentDirectory, "z-other.ts")).AbsoluteUri, range } }
                    : uri.EndsWith("many.ts", StringComparison.Ordinal)
                    ? Enumerable.Range(0, 250).Select(line => new { uri, range = new { start = new { line, character = 0 }, end = new { line, character = 5 } } }).ToArray()
                    : new[] { new { uri, range } };
            }
            else result = new[] { new { uri, range } };
            await Send(new { jsonrpc = "2.0", id, result });
        }
        async Task Send(object message)
        {
            var response = JsonSerializer.SerializeToUtf8Bytes(message);
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {response.Length}\r\n\r\n"));
            await output.WriteAsync(response); await output.FlushAsync();
        }
    }
}