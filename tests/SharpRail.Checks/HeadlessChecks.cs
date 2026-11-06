using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>
/// The headless host as a real child process: no window is involved. Stdout carries JSON-RPC lines only, stderr the logs,
/// end of input and SIGTERM exit 0, a configuration error exits 2, and the project reaches no UI package.
/// </summary>
internal static partial class HeadlessChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    [GeneratedRegex(@"Avalonia|SkiaSharp|HarfBuzz|SharpRail\.UI|\.UI/|Ghostty|Scintilla|Plugins\.UI\.Kit|Host\.Remote|Host\.Client|Microsoft\.AspNetCore", RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    internal static async Task Run(string fixture)
    {
        Dependencies();
        var root = Path.Combine(fixture, "headless");
        var workspace = Path.Combine(root, "workspace");
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(Path.Combine(state, "language"));
        Directory.CreateDirectory(workspace);
        await GitRepository.RunAsync(workspace, default, "init");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), ".tools/\n");
        await File.WriteAllTextAsync(Path.Combine(workspace, "code.ts"), "😀 target");
        await File.WriteAllTextAsync(Path.Combine(workspace, "SPEC.md"), "---\nid: goal\ntitle: Project goal\ntype: product-goal\n---\n# Goal\n");
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(HeadlessChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        await File.WriteAllTextAsync(Path.Combine(state, "language", "servers.json"), JsonSerializer.Serialize(new { typescript = new { command = executable, arguments } }));

        await Conversation(workspace, state);
        await Terminate(workspace, state);
        await Configuration(root, workspace, state);
        Console.WriteLine("PASS headless host: JSON-RPC lines only on stdout, logs on stderr, EOF and SIGTERM exit 0, bad configuration exits 2, no UI reference");
    }

    /// <summary>The restore graph, transitive packages and projects included, holds no UI, Skia or web server package.</summary>
    private static void Dependencies()
    {
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP headless dependency graph: no source checkout"); return; }
        var assets = Path.Combine(checkout, "src", "SharpRail.Host.Headless", "obj", "project.assets.json");
        Require(File.Exists(assets), "Restore the headless project before checking its dependencies.");
        var libraries = JsonNode.Parse(File.ReadAllText(assets))!["libraries"]!.AsObject().Select(pair => pair.Key).ToArray();
        Require(libraries.Any(name => name.StartsWith("SharpRail.Host.Core/", StringComparison.Ordinal)) && libraries.Length > 20, "The headless dependency graph is empty or lost the host core.");
        var offending = libraries.Where(name => Forbidden().IsMatch(name)).ToArray();
        Require(offending.Length == 0, "The headless host reaches UI or web server packages: " + string.Join(", ", offending));
    }

    private static async Task Conversation(string workspace, string state)
    {
        await using var host = Host.Start(["mcp", "--workspace", workspace], state);
        var big = new string('x', 2_000_000);
        host.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}""");
        host.Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        host.Send("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        host.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");
        host.Send("""{"jsonrpc":"2.0","id":4,"method":"no/such"}""");
        host.Send("this is not json");
        host.Send("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":999}}""");
        host.Send("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\",\"params\":{\"pad\":\"" + big + "\"}}");
        host.Send("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"lsp_status","arguments":{"workspace":"/somewhere/else"}}}""");
        host.Send("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"spec_validate","arguments":{}}}""");
        host.Send("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"lsp_diagnostics","arguments":{"paths":["code.ts"]}}}""");
        host.Send("""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"set_title","arguments":{"title":"x"}}}""");
        // Overlapping calls: every answer arrives once, in any order.
        for (var id = 20; id < 36; id++)
            host.Send(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = id % 2 == 0 ? "spec_get" : "lsp_status", ["arguments"] = id % 2 == 0 ? new JsonObject { ["id"] = "goal" } : new JsonObject() }
            }.ToJsonString());
        var ids = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }.Concat(Enumerable.Range(20, 16)).ToArray();
        var replies = await host.ReplyAsync(ids, TimeSpan.FromSeconds(90));
        var parseError = host.Lines.Select(line => JsonNode.Parse(line)!).Single(node => node["error"]?["code"]?.GetValue<int>() == -32700);
        Require(parseError["id"] is null, "A parse error must answer with a null id.");

        var initialize = replies[1]["result"]!;
        var instructions = initialize["instructions"]!.GetValue<string>();
        Require(initialize["protocolVersion"]!.GetValue<string>() == "2025-06-18" && !instructions.Contains("set_title", StringComparison.Ordinal) && !instructions.Contains("workspace_delete", StringComparison.Ordinal),
            "The headless initialize text mentions terminal-only tools.");
        Require(replies[2]["result"] is JsonObject { Count: 0 }, "ping did not answer an empty result.");
        Require(replies[4]["error"]?["code"]?.GetValue<int>() == -32601, "An unknown method must answer -32601.");
        Require(replies[5]["result"] is JsonObject, "A 2 MB request line was not answered.");

        var tools = replies[3]["result"]!["tools"]!.AsArray().ToDictionary(tool => tool!["name"]!.GetValue<string>(), tool => tool!);
        string[] required = ["lsp_diagnostics", "lsp_navigate", "lsp_status", "spec_grep", "spec_get", "spec_graph", "spec_create", "spec_update", "spec_delete", "spec_validate", "blueprint_check"];
        Require(required.All(tools.ContainsKey) && tools.Keys.All(name => name.StartsWith("lsp_", StringComparison.Ordinal) || name.StartsWith("spec_", StringComparison.Ordinal) || name == "blueprint_check"),
            "The headless tool table is not exactly the language, spec and Blueprint tools: " + string.Join(", ", tools.Keys));
        var desktop = LanguageMcpTools.Create(new HostStateStore(Path.Combine(Path.GetDirectoryName(state)!, "desktop-state")), workspace);
        Require(desktop.Count == 3 && desktop.All(tool => tools[tool.Name]["inputSchema"]!.ToJsonString() == tool.InputSchema.ToJsonString()), "A language tool's schema differs from the desktop's.");
        Require(replies[9]["error"]?["code"]?.GetValue<int>() == -32602, "A terminal-only tool must not exist headless.");

        string Text(int id) => replies[id]["result"]!["content"]![0]!["text"]!.GetValue<string>();
        bool Failed(int id) => replies[id]["result"]!["isError"]?.GetValue<bool>() ?? false;
        Require(!Failed(6) && Text(6).Contains("\"state\"", StringComparison.Ordinal), "lsp_status failed or did not ignore a foreign workspace argument: " + Text(6));
        Require(!Failed(7) && Text(7).Contains("valid", StringComparison.Ordinal), "spec_validate failed: " + Text(7));
        Require(!Failed(8) && Text(8).Contains("code.ts", StringComparison.Ordinal), "lsp_diagnostics did not report the fixture file through the fake server: " + Text(8));
        for (var id = 20; id < 36; id++)
            Require(!Failed(id) && (id % 2 == 0 ? Text(id).Contains("Project goal", StringComparison.Ordinal) : Text(id).Contains("\"state\"", StringComparison.Ordinal)), $"Overlapping call {id} was lost or failed: {Text(id)}");

        host.CloseInput();
        var exit = await host.ExitAsync(TimeSpan.FromSeconds(30));
        Require(exit == 0, $"End of input must exit 0, got {exit}. stderr:\n{host.Errors}");
        Require(host.Lines.Count == ids.Length + 1, $"Expected {ids.Length + 1} output lines, got {host.Lines.Count}.");
        foreach (var line in host.Lines)
        {
            var node = JsonNode.Parse(line);
            Require(node is JsonObject frame && frame["jsonrpc"]?.GetValue<string>() == "2.0" && (frame.ContainsKey("result") || frame.ContainsKey("error")), "stdout carried something besides a JSON-RPC response: " + line[..Math.Min(line.Length, 200)]);
        }
        Require(host.Errors.Contains("sharprail-headless: serving", StringComparison.Ordinal) && host.Errors.Contains("sharprail-headless: stopping", StringComparison.Ordinal), "stderr lost the host's log lines.");
    }

    private static async Task Terminate(string workspace, string state)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var host = Host.Start(["mcp", "--workspace", workspace], state);
        host.Send("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        await host.ReplyAsync([1], TimeSpan.FromSeconds(60));
        using (var kill = Process.Start("kill", ["-TERM", host.Id.ToString()])!) await kill.WaitForExitAsync();
        var exit = await host.ExitAsync(TimeSpan.FromSeconds(30));
        Require(exit == 0, $"SIGTERM must exit 0, got {exit}. stderr:\n{host.Errors}");
    }

    private static async Task Configuration(string root, string workspace, string state)
    {
        foreach (var arguments in new[] { new[] { "mcp", "--workspace", Path.Combine(root, "missing") }, ["mcp", "--workspace", Path.Combine(workspace, "code.ts")], ["frobnicate"], ["mcp", "--nope"] })
        {
            await using var host = Host.Start(arguments, state);
            var exit = await host.ExitAsync(TimeSpan.FromSeconds(30));
            Require(exit == 2 && host.Lines.Count == 0 && host.Errors.Contains("sharprail-headless:", StringComparison.Ordinal), $"`{string.Join(' ', arguments)}` must exit 2 with a message on stderr only, got {exit}, stdout {host.Lines.Count} lines, stderr: {host.Errors}");
        }
        var file = Path.Combine(root, "state-is-a-file");
        await File.WriteAllTextAsync(file, "x");
        await using var blocked = Host.Start(["mcp", "--workspace", workspace], file);
        var blockedExit = await blocked.ExitAsync(TimeSpan.FromSeconds(30));
        Require(blockedExit == 2 && blocked.Lines.Count == 0, $"An unusable state directory must exit 2, got {blockedExit}. stderr: {blocked.Errors}");
    }

    /// <summary>One child process of the headless executable (or its dll under dotnet) with its three streams captured.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly Process process;
        private readonly object gate = new();
        private readonly List<string> lines = [];
        private readonly StringBuilder errors = new();
        private readonly Task pumps;

        private Host(Process process)
        {
            this.process = process;
            var output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line) lock (gate) lines.Add(line);
            });
            var error = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line) lock (gate) errors.AppendLine(line);
            });
            pumps = Task.WhenAll(output, error);
        }

        internal static Host Start(string[] arguments, string state)
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "sharprail-headless.exe" : "sharprail-headless");
            var dll = Path.Combine(AppContext.BaseDirectory, "sharprail-headless.dll");
            Require(File.Exists(dll), "The checks output has no headless host: " + dll);
            var start = new ProcessStartInfo(File.Exists(apphost) ? apphost : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!File.Exists(apphost)) start.ArgumentList.Add(dll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            // The checks switch language services off for themselves; the host under test must run them.
            start.Environment.Remove("SHARPRAIL_LSP_DISABLE");
            start.Environment.Remove("SHARPRAIL_ROOT");
            start.Environment["SHARPRAIL_STATE_DIR"] = state;
            return new(Process.Start(start)!);
        }

        internal int Id => process.Id;
        internal string Errors { get { lock (gate) return errors.ToString(); } }
        internal IReadOnlyList<string> Lines { get { lock (gate) return [.. lines]; } }
        internal void Send(string line) { process.StandardInput.Write(line + "\n"); process.StandardInput.Flush(); }
        internal void CloseInput() => process.StandardInput.Close();

        /// <summary>Waits until every id has a response and returns them; a response with an unexpected id fails.</summary>
        internal async Task<Dictionary<int, JsonNode>> ReplyAsync(int[] ids, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var found = Lines.Select(line => JsonNode.Parse(line)!).Where(node => node["id"] is not null).ToDictionary(node => node["id"]!.GetValue<int>());
                if (ids.All(found.ContainsKey)) return found;
                if (process.HasExited && DateTime.UtcNow > deadline - timeout + TimeSpan.FromSeconds(5)) throw new InvalidOperationException("The headless host exited early. stderr:\n" + Errors);
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"No answer for ids {string.Join(',', ids.Where(id => !found.ContainsKey(id)))}. stderr:\n{Errors}");
                await Task.Delay(50);
            }
        }

        internal async Task<int> ExitAsync(TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancel.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException("The headless host did not exit. stderr:\n" + Errors); }
            await pumps;
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }
}