using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>
/// The agent tools that change code through a language server (lsp_code_actions, lsp_rename, lsp_format) against the fixture server:
/// listing, preview writes nothing, apply writes and reports the diagnostics delta, a stale listing, an edit outside the workspace, an
/// editor's unsaved buffer, file operations, rename and format, and a server without the capability.
/// </summary>
internal static class LanguageRefactoringChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Rig(string root, bool bare) : IAsyncDisposable
    {
        internal string Workspace = Path.Combine(root, bare ? "bare" : "workspace");
        internal HostStateStore State = null!;
        internal IReadOnlyList<McpServer.McpTool> Tools = null!;
        private string? disabled;
        private int next;

        internal async Task StartAsync()
        {
            Directory.CreateDirectory(Path.Combine(Workspace, ".tools"));
            if (bare) await File.WriteAllTextAsync(Path.Combine(Workspace, ".tools", "no-rename"), "");
            disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
            State = new HostStateStore(Path.Combine(root, bare ? "bare-state" : "state"));
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
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
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
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
        Require(outside["state"]!.ToString() == "unsupported" && !File.Exists(Path.Combine(Path.GetTempPath(), "sharprail-outside.ts")), "An outside edit was not refused: " + outside.ToJsonString());

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
        Console.WriteLine("PASS agent refactoring: list, preview, apply with delta, resolve and command edits, stale, outside root, dirty buffer, file operations, rename, format, BOM and CRLF, missing capability");
    }
}