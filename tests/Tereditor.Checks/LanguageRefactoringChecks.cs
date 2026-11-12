using System.Text.Json;
using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;

namespace Tereditor.Checks;

/// <summary>
/// The agent tools that change code through a language server (lsp_code_actions, lsp_rename, lsp_format) against the fixture server:
/// listing, preview writes nothing, apply writes and reports the diagnostics delta, a stale listing, an edit outside the workspace, an
/// editor's unsaved buffer, file operations, rename and format, and a server without the capability.
/// </summary>
internal static class LanguageRefactoringChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Rig(string root, bool bare, string mode = "") : IAsyncDisposable
    {
        internal string Workspace = Path.Combine(root, bare ? "bare" : mode.Length > 0 ? mode : "workspace");
        internal HostStateStore State = null!;
        internal IReadOnlyList<McpServer.McpTool> Tools = null!;
        private string? disabled;
        private int next;

        internal async Task StartAsync()
        {
            Directory.CreateDirectory(Path.Combine(Workspace, ".tools"));
            if (bare) await File.WriteAllTextAsync(Path.Combine(Workspace, ".tools", "no-rename"), "");
            if (mode == "report") await File.WriteAllTextAsync(Path.Combine(Workspace, ".tools", "workspace-diagnostic"), "");
            disabled = Environment.GetEnvironmentVariable("TEREDITOR_LSP_DISABLE"); Environment.SetEnvironmentVariable("TEREDITOR_LSP_DISABLE", "0");
            State = new HostStateStore(Path.Combine(root, bare ? "bare-state" : mode.Length > 0 ? mode + "-state" : "state"));
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, mode == "push" ? "--fake-lsp-push" : "--fake-lsp"] : [mode == "push" ? "--fake-lsp-push" : "--fake-lsp"];
            await State.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            Tools = LanguageMcpTools.Create(State, Workspace);
        }

        internal string Read(string name) => File.ReadAllText(Path.Combine(Workspace, name));
        internal void Write(string name, string text) => File.WriteAllText(Path.Combine(Workspace, name), text);

        internal async Task<JsonNode> Call(string tool, object arguments)
        {
            var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++next, method = "tools/call", @params = new { name = tool, arguments } });
            var (_, body) = await McpServer.HandleAsync(JsonNode.Parse(message), Tools, default);
            return JsonNode.Parse(body!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        }

        /// <summary>Lists the actions at the diagnostic of the fixture file, waiting out a server that is still starting.</summary>
        internal async Task<JsonNode> List(string path, string kinds = "")
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var listed = await Call("lsp_code_actions", new { path, line = 0, column = 4, kinds });
                if (listed["state"]!.ToString() != "initializing") return listed;
                await Task.Delay(500);
            }
            throw new InvalidOperationException("The fixture server never became ready.");
        }

        internal static string Id(JsonNode listed, string title) => listed["actions"]!.AsArray().Single(action => action!["title"]!.ToString() == title)!["id"]!.ToString();

        public async ValueTask DisposeAsync()
        {
            await State.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("TEREDITOR_LSP_DISABLE", disabled);
        }
    }

    /// <summary>A call hierarchy item of the fixture server; each name sits on its own line so the host tells them apart.</summary>
    internal static object CallNode(string uri, string name)
    {
        var line = Array.IndexOf(["target", "callerA", "callerB", "callerC", "calleeA", "calleeB", "calleeC"], name);
        var range = new { start = new { line, character = 0 }, end = new { line, character = name.Length } };
        return new { name, kind = 12, uri, range, selectionRange = range };
    }

    private static async Task Queries(Rig rig, Rig bare)
    {
        rig.Write("code.ts", "😀 broken");
        await rig.List("code.ts");

        // capabilities: one server by file, all running servers without one; a server without the capability says nothing about it.
        var caps = await rig.Call("lsp_status", new { operation = "capabilities", path = "code.ts" });
        Require(caps["result"]!["serverCapabilities"]!["callHierarchyProvider"]!.GetValue<bool>() && caps["result"]!["serverCapabilities"]!["renameProvider"] is not null, "Capabilities: " + caps.ToJsonString());
        var allCaps = await rig.Call("lsp_status", new { operation = "capabilities" });
        Require(allCaps["result"]!["typescript"]!["serverCapabilities"] is not null, "Capabilities of all servers: " + allCaps.ToJsonString());

        // Raw requests: queries pass, anything that writes or executes is refused with the tool to use.
        var highlight = await rig.Call("lsp_navigate", new { operation = "request", method = "textDocument/documentHighlight", path = "code.ts", line = 0, column = 4 });
        Require(highlight["state"]!.ToString() == "ready" && highlight["result"]!.AsArray().Count == 1, "Raw query: " + highlight.ToJsonString());
        foreach (var (method, tool) in new[] { ("workspace/executeCommand", "lsp_code_actions"), ("textDocument/rename", "lsp_rename"), ("textDocument/formatting", "lsp_format"), ("textDocument/didChange", "notification"), ("workspace/willRenameFiles", "lsp_rename"), ("vendor/anything", "Allowed") })
        {
            var refused = await rig.Call("lsp_navigate", new { operation = "request", method, path = "code.ts" });
            Require(refused["state"]!.ToString() == "refused" && refused["detail"]!.ToString().Contains(tool, StringComparison.Ordinal), $"{method} was not refused with {tool}: {refused.ToJsonString()}");
        }
        var byServer = await rig.Call("lsp_navigate", new { operation = "request", method = "workspace/symbol", server = "typescript", query = "target" });
        Require(byServer["state"]!.ToString() == "ready" && byServer["result"]!.AsArray().Count == 1, "Workspace-level raw query: " + byServer.ToJsonString());
        var bareRaw = await bare.Call("lsp_navigate", new { operation = "request", method = "textDocument/prepareCallHierarchy", path = "code.ts", line = 0, column = 4 });
        Require(bareRaw["state"]!.ToString() == "unsupported", "A raw query the server does not declare: " + bareRaw.ToJsonString());

        // Call hierarchy: depth, a cycle listed once, the per-node and total caps, and gating.
        var callers = await rig.Call("lsp_navigate", new { operation = "incomingCalls", path = "code.ts", line = 0, column = 4 });
        var shape = callers["locations"]!.AsArray().Select(node => $"{node!["text"]}@{node["depth"]?.ToString() ?? "0"}").ToArray();
        Require(callers["state"]!.ToString() == "ready" && shape.SequenceEqual(["target@0", "callerA@1", "callerC@2", "target (listed above)@2", "callerB@1"]), "Callers: " + callers.ToJsonString());
        var narrow = await rig.Call("lsp_navigate", new { operation = "incomingCalls", path = "code.ts", line = 0, column = 4, maxChildren = 1 });
        Require(narrow["locations"]!.AsArray().Select(node => node!["text"]!.ToString()).SequenceEqual(["target", "callerA", "callerC"]) && narrow["truncated"]!.GetValue<bool>(), "maxChildren: " + narrow.ToJsonString());
        var capped = await rig.Call("lsp_navigate", new { operation = "incomingCalls", path = "code.ts", line = 0, column = 4, maxNodes = 1 });
        Require(capped["locations"]!.AsArray().Count == 2 && capped["truncated"]!.GetValue<bool>(), "maxNodes: " + capped.ToJsonString());
        var callees = await rig.Call("lsp_navigate", new { operation = "outgoingCalls", path = "code.ts", line = 0, column = 4, depth = 1 });
        Require(callees["locations"]!.AsArray().Select(node => node!["text"]!.ToString()).SequenceEqual(["target", "calleeA", "calleeB"]), "Callees at depth 1: " + callees.ToJsonString());
        var noCalls = await bare.Call("lsp_navigate", new { operation = "incomingCalls", path = "code.ts", line = 0, column = 4 });
        Require(noCalls["state"]!.ToString() == "unsupported" && noCalls["detail"]!.ToString().Contains("call hierarchy", StringComparison.Ordinal), "Call hierarchy without the capability: " + noCalls.ToJsonString());

        // Diagnostics: globs, errors first, minSeverity, the method used.
        rig.Write("w1.ts", "warned"); rig.Write("w2.ts", "broken w2");
        var globbed = await rig.Call("lsp_diagnostics", new { paths = new[] { "w*.ts" } });
        Require(globbed["summary"]!["checked"]!.GetValue<int>() == 2 && globbed["diagnostics"]![0]!["text"]!.ToString() == "broken" && globbed["diagnostics"]![1]!["text"]!.ToString() == "warned", "Glob and severity order: " + globbed.ToJsonString());
        Require(globbed["methods"]![0]!.ToString() == "textDocument/diagnostic", "The method used is not reported: " + globbed.ToJsonString());
        var errors = await rig.Call("lsp_diagnostics", new { paths = new[] { "w*.ts" }, minSeverity = "error" });
        Require(errors["diagnostics"]!.AsArray().Count == 1 && errors["detail"]!.ToString().Contains("below minSeverity", StringComparison.Ordinal), "minSeverity: " + errors.ToJsonString());
        // A server error (CS1061, severity 1) and one without a severity count as errors; the summary counts what is shown by severity, apart from files that failed.
        rig.Write("r1.ts", "roslyn");
        var roslyn = await rig.Call("lsp_diagnostics", new { paths = new[] { "r1.ts" } });
        var summary = roslyn["summary"]!;
        Require(roslyn["diagnostics"]![0]!["severity"]!.ToString() == "error" && roslyn["diagnostics"]![0]!["code"]!.ToString() == "CS1061" && roslyn["diagnostics"]![1]!["severity"]!.ToString() == "error"
            && summary["errors"]!.GetValue<int>() == 2 && summary["hints"]!.GetValue<int>() == 1 && summary["failedFiles"]!.GetValue<int>() == 0, "Roslyn-shaped severities: " + roslyn.ToJsonString());
        var roslynErrors = await rig.Call("lsp_diagnostics", new { paths = new[] { "r1.ts" }, minSeverity = "error" });
        Require(roslynErrors["diagnostics"]!.AsArray().Count == 2 && roslynErrors["summary"]!["errors"]!.GetValue<int>() == 2 && roslynErrors["detail"]!.ToString().Contains("1 hint", StringComparison.Ordinal), "minSeverity keeps Roslyn's errors and names the hidden hint: " + roslynErrors.ToJsonString());
        var nothing = await rig.Call("lsp_diagnostics", new { paths = new[] { "zzz*.ts" } });
        Require(nothing["detail"]!.ToString().Contains("matched no source file", StringComparison.Ordinal), "An empty glob: " + nothing.ToJsonString());
    }

    private static async Task Moves(Rig rig, Rig bare)
    {
        static async Task<JsonNode> Move(Rig target, string path, string to, string operation = "preview") => await target.Call("lsp_rename", new { operation, path, newPath = to });
        rig.Write("old.ts", "x"); rig.Write("imports.ts", "from old.ts");
        await rig.List("code.ts");

        var preview = await Move(rig, "old.ts", "moved.ts");
        var kinds = preview["changes"]!.AsArray().Select(change => change!["change"]!.ToString()).ToArray();
        Require(preview["state"]!.ToString() == "ready" && !preview["applied"]!.GetValue<bool>() && kinds.SequenceEqual(["renamed", "edited"]) && File.Exists(Path.Combine(rig.Workspace, "old.ts")) && rig.Read("imports.ts") == "from old.ts", "Move preview: " + preview.ToJsonString());
        var applied = await Move(rig, "old.ts", "moved.ts", "apply");
        Require(applied["applied"]!.GetValue<bool>() && rig.Read("moved.ts") == "x" && !File.Exists(Path.Combine(rig.Workspace, "old.ts")) && rig.Read("imports.ts") == "from moved.ts", "Move apply: " + applied.ToJsonString());
        var told = Path.Combine(rig.Workspace, ".tools", "did-rename.json");
        for (var wait = 0; wait < 50 && !File.Exists(told); wait++) await Task.Delay(100);
        Require(File.Exists(told) && File.ReadAllText(told).Contains("moved.ts", StringComparison.Ordinal), "The server was not told about the move.");

        // Refusals write nothing.
        rig.Write("a.ts", "a"); rig.Write("b.ts", "b");
        foreach (var (path, to, state) in new[] { ("a.ts", "a.ts", "notApplicable"), ("missing.ts", "c.ts", "notFound"), ("a.ts", "b.ts", "conflict"), ("a.ts", "../outside.ts", "unsupported") })
        {
            var refused = await Move(rig, path, to, "apply");
            Require(refused["state"]!.ToString() == state, $"{path} to {to} should be {state}: {refused.ToJsonString()}");
        }
        Require(rig.Read("a.ts") == "a" && rig.Read("b.ts") == "b", "A refused move changed files.");
        await rig.State.Languages.ExecuteAsync(new(rig.Workspace, "a.ts", "inspect", Text: "a", DocumentId: "editor-2", Version: 1, Dirty: true));
        var dirty = await Move(rig, "a.ts", "c.ts", "apply");
        Require(dirty["state"]!.ToString() == "conflict" && File.Exists(Path.Combine(rig.Workspace, "a.ts")), "An unsaved buffer was moved: " + dirty.ToJsonString());
        await rig.State.Languages.ExecuteAsync(new(rig.Workspace, "a.ts", "close", DocumentId: "editor-2"));

        // A server that fails willRenameFiles stops the move before anything is written; method-not-found only skips that server.
        rig.Write("fail.ts", "f"); rig.Write("nomethod.ts", "n");
        var failed = await Move(rig, "fail.ts", "failed.ts", "apply");
        Require(failed["state"]!.ToString() == "error" && File.Exists(Path.Combine(rig.Workspace, "fail.ts")) && !File.Exists(Path.Combine(rig.Workspace, "failed.ts")), "A failing server: " + failed.ToJsonString());
        var skipped = await Move(rig, "nomethod.ts", "skipped.ts", "apply");
        Require(skipped["applied"]!.GetValue<bool>() && File.Exists(Path.Combine(rig.Workspace, "skipped.ts")), "Method not found: " + skipped.ToJsonString());

        // A move that fails rolls the import edits back.
        rig.Write("old2.ts", "y"); rig.Write("imports.ts", "from old2.ts"); rig.Write("blocked", "file in the way");
        var rolled = await Move(rig, "old2.ts", "blocked/x.ts", "apply");
        Require(rolled["state"]!.ToString() == "failed" && rig.Read("imports.ts") == "from old2.ts" && File.Exists(Path.Combine(rig.Workspace, "old2.ts")), "Rollback: " + rolled.ToJsonString());

        // A directory moves with its files, and the server is asked for each.
        Directory.CreateDirectory(Path.Combine(rig.Workspace, "dir"));
        rig.Write("dir/a.ts", "da"); rig.Write("dir/b.ts", "db"); rig.Write("imports.ts", "from dir/a.ts\nfrom dir/b.ts");
        var directory = await Move(rig, "dir", "relocated", "apply");
        Require(directory["applied"]!.GetValue<bool>() && rig.Read("relocated/b.ts") == "db" && !Directory.Exists(Path.Combine(rig.Workspace, "dir")) && rig.Read("imports.ts").Replace("\r", "") == "from relocated/a.ts\nfrom relocated/b.ts", "Directory move: " + directory.ToJsonString());

        // Without the capability only the path moves, and the result says so.
        bare.Write("plain.ts", "p");
        await bare.List("code.ts");
        var plain = await Move(bare, "plain.ts", "plain2.ts", "apply");
        Require(plain["applied"]!.GetValue<bool>() && plain["detail"]!.ToString().Contains("No running language server declared", StringComparison.Ordinal) && File.Exists(Path.Combine(bare.Workspace, "plain2.ts")), "Move without the capability: " + plain.ToJsonString());
    }

    private static async Task Reports(string root)
    {
        await using var report = new Rig(root, bare: false, mode: "report");
        await report.StartAsync();
        report.Write("a.ts", "broken a"); report.Write("b.ts", "clean"); report.Write("c.ts", "broken c");
        await report.List("a.ts");
        var all = await report.Call("lsp_diagnostics", new { scope = "workspace" });
        Require(all["methods"]!.AsArray().Any(method => method!.ToString() == "workspace/diagnostic") && all["diagnostics"]!.AsArray().Any(item => item!["source"]!.ToString() == "workspace-report"), "Workspace diagnostics did not use workspace/diagnostic: " + all.ToJsonString());

        await using var push = new Rig(root, bare: false, mode: "push");
        await push.StartAsync();
        push.Write("late.ts", "late broken"); push.Write("stale.ts", "stale broken");
        var late = await push.Call("lsp_diagnostics", new { paths = new[] { "late.ts" } });
        Require(late["files"]![0]!["state"]!.ToString() == "checked" && late["files"]![0]!["method"]!.ToString() == "publishDiagnostics", "Late diagnostics were not waited for: " + late.ToJsonString());
        push.State.Languages.DiagnosticWait = TimeSpan.FromSeconds(1);
        var stale = await push.Call("lsp_diagnostics", new { paths = new[] { "stale.ts" } });
        Require(stale["files"]![0]!["state"]!.ToString() == "pending", "A version that never arrives is not clean: " + stale.ToJsonString());
    }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "language-refactoring");
        await using var rig = new Rig(root, bare: false);
        await rig.StartAsync();
        var tools = rig.Tools.Select(tool => tool.Name).ToArray();
        Require(new[] { "lsp_code_actions", "lsp_rename", "lsp_format" }.All(tools.Contains), "The refactoring tools are not in the roster: " + string.Join(", ", tools));
        Require(rig.Tools.Single(tool => tool.Name == "lsp_rename").InputSchema["properties"]!["operation"]!["default"]!.ToString() == "preview", "lsp_rename must default to preview.");

        // Listing: ids, kinds, preferred first, what each carries, files of an eager edit.
        rig.Write("code.ts", "😀 broken");
        var listed = await rig.List("code.ts");
        Require(listed["state"]!.ToString() == "ready", "Listing failed: " + listed.ToJsonString());
        var titles = listed["actions"]!.AsArray().Select(action => action!["title"]!.ToString()).ToArray();
        Require(titles.SequenceEqual(["Fix broken", "Run fixture command", "Resolve me"]), "Listed: " + string.Join(", ", titles));
        var fix = listed["actions"]![0]!;
        Require(fix["preferred"]!.GetValue<bool>() && fix["carries"]!.ToString() == "edit" && fix["files"]![0]!.ToString() == "code.ts", "The preferred action is described wrong: " + fix.ToJsonString());
        Require(listed["actions"]![2]!["carries"]!.ToString() == "resolve" && listed["actions"]![1]!["carries"]!.ToString() == "command", "Resolve and command actions are described wrong.");
        var kindFiltered = await rig.List("code.ts", "refactor");
        Require(kindFiltered["actions"]!.AsArray().Single()!["title"]!.ToString() == "Resolve me", "The kinds filter did not apply: " + kindFiltered.ToJsonString());
        var byCode = await rig.Call("lsp_code_actions", new { path = "code.ts", query = "broken" });
        Require(byCode["actions"]!.AsArray().Count == 3, "Selecting by diagnostic failed: " + byCode.ToJsonString());
        var noMatch = await rig.Call("lsp_code_actions", new { path = "code.ts", query = "CS9999" });
        Require(noMatch["state"]!.ToString() == "notFound", "A missing diagnostic must be notFound: " + noMatch.ToJsonString());

        // Preview writes nothing and says which files and edits.
        var preview = await rig.Call("lsp_code_actions", new { operation = "preview", id = Rig.Id(listed, "Fix broken") });
        Require(preview["state"]!.ToString() == "ready" && !preview["applied"]!.GetValue<bool>() && preview["changes"]![0]!["path"]!.ToString() == "code.ts" && preview["changes"]![0]!["edits"]!.GetValue<int>() == 2, "Preview: " + preview.ToJsonString());
        Require(rig.Read("code.ts") == "😀 broken", "Preview changed the file.");

        // Apply writes, reports the changed files and the diagnostics delta (the fixture reports "broken" only while the text has it).
        var applied = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Fix broken") });
        Require(applied["state"]!.ToString() == "ready" && applied["applied"]!.GetValue<bool>(), "Apply: " + applied.ToJsonString());
        Require(rig.Read("code.ts") == "// 😀 fixed", "Apply wrote: " + rig.Read("code.ts"));
        Require(applied["diagnostics"]!["removed"]!.AsArray().Count == 1 && applied["diagnostics"]!["added"]!.AsArray().Count == 0, "The diagnostics delta is wrong: " + applied.ToJsonString());

        // An action resolved on apply, and a command whose workspace/applyEdit belongs to the chosen action.
        rig.Write("code.ts", "😀 broken");
        listed = await rig.List("code.ts");
        var resolved = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Resolve me") });
        Require(resolved["applied"]!.GetValue<bool>() && rig.Read("code.ts") == "/*r*/😀 broken", "Resolve then apply: " + resolved.ToJsonString());
        rig.Write("code.ts", "😀 broken");
        listed = await rig.List("code.ts");
        var command = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Run fixture command") });
        Require(command["applied"]!.GetValue<bool>() && rig.Read("code.ts") == "/*cmd*/😀 broken", "Command then apply: " + command.ToJsonString());

        // A listing is refused once the file changed; so is an unknown id.
        rig.Write("code.ts", "😀 broken");
        listed = await rig.List("code.ts");
        rig.Write("code.ts", "😀 broken again");
        var stale = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Fix broken") });
        Require(stale["state"]!.ToString() == "stale" && rig.Read("code.ts") == "😀 broken again", "A stale listing was applied: " + stale.ToJsonString());
        var unknown = await rig.Call("lsp_code_actions", new { operation = "apply", id = "ca999.0" });
        Require(unknown["state"]!.ToString() == "notFound", "An unknown id: " + unknown.ToJsonString());

        // An edit outside the workspace is refused whole.
        rig.Write("code.ts", "😀 broken outside");
        listed = await rig.List("code.ts");
        var outside = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Edit outside") });
        Require(outside["state"]!.ToString() == "unsupported" && !File.Exists(Path.Combine(Path.GetTempPath(), "tereditor-outside.ts")), "An outside edit was not refused: " + outside.ToJsonString());

        // An editor's unsaved buffer is never overwritten.
        rig.Write("code.ts", "😀 broken");
        await rig.State.Languages.ExecuteAsync(new(rig.Workspace, "code.ts", "inspect", Text: "😀 broken", DocumentId: "editor-1", Version: 1, Dirty: true));
        listed = await rig.List("code.ts");
        var dirty = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Fix broken") });
        Require(dirty["state"]!.ToString() == "conflict" && rig.Read("code.ts") == "😀 broken", "A dirty buffer was overwritten: " + dirty.ToJsonString());
        await rig.State.Languages.ExecuteAsync(new(rig.Workspace, "code.ts", "close", DocumentId: "editor-1"));

        // File operations: create with content, rename, delete.
        rig.Write("code.ts", "😀 broken fileops");
        rig.Write("old.ts", "old");
        rig.Write("gone.ts", "gone");
        listed = await rig.List("code.ts");
        var operations = await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "File operations") });
        Require(operations["applied"]!.GetValue<bool>() && rig.Read("created.ts") == "hello" && rig.Read("renamed.ts") == "old" && !File.Exists(Path.Combine(rig.Workspace, "old.ts")) && !File.Exists(Path.Combine(rig.Workspace, "gone.ts")),
            "File operations: " + operations.ToJsonString());
        var kinds = operations["changes"]!.AsArray().Select(change => change!["change"]!.ToString()).Order().ToArray();
        Require(kinds.SequenceEqual(["created", "deleted", "renamed"]), "Reported changes: " + string.Join(", ", kinds));

        // Rename: preview writes nothing and lists both files; apply writes them. A bad name is refused before the server is asked.
        rig.Write("code.ts", "😀 broken");
        rig.Write("other.ts", "broken");
        var renamePreview = await rig.Call("lsp_rename", new { path = "code.ts", line = 0, column = 4, newName = "fixed" });
        Require(renamePreview["state"]!.ToString() == "ready" && !renamePreview["applied"]!.GetValue<bool>() && renamePreview["totals"]!["files"]!.GetValue<int>() == 2
            && renamePreview["detail"]!.ToString().Contains("Rename broken to fixed", StringComparison.Ordinal), "Rename preview: " + renamePreview.ToJsonString());
        Require(rig.Read("code.ts") == "😀 broken" && rig.Read("other.ts") == "broken", "Rename preview wrote files.");
        var renamed = await rig.Call("lsp_rename", new { operation = "apply", path = "code.ts", line = 0, column = 4, newName = "fixed" });
        Require(renamed["applied"]!.GetValue<bool>() && rig.Read("code.ts") == "😀 fixed" && rig.Read("other.ts") == "fixed", "Rename apply: " + renamed.ToJsonString());
        var badName = await rig.Call("lsp_rename", new { path = "code.ts", line = 0, column = 4, newName = "two words\n" });
        Require(badName["state"]!.ToString() == "notApplicable", "A bad name was sent: " + badName.ToJsonString());

        // Format.
        rig.Write("format.ts", "  messy");
        var formatted = await rig.Call("lsp_format", new { operation = "apply", path = "format.ts" });
        Require(formatted["applied"]!.GetValue<bool>() && rig.Read("format.ts") == "messy", "Format: " + formatted.ToJsonString());
        var already = await rig.Call("lsp_format", new { path = "format.ts" });
        Require(already["state"]!.ToString() == "notApplicable", "Formatting a formatted file: " + already.ToJsonString());

        // A line-ending-preserving, BOM-preserving edit.
        File.WriteAllBytes(Path.Combine(rig.Workspace, "bom.ts"), [0xEF, 0xBB, 0xBF, .. "😀 broken\r\nsecond\r\n"u8.ToArray()]);
        listed = await rig.List("bom.ts");
        await rig.Call("lsp_code_actions", new { operation = "apply", id = Rig.Id(listed, "Fix broken") });
        var bytes = File.ReadAllBytes(Path.Combine(rig.Workspace, "bom.ts"));
        Require(bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) == "// 😀 fixed\r\nsecond\r\n", "BOM or line endings lost: " + BitConverter.ToString(bytes));

        // A server without the capability is reported, never emulated.
        await using var bare = new Rig(root, bare: true);
        await bare.StartAsync();
        bare.Write("code.ts", "😀 broken");
        await bare.List("code.ts");
        var unsupported = await bare.Call("lsp_rename", new { path = "code.ts", line = 0, column = 4, newName = "fixed" });
        Require(unsupported["state"]!.ToString() == "unsupported" && bare.Read("code.ts") == "😀 broken", "A missing rename capability: " + unsupported.ToJsonString());

        // The editor's own operations stay closed to everyone but the agent tools.
        try
        {
            await rig.State.Languages.ExecuteAsync(new(rig.Workspace, "code.ts", "agentCommit") { Id = "x" });
            Require(false, "An agent operation ran without the agent origin.");
        }
        catch (ArgumentException) { }
        await Queries(rig, bare);
        await Moves(rig, bare);
        await Reports(root);
        Console.WriteLine("PASS agent refactoring: list, preview, apply with delta, resolve and command edits, stale, outside root, dirty buffer, file operations, rename, format, BOM and CRLF, missing capability; capabilities, raw requests and refusals, call hierarchy, diagnostic globs, severity and methods, file and directory moves with rollback");
    }
}