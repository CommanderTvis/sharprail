using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Tereditor.Plugins.AirContext;
using Tereditor.Plugins.AirContext.Host;

namespace Tereditor.Checks;

internal static partial class AirContextChecks
{
    private static async Task NativeWorkflowAsync(string root)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Context native-command fixtures: the pinned integration has no Windows artifact."); return; }
        var workspace = Path.Combine(root, "native-workflow"); Directory.CreateDirectory(workspace);
        // The fake jbcontext is this checks binary itself (FakeJbContext below) behind a shell launcher, so the check needs no JavaScript runtime.
        var executable = Path.Combine(workspace, "context-fixture");
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var process = Environment.ProcessPath!;
        var command = Quote(process) + (Path.GetFileNameWithoutExtension(process) == "dotnet" ? " " + Quote(typeof(AirContextChecks).Assembly.Location) : "");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec " + command + " " + FakeJbContextArgument + " \"$@\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var logs = new ConcurrentQueue<string>(); var authorizations = 0;
        await using var runtime = new ContextRuntime(executable, workspace, logs.Enqueue, token => { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref authorizations); return Task.CompletedTask; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await runtime.IndexAsync(deadline.Token);
        Require((await runtime.StatusAsync(deadline.Token)).Contains("indexed", StringComparison.Ordinal), "Context status command was not usable.");
        Require((await runtime.PrepareLoginAsync(deadline.Token)).EndsWith("' login", StringComparison.Ordinal), "Sign-in command did not preserve the pinned executable.");
        var data = JsonNode.Parse(await runtime.SearchAsync("locate behavior", "src", 7, deadline.Token))!;
        Require(data["text"]!.ToString() == "locate behavior" && data["pathFilter"]!.ToString() == "src" && data["limit"]!.GetValue<int>() == 7 &&
            data["root"]!.AsArray().Count == 1 && data["root"]![0]!["uri"]!.ToString() == new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri,
            "Native MCP schema adaptation or single-root initialization failed.");
        await runtime.SearchAsync("same warm session", ".", 2, deadline.Token);
        Require(authorizations == 4, "A warm search unnecessarily reprovisioned/restarted the server or a fresh execution skipped authorization.");
        Require((await runtime.SearchAsync("large", ".", 2, deadline.Token)).Length < 31_000, "Context result output was not bounded.");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await runtime.SearchAsync("cancel", ".", 2, cancelled.Token); throw new InvalidOperationException("Context MCP ignored cancellation."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        await runtime.SearchAsync("reconnect", ".", 2, deadline.Token);
        Require(authorizations == 5, "MCP cancellation failed to restart only its owned connection through the terms guard.");
        try { await runtime.SearchAsync("needs-auth", ".", 2, deadline.Token); throw new InvalidOperationException("A warm MCP authentication failure appeared successful."); }
        catch (ContextLoginRequiredException) { }
        await File.WriteAllTextAsync(Path.Combine(workspace, "hold-index"), "");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            try { await runtime.IndexAsync(cancelled.Token); throw new InvalidOperationException("Context index ignored cancellation."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        File.Delete(Path.Combine(workspace, "hold-index"));
        await Task.Delay(50, deadline.Token);
        Require(logs.Any(line => line.Contains("progress", StringComparison.Ordinal)) && logs.All(line => !line.Contains("fixture-native-private", StringComparison.Ordinal) && !line.Contains("fixture-mcp-private", StringComparison.Ordinal)),
            "Native Context progress was lost or credentials entered logs.");
        var calls = (await File.ReadAllLinesAsync(Path.Combine(workspace, "invocations.jsonl"))).Select(line => JsonNode.Parse(line)!.AsArray()).ToArray();
        Require(calls.All(call => call[0]!.ToString() is "index" or "status" or "mcp") && calls.Where(call => call[0]!.ToString() != "mcp").All(call => call.Any(arg => arg?.ToString() == "--project-path=" + workspace)),
            "Context changed global setup/watch state or omitted its fixed workspace argument.");
        File.Delete(Path.Combine(workspace, "search-entered"));
        var disposing = new ContextRuntime(executable, workspace, logs.Enqueue, _ => Task.CompletedTask);
        var waiting = disposing.SearchAsync("cancel", ".", 2, default);
        while (!File.Exists(Path.Combine(workspace, "search-entered"))) await Task.Delay(10, deadline.Token);
        await disposing.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        await disposing.DisposeAsync();
        try { await waiting; throw new InvalidOperationException("Runtime disposal did not cancel a caller without a deadline."); }
        catch (OperationCanceledException) { }
        await File.WriteAllTextAsync(Path.Combine(workspace, "requires-login"), "");
        await using var signedOut = new ContextRuntime(executable, workspace, logs.Enqueue, _ => Task.CompletedTask);
        try { await signedOut.SearchAsync("query", ".", 2, deadline.Token); throw new InvalidOperationException("Signed-out native MCP appeared ready."); }
        catch (ContextLoginRequiredException) { }
        Console.WriteLine("PASS Context native-command fixtures: pinned launch, fresh-execution authorization, warm MCP schema/roots, progress, redaction, bounded results, cancellation, reconnect and login state");
    }

    internal const string FakeJbContextArgument = "--fake-jbcontext";

    // A fake jbcontext for the native workflow: logs each argv to invocations.jsonl in the working directory, then serves the index,
    // status and mcp modes. hold-index makes index never finish, requires-login makes mcp refuse to start, and the code_search queries
    // "cancel" (never answered, writes search-entered), "needs-auth" and "large" are special. Any other mode exits with 22.
    internal static int FakeJbContext(string[] args)
    {
        static void Write(TextWriter writer, string line)
        {
            writer.Write(line + "\n");
            writer.Flush();
        }
        static void Send(JsonObject message)
        {
            var framed = new JsonObject { ["jsonrpc"] = "2.0" };
            foreach (var (key, value) in message) framed[key] = value?.DeepClone();
            Write(Console.Out, framed.ToJsonString());
        }
        var mode = args.Length > 0 ? args[0] : null;
        File.AppendAllText("invocations.jsonl", new JsonArray(args.Select(arg => (JsonNode?)arg).ToArray()).ToJsonString() + "\n");
        if (mode == "index")
        {
            Write(Console.Out, new JsonObject { ["progress"] = 50, ["token"] = "fixture-native-private" }.ToJsonString());
            if (File.Exists("hold-index")) Thread.Sleep(Timeout.Infinite);
            Write(Console.Out, """{"indexed":true}""");
            return 0;
        }
        if (mode == "status") { Write(Console.Out, """{"indexed":true}"""); return 0; }
        if (mode != "mcp") return 22;
        if (File.Exists("requires-login")) { Write(Console.Out, "You need to log in to JetBrains AI Platform first"); return 1; }
        JsonNode? initialize = null, root = null;
        while (Console.In.ReadLine() is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            var method = message["method"]?.GetValue<string>();
            var id = message["id"];
            if (method == "initialize")
            {
                initialize = id?.DeepClone();
                Send(new JsonObject { ["id"] = 100, ["method"] = "roots/list" });
            }
            else if (id is JsonValue number && number.GetValueKind() == System.Text.Json.JsonValueKind.Number && number.GetValue<double>() == 100)
            {
                root = message["result"]!["roots"]?.DeepClone();
                Send(new JsonObject { ["id"] = initialize?.DeepClone(), ["result"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() } } });
            }
            else if (method == "tools/list")
            {
                var schema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" }, ["pathFilter"] = new JsonObject { ["type"] = "string" }, ["limit"] = new JsonObject { ["type"] = "integer" } },
                    ["required"] = new JsonArray("text"),
                };
                Send(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "code_search", ["inputSchema"] = schema }) } });
            }
            else if (method == "tools/call")
            {
                var arguments = message["params"]!["arguments"]!.AsObject();
                var text = arguments["text"] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;
                if (text == "cancel") { File.WriteAllText("search-entered", ""); continue; }
                if (text == "needs-auth")
                {
                    Send(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "You need to log in to JetBrains AI Platform first" }) } });
                    continue;
                }
                if (text == "large")
                {
                    Send(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = new string('x', 50_000) }) } });
                    continue;
                }
                Write(Console.Error, "Authorization: Bearer fixture-mcp-private");
                var echo = new JsonObject();
                if (root is not null) echo["root"] = root.DeepClone();
                foreach (var (key, item) in arguments) echo[key] = item?.DeepClone();
                Send(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = echo.ToJsonString() }) } });
            }
        }
        return 0;
    }
}
