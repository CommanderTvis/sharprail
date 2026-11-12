using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;

namespace SharpRail.Checks;

/// <summary>
/// Agents get no partial answers from a project graph that is still loading, see files created after the server started without a
/// restart, and get one answer for a type named together with its partial parts and constructors.
/// </summary>
internal static partial class LanguageFreshnessChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        GlobChecks();
        var root = Path.Combine(fixture, "language-freshness");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "other"));
        await File.WriteAllTextAsync(Path.Combine(workspace, FakeProject.Manifest), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(workspace, "Widget.cs"), "partial class Widget\n{\n    public Widget() { }\n}\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "Widget.Part.cs"), "partial class Widget\n{\n}\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "User.cs"), "class User\n{\n    Widget widget = new Widget();\n}\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "Gadget.cs"), "class Gadget\n{\n}\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "other", "Gadget.cs"), "class Gadget\n{\n}\n");

        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE");
        Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var (grace, reload, debounce) = (LanguageSession.SettleGrace, LanguageSession.ReloadWait, LanguageSession.WatchDebounce);
        (LanguageSession.SettleGrace, LanguageSession.ReloadWait, LanguageSession.WatchDebounce) = (TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(100));
        var state = new HostStateStore(Path.Combine(root, "state"));
        try
        {
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
            await state.ChangeAsync([HostStateChange.Setting("language-server:csharp", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            state.Languages.OnDemandWait = TimeSpan.FromSeconds(3);
            var tools = LanguageMcpTools.Create(state, workspace);
            var next = 0;
            async Task<JsonNode> Call(string tool, object call)
            {
                var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++next, method = "tools/call", @params = new { name = tool, arguments = call } });
                var (_, body) = await McpServer.HandleAsync(JsonNode.Parse(message), tools, default);
                return JsonNode.Parse(body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
            }

            // The graph is loaded but a restore is still running: references wait (bounded) and say so instead of answering from part of the graph.
            var watch = Stopwatch.StartNew();
            var early = await Call("lsp_navigate", new { operation = "references", query = "Widget", workspace });
            Require(early["state"]!.ToString() == "initializing" && early["locations"] is null or JsonArray { Count: 0 } && early["detail"]!.ToString().Contains("still loading", StringComparison.Ordinal),
                "References answered from a project graph that was still loading: " + early);
            Require(watch.Elapsed >= TimeSpan.FromSeconds(2), "References did not wait for the project graph before giving up: " + watch.Elapsed);
            var diagnosed = await Call("lsp_diagnostics", new { paths = new[] { "User.cs" }, workspace });
            Require(diagnosed["state"]!.ToString() == "initializing" && diagnosed["files"]![0]!["state"]!.ToString() == "pending" && diagnosed["diagnostics"] is null or JsonArray { Count: 0 },
                "A file checked while the graph loaded was reported checked or with diagnostics: " + diagnosed);
            // An outline is syntax: it answers at once.
            var outline = await Call("lsp_navigate", new { operation = "symbols", path = "Widget.cs", workspace });
            Require(outline["state"]!.ToString() == "ready", "An outline waited for the project graph: " + outline);

            // A member body is found by the name the outline lists, simple or with its type, not only by the full qualified name.
            foreach (var member in new[] { "Widget/Widget", "Widget()" })
            {
                var body = await Call("lsp_navigate", new { operation = "symbolBody", path = "Widget.cs", query = member, workspace });
                Require(body["state"]!.ToString() == "ready" && body["locations"]!.AsArray().Count == 1 && body["locations"]![0]!["body"]!.ToString().Contains("public Widget()", StringComparison.Ordinal),
                    $"symbolBody did not find {member}, which symbols lists: " + body);
            }

            await File.WriteAllTextAsync(Path.Combine(workspace, ".tools", FakeProject.FinishRestore), "");
            var loaded = await Until(() => Call("lsp_navigate", new { operation = "references", query = "Widget", workspace }));
            Require(loaded["state"]!.ToString() == "ready" && loaded["locations"]!.AsArray().Any(item => item!["path"]!.ToString() == "User.cs"),
                "A type named with its partial parts and constructor was not resolved to the type: " + loaded);
            var ambiguous = await Call("lsp_navigate", new { operation = "references", query = "Gadget", workspace });
            Require(ambiguous["state"]!.ToString() == "ambiguous" && ambiguous["locations"]!.AsArray().Count == 2, "Two types in different projects were not reported as ambiguous: " + ambiguous);

            // A type created after the server started is found and diagnosed without a restart.
            var pid = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, ".tools", "initialize.json")))!["pid"]!.GetValue<int>();
            await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), "Not watched by the server.\n");
            await File.WriteAllTextAsync(Path.Combine(workspace, "Fresh.cs"), "class Fresh\n{\n}\n");
            await File.WriteAllTextAsync(Path.Combine(workspace, "Consumer.cs"), "class Consumer\n{\n    Fresh fresh = new Fresh();\n}\n");
            // The host's watcher sees a file a moment after it is written, so the call repeats until the server was told.
            var fresh = await Until(() => Call("lsp_navigate", new { operation = "references", query = "Fresh", workspace }), result => result["state"]!.ToString() == "ready");
            Require(fresh["state"]!.ToString() == "ready" && fresh["locations"]!.AsArray().Any(item => item!["path"]!.ToString() == "Consumer.cs"), "A new type was not found by references: " + fresh);
            var clean = await Call("lsp_diagnostics", new { paths = new[] { "Consumer.cs" }, workspace });
            Require(clean["files"]![0]!["state"]!.ToString() == "checked" && clean["diagnostics"] is null or JsonArray { Count: 0 }, "A new type was reported missing: " + clean);
            var watched = await File.ReadAllTextAsync(Path.Combine(workspace, ".tools", FakeProject.WatchedLog));
            Require(watched.Contains("1 " + new Uri(Path.Combine(workspace, "Fresh.cs")).AbsoluteUri, StringComparison.Ordinal), "The new file was not reported as created: " + watched);
            Require(!watched.Contains("README", StringComparison.Ordinal), "A file no watcher covers was reported: " + watched);
            Require(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, ".tools", "initialize.json")))!["pid"]!.GetValue<int>() == pid, "The server was restarted to see a new file.");
            Console.WriteLine("PASS language freshness: loading graph waits, new files without restart, type and constructor resolution");
        }
        finally
        {
            await state.StopLanguagesAsync();
            (LanguageSession.SettleGrace, LanguageSession.ReloadWait, LanguageSession.WatchDebounce) = (grace, reload, debounce);
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
    }

    private static async Task<JsonNode> Until(Func<Task<JsonNode>> call, Func<JsonNode, bool>? done = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var result = await call();
            if ((done?.Invoke(result) ?? result["state"]!.ToString() != "initializing") || DateTime.UtcNow > deadline) return result;
            await Task.Delay(100);
        }
    }

    private static void GlobChecks()
    {
        var sources = LanguageSession.Glob("**/*{.cs,.cshtml,.razor}");
        Require(sources.IsMatch("a/b/C.cs") && sources.IsMatch("C.razor") && !sources.IsMatch("C.csx") && !sources.IsMatch("a/C.txt"), "A source glob matched the wrong files.");
        Require(LanguageSession.Glob("project.assets.json").IsMatch("project.assets.json") && !LanguageSession.Glob("project.assets.json").IsMatch("x/project.assets.json"), "A file-name glob matched below its base.");
        Require(LanguageSession.Glob("lib/*.dll").IsMatch("lib/a.dll") && !LanguageSession.Glob("lib/*.dll").IsMatch("lib/x/a.dll") && LanguageSession.Glob("[!x]?.cs").IsMatch("ab.cs") && !LanguageSession.Glob("[!x]?.cs").IsMatch("xb.cs"),
            "Single-segment wildcards or character classes matched wrongly.");
    }

    /// <summary>
    /// The fixture server's project mode (a <see cref="Manifest"/> in its directory): it registers watchers like Roslyn, keeps a restore running
    /// until the check writes <see cref="FinishRestore"/>, knows only the C# files it saw at start or was told about, and logs a reload end.
    /// </summary>
    internal sealed partial class FakeProject(Func<object, Task> send)
    {
        internal const string Manifest = "Freshness.csproj";
        internal const string FinishRestore = "finish-restore";
        internal const string WatchedLog = "watched.log";
        private readonly HashSet<string> known = new(StringComparer.Ordinal);
        // The workspace as the client names it, which may differ from the process directory by a symlink such as /var and /private/var.
        private string root = Environment.CurrentDirectory;

        internal async Task<bool> HandleAsync(string method, JsonNode? id, JsonNode? parameters)
        {
            if (method == "initialize")
            {
                root = Path.TrimEndingDirectorySeparator(new Uri(parameters!["rootUri"]!.ToString()).LocalPath);
                known.UnionWith(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories));
                return false;
            }
            var directory = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
            if (method == "initialized")
            {
                Directory.CreateDirectory(".tools");
                await send(new
                {
                    jsonrpc = "2.0",
                    id = "fixture-watch",
                    method = "client/registerCapability",
                    @params = new
                    {
                        registrations = new object[]
                {
                    new { id = "sources", method = "workspace/didChangeWatchedFiles", registerOptions = new { watchers = new[] { new { globPattern = new { baseUri = directory, pattern = "**/*{.cs,.cshtml,.razor}" } } } } },
                    new { id = "project", method = "workspace/didChangeWatchedFiles", registerOptions = new { watchers = new[] { new { globPattern = new { baseUri = directory, pattern = Manifest } } } } }
                }
                    }
                });
                await send(new { jsonrpc = "2.0", method = "workspace/projectInitializationComplete", @params = new { } });
                await send(new { jsonrpc = "2.0", method = "$/progress", @params = new { token = "restore", value = new { kind = "begin", title = "Restore" } } });
                _ = Task.Run(async () =>
                {
                    while (!File.Exists(Path.Combine(".tools", FinishRestore))) await Task.Delay(50);
                    await send(new { jsonrpc = "2.0", method = "$/progress", @params = new { token = "restore", value = new { kind = "end" } } });
                });
                return false;
            }
            if (method == "workspace/didChangeWatchedFiles")
            {
                foreach (var change in parameters?["changes"] as JsonArray ?? [])
                {
                    var path = new Uri(change!["uri"]!.ToString()).LocalPath; var type = change["type"]!.GetValue<int>();
                    await File.AppendAllTextAsync(Path.Combine(".tools", WatchedLog), $"{type} {change["uri"]}\n");
                    if (type == 1) known.Add(path);
                    if (type == 3) known.Remove(path);
                }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300);
                    await send(new { jsonrpc = "2.0", method = "window/logMessage", @params = new { type = 3, message = "[LanguageServerProjectSystem] Completed (re)load of 1 project(s) in 00:00:00.3" } });
                });
                return true;
            }
            if (id is null) return false;
            object? result = method switch
            {
                "workspace/symbol" => Declarations().Where(item => item.Name == parameters?["query"]?.ToString()).Select(item => item.Symbol).ToArray(),
                "textDocument/definition" => Declarations().Where(item => item.Kind == 5 && item.Name == NameAt(parameters)).Select(item => item.Location).ToArray(),
                "textDocument/references" => Occurrences(NameAt(parameters)).ToArray(),
                "textDocument/documentSymbol" when parameters!["textDocument"]!["uri"]!.ToString().EndsWith("/Widget.cs", StringComparison.Ordinal) => new[]
                {
                    new { name = "Widget", kind = 5, range = new { start = new { line = 0, character = 0 }, end = new { line = 3, character = 1 } }, selectionRange = Range(0, 14, 6),
                        children = new[] { new { name = "Widget()", kind = 9, range = new { start = new { line = 2, character = 4 }, end = new { line = 2, character = 23 } }, selectionRange = Range(2, 11, 6) } } }
                },
                "textDocument/diagnostic" => new { kind = "full", items = Missing(new Uri(parameters!["textDocument"]!["uri"]!.ToString()).LocalPath).ToArray() },
                _ => null
            };
            if (result is null) return false;
            await send(new { jsonrpc = "2.0", id = id.DeepClone(), result });
            return true;
        }

        private static object Range(int line, int column, int length) => new { start = new { line, character = column }, end = new { line, character = column + length } };

        private IEnumerable<(string Name, int Kind, object Symbol, object Location)> Declarations()
        {
            foreach (var file in known.Where(File.Exists).Order(StringComparer.Ordinal))
            {
                var lines = File.ReadAllLines(file);
                var project = Path.GetDirectoryName(file) == root ? "Freshness (net10.0)" : "Other (net10.0)";
                for (var line = 0; line < lines.Length; line++)
                {
                    var type = TypeDeclaration().Match(lines[line]);
                    var constructor = ConstructorDeclaration().Match(lines[line]);
                    var match = type.Success ? type : constructor;
                    if (!match.Success) continue;
                    var name = match.Groups[1].Value;
                    var location = new { uri = new Uri(file).AbsoluteUri, range = Range(line, match.Groups[1].Index, name.Length) };
                    var container = type.Success ? $"{Path.GetFileName(file)} - {project}" : $"in {name} (project {project})";
                    yield return (name, type.Success ? 5 : 6, new { name, kind = type.Success ? 5 : 6, containerName = container, location }, location);
                }
            }
        }

        private IEnumerable<object> Occurrences(string name)
        {
            foreach (var file in known.Where(File.Exists).Order(StringComparer.Ordinal))
            {
                var lines = File.ReadAllLines(file);
                for (var line = 0; line < lines.Length; line++)
                    foreach (Match word in Regex.Matches(lines[line], $@"\b{Regex.Escape(name)}\b"))
                        yield return new { uri = new Uri(file).AbsoluteUri, range = Range(line, word.Index, name.Length) };
            }
        }

        private IEnumerable<object> Missing(string file)
        {
            var declared = Declarations().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            var lines = File.Exists(file) ? File.ReadAllLines(file) : [];
            for (var line = 0; line < lines.Length; line++)
                foreach (Match created in Regex.Matches(lines[line], @"new (\w+)\("))
                    if (!declared.Contains(created.Groups[1].Value))
                        yield return new { range = Range(line, created.Groups[1].Index, created.Groups[1].Length), message = $"The type or namespace name '{created.Groups[1].Value}' could not be found", severity = 1, source = "fixture" };
        }

        private static string NameAt(JsonNode? parameters)
        {
            var path = new Uri(parameters!["textDocument"]!["uri"]!.ToString()).LocalPath;
            var line = File.ReadAllLines(path)[parameters["position"]!["line"]!.GetValue<int>()];
            var column = parameters["position"]!["character"]!.GetValue<int>();
            return Regex.Matches(line, @"\w+").FirstOrDefault(word => word.Index <= column && column <= word.Index + word.Length)?.Value ?? "";
        }

        [GeneratedRegex(@"\bclass (\w+)")]
        private static partial Regex TypeDeclaration();

        [GeneratedRegex(@"public (\w+)\(")]
        private static partial Regex ConstructorDeclaration();
    }
}