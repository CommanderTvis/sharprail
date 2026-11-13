using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Language;

namespace Tereditor.Checks;

/// <summary>
/// The agent edit hook's diagnostics: only from servers already running (never starting one), a skipped note per file whose
/// server is not running, errors before warnings with information and hints hidden, a cap with the rest counted, silence for
/// clean edits and files no server handles, and an answer within the deadline even when a server never reports.
/// </summary>
internal static class EditDiagnosticsChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Rig(string root, bool push = false) : IAsyncDisposable
    {
        internal string Workspace = Path.Combine(root, push ? "push" : "workspace");
        internal HostStateStore State = null!;
        private IReadOnlyList<McpServer.McpTool> tools = null!;
        private string? disabled;
        private int next;

        internal async Task StartAsync()
        {
            Directory.CreateDirectory(Workspace);
            disabled = Environment.GetEnvironmentVariable("TEREDITOR_LSP_DISABLE"); Environment.SetEnvironmentVariable("TEREDITOR_LSP_DISABLE", "0");
            State = new HostStateStore(Path.Combine(root, push ? "push-state" : "state"));
            var executable = Environment.ProcessPath!;
            var mode = push ? "--fake-lsp-push" : "--fake-lsp";
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, mode] : [mode];
            var fake = JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web);
            foreach (var id in new[] { "typescript", "python" })
                await State.ChangeAsync([HostStateChange.Setting("language-server:" + id, fake)]);
            tools = LanguageMcpTools.Create(State, Workspace);
        }

        internal void Write(string name, string text) => File.WriteAllText(Path.Combine(Workspace, name), text);

        internal async Task<string> Row(string id)
            => (await State.Languages.ExecuteAsync(new(Workspace, Operation: "servers"))).Servers.Single(item => item.Id == id).State;

        // What lsp_diagnostics does for the agent: it starts the file's server, which the hook never does.
        internal async Task Start(string file)
        {
            var message = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = ++next, method = "tools/call", @params = new { name = "lsp_diagnostics", arguments = new { paths = new[] { file }, workspace = Workspace } } });
            await McpServer.HandleAsync(JsonNode.Parse(message), tools, default);
        }

        internal Task<string?> Hook(string tool, string file, string key = "file_path")
        {
            var input = new JsonObject { ["hook_event_name"] = "PostToolUse", ["cwd"] = Workspace, ["tool_name"] = tool, ["tool_input"] = new JsonObject { [key] = Path.Combine(Workspace, file) } };
            return EditDiagnostics.ContextAsync(State, Workspace, input, default);
        }

        public async ValueTask DisposeAsync()
        {
            await State.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("TEREDITOR_LSP_DISABLE", disabled);
        }
    }

    internal static async Task Run(string fixture)
    {
        Format();
        var root = Path.Combine(fixture, "edit-diagnostics");
        await Running(root);
        await Deadline(root);
        Console.WriteLine("PASS edit diagnostics: running servers only, skipped notes, order and cap, silence, deadline");
    }

    private static async Task Running(string root)
    {
        await using var rig = new Rig(root);
        await rig.StartAsync();
        var workspace = rig.Workspace;
        rig.Write("code.ts", "broken code"); rig.Write("warn.ts", "warned code"); rig.Write("clean.ts", "fine");
        rig.Write("agent.py", "broken = missing"); rig.Write("notes.md", "broken prose");

        // Nothing runs yet: the edit is reported skipped, and the server is still not running afterwards.
        var skipped = await rig.Hook("Edit", "code.ts");
        Require(skipped == $"tereditor: no running language server for {Path.Combine(workspace, "code.ts")} (typescript); skipped. lsp_diagnostics with its path starts it.",
            "A file whose server is not running is not reported skipped: " + skipped);
        Require(await rig.Row("typescript") == "idle" && await rig.Row("python") == "idle", "The edit hook started a language server.");

        // Once the agent's lsp_diagnostics started TypeScript, edits get its diagnostics; Python stays skipped and stopped.
        await rig.Start("clean.ts");
        Require(await rig.Row("typescript") == "ready", "lsp_diagnostics did not start the TypeScript fixture server.");
        var errors = await rig.Hook("Write", "code.ts");
        Require(errors is not null && errors.StartsWith("tereditor: diagnostics from the running language server after this edit", StringComparison.Ordinal) &&
            errors.Contains("\n" + Path.Combine(workspace, "code.ts") + ":", StringComparison.Ordinal) && errors.Contains(" error broken", StringComparison.Ordinal),
            "An edit with an error does not report it: " + errors);
        var warning = await rig.Hook("Edit", "warn.ts");
        Require(warning is not null && warning.Contains(" warning warned", StringComparison.Ordinal), "An edit with a warning does not report it: " + warning);
        Require(await rig.Hook("Edit", "clean.ts") is null, "A clean edit was not silent.");
        Require(await rig.Hook("NotebookEdit", "notes.md", "notebook_path") is null, "A file no language server handles was not silent.");
        Require(await EditDiagnostics.ContextAsync(rig.State, workspace, JsonNode.Parse("""{"tool_input":{"file_path":"/elsewhere/outside.ts"}}"""), default) is null &&
            await EditDiagnostics.ContextAsync(rig.State, workspace, JsonNode.Parse("""{"tool_input":{}}"""), default) is null, "A file outside the project, or no file at all, was not silent.");
        var python = await rig.Hook("Edit", "agent.py");
        Require(python is not null && python.Contains("no running language server for " + Path.Combine(workspace, "agent.py") + " (python); skipped", StringComparison.Ordinal),
            "A Python edit without its server is not reported skipped: " + python);
        Require(await rig.Row("python") == "idle", "The edit hook started the Python server.");
        // A path relative to the session's directory resolves against it.
        var relative = await EditDiagnostics.ContextAsync(rig.State, workspace, new JsonObject { ["cwd"] = workspace, ["tool_input"] = new JsonObject { ["file_path"] = "code.ts" } }, default);
        Require(relative is not null && relative.Contains(" error broken", StringComparison.Ordinal), "A relative file path was not resolved against cwd: " + relative);
    }

    private static async Task Deadline(string root)
    {
        var (budget, deadline) = (EditDiagnostics.Budget, EditDiagnostics.Deadline);
        await using var rig = new Rig(root, push: true);
        await rig.StartAsync();
        rig.State.Languages.DiagnosticWait = TimeSpan.FromSeconds(1);
        // A push-only server that never confirms this version: the scan stops at its budget and says the file was not ready.
        rig.Write("stale.ts", "stale broken"); rig.Write("first.ts", "fine");
        await rig.Start("first.ts");
        try
        {
            EditDiagnostics.Budget = TimeSpan.FromSeconds(1);
            var clock = Stopwatch.StartNew();
            var pending = await rig.Hook("Edit", "stale.ts");
            Require(clock.Elapsed < TimeSpan.FromSeconds(4) && pending is not null && pending.Contains("diagnostics for " + Path.Combine(rig.Workspace, "stale.ts") + " were not ready", StringComparison.Ordinal),
                $"A file the server never confirms is not reported pending within the budget ({clock.Elapsed}): " + pending);
            // The hard deadline answers with one note even when the scan itself would wait longer.
            EditDiagnostics.Budget = TimeSpan.FromSeconds(30);
            rig.State.Languages.DiagnosticWait = TimeSpan.FromSeconds(30);
            EditDiagnostics.Deadline = TimeSpan.FromMilliseconds(500);
            clock.Restart();
            rig.Write("stale.ts", "stale broken again");
            var late = await rig.Hook("Edit", "stale.ts");
            Require(clock.Elapsed < TimeSpan.FromSeconds(3) && late is not null && late.StartsWith("tereditor: diagnostics for this edit did not finish within", StringComparison.Ordinal),
                $"The hard deadline did not answer in time ({clock.Elapsed}): " + late);
        }
        finally { (EditDiagnostics.Budget, EditDiagnostics.Deadline) = (budget, deadline); }
    }

    // Errors first, then warnings, by path and position; information and hints hidden; at most 20 lines and the rest counted.
    private static void Format()
    {
        var items = new List<LanguageItem>();
        for (var line = 0; line < 3; line++) items.Add(new("b.ts", line, 4, line, 5, "a hint", "4"));
        for (var line = 0; line < 8; line++) items.Add(new("a.ts", 10 - line, 2, 10 - line, 3, "a warning", "2") { Raw = """{"code":"W1"}""" });
        for (var line = 0; line < 17; line++) items.Add(new(line % 2 == 0 ? "b.ts" : "a.ts", line, 0, line, 1, "an error\nover two lines", "1") { Raw = """{"code":2304}""" });
        items.Add(new("a.ts", 1, 0, 1, 1, "information", "3"));
        var result = new LanguageResult("ready") { Diagnostics = items, Files = [new("a.ts", "checked", "current", ""), new("b.ts", "checked", "current", ""), new("c.py", "skipped", LanguageService.NotRunning, "python")] };
        var text = EditDiagnostics.Format([("/ws", result)])!;
        var lines = text.Split('\n');
        Require(lines.Length == 1 + 20 + 1 + 1, "The cap is not 20 lines plus the rest: " + text);
        Require(lines[1..18].All(line => line.Contains(" error 2304 an error over two lines", StringComparison.Ordinal)) && lines[18..21].All(line => line.Contains(" warning W1 a warning", StringComparison.Ordinal)),
            "Errors do not come before warnings: " + text);
        Require(lines[1] == Path.Combine("/ws", "a.ts") + ":2:1 error 2304 an error over two lines" && lines[9] == Path.Combine("/ws", "b.ts") + ":1:1 error 2304 an error over two lines" && lines[18] == Path.Combine("/ws", "a.ts") + ":4:3 warning W1 a warning",
            "Lines are not ordered by path and 1-based position: " + text);
        Require(lines[21] == "… and 5 more; lsp_diagnostics with the path lists them all." && !text.Contains("hint", StringComparison.Ordinal) && !text.Contains("information", StringComparison.Ordinal),
            "The rest is not counted, or information and hints are shown: " + text);
        Require(lines[22] == "tereditor: no running language server for " + Path.Combine("/ws", "c.py") + " (python); skipped. lsp_diagnostics with its path starts it.", "The skipped note is missing: " + text);
        Require(EditDiagnostics.Format([("/ws", new LanguageResult("ready") { Diagnostics = [new("a.ts", 0, 0, 0, 1, "hint", "4")], Files = [new("a.ts", "checked", "current", "")] })]) is null,
            "Hints alone were not silent.");
    }
}