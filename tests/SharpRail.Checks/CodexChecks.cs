using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.Codex.Host;
using SharpRail.Plugins.Codex.Host.IdeBridge;
using SharpRail.Plugins.Codex.UI;

namespace SharpRail.Checks;

// The Codex plugin's own logic, translated from the fork's package tests: the configuration reference, the in-place
// TOML edit and layering, hooks, hook reports, the launch line, revival, the rollout reader, process detection, the
// app-server account and model reads against a fixture executable, the host module, the IDE-context IPC transport,
// and the UI half's store, model picker and editor projection.
internal static class CodexChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Codex: " + message);
    }

    private static void Equal<T>(T actual, T expected, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"Codex: {message}: expected {expected}, got {actual}");
    }

    /// <summary>A short temporary directory: Unix socket paths are limited to about a hundred bytes.</summary>
    internal static string ShortTemp(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static async Task RunHost(string root)
    {
        Directory.CreateDirectory(root);
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            ConfigDocs();
            Config(Path.Combine(root, "codex-config"));
            Skills(Path.Combine(root, "codex-skills"));
            await HookCommand(Path.Combine(root, "codex-hook"));
            Status();
            Launch();
            if (!OperatingSystem.IsWindows()) LaunchShell(Path.Combine(root, "codex-launch-shell"));
            Resume(Path.Combine(root, "codex-resume"));
            InstructionScopes(ShortTemp("tr-cs-"));
            Rollout(Path.Combine(root, "codex-rollout"));
            Processes();
            await AppServer(Path.Combine(root, "codex-account"));
            await HostModule(Path.Combine(root, "codex-host"));
            if (!OperatingSystem.IsWindows()) await IdeBridge();
            Store();
            await ModelPicker();
            EditorProjection();
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", previousHome); }
        Console.WriteLine("PASS Codex plugin logic: configuration, hooks, skills, launch, revival, rollout, processes, app-server, host module, IDE bridge, picker");
    }

    private static void Skills(string root)
    {
        var home = Path.Combine(root, "home");
        var previousState = Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR");
        Environment.SetEnvironmentVariable("CODEX_HOME", home);
        Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", Path.Combine(root, "state"));
        try
        {
            var shipped = CodexSkills.ShippedRoot(null);
            string Shipped(string path) => File.ReadAllText(Path.Combine(shipped, path));
            string Installed(string path) => Path.Combine(home, "skills", path);
            void Write(string path, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            Require(Directory.GetDirectories(shipped).Select(Path.GetFileName).Order().SequenceEqual(
                ["importing-a-codebase", "setting-up-a-project", "shipping-a-pr", "starting-a-new-project", "writing-specs"]), "the shipped skills");

            // A skill of the same name that SharpRail never wrote is the user's, as is every other skill.
            Write(Installed("shipping-a-pr/SKILL.md"), "mine\n");
            Write(Installed("my-skill/SKILL.md"), "also mine\n");
            CodexSkills.Install(shipped);
            Equal(File.ReadAllText(Installed("writing-specs/SKILL.md")), Shipped("writing-specs/SKILL.md"), "a skill is installed where Codex discovers it");
            Require(File.ReadAllText(Installed("shipping-a-pr/SKILL.md")) == "mine\n" && !File.Exists(Installed("shipping-a-pr/body.md")), "the user's own skill of that name is left whole");

            // Installing again writes nothing; an edited file stays the user's and a deleted one comes back.
            var written = File.GetLastWriteTimeUtc(Installed("setting-up-a-project/SKILL.md"));
            Write(Installed("writing-specs/SKILL.md"), "edited\n");
            File.Delete(Installed("importing-a-codebase/SKILL.md"));
            CodexSkills.Install(shipped);
            Equal(File.GetLastWriteTimeUtc(Installed("setting-up-a-project/SKILL.md")), written, "an unchanged skill is not rewritten");
            Equal(File.ReadAllText(Installed("writing-specs/SKILL.md")), "edited\n", "the user's edit survives a reinstall");
            Require(File.Exists(Installed("importing-a-codebase/SKILL.md")), "a missing file is restored");

            // A later build's text replaces what SharpRail wrote and takes away what it no longer ships.
            var next = Path.Combine(root, "next");
            Write(Path.Combine(next, "setting-up-a-project", "SKILL.md"), "newer\n");
            Write(Path.Combine(next, "writing-specs", "SKILL.md"), "newer\n");
            CodexSkills.Install(next);
            Equal(File.ReadAllText(Installed("setting-up-a-project/SKILL.md")), "newer\n", "an unedited skill follows the shipped text");
            Equal(File.ReadAllText(Installed("writing-specs/SKILL.md")), "edited\n", "an edited one does not");
            Require(!Directory.Exists(Installed("importing-a-codebase")) && !Directory.Exists(Installed("starting-a-new-project")), "skills no longer shipped are removed");

            // Disabling removes SharpRail's files and nothing of the user's.
            CodexSkills.Install(shipped);
            Require(File.Exists(Installed("starting-a-new-project/SKILL.md")), "skills return with the build that ships them");
            CodexSkills.Remove();
            Require(Directory.GetFiles(Path.Combine(home, "skills"), "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(Path.Combine(home, "skills"), file)).Order()
                .SequenceEqual([Path.Combine("my-skill", "SKILL.md"), Path.Combine("shipping-a-pr", "SKILL.md"), Path.Combine("writing-specs", "SKILL.md")]) &&
                Directory.GetDirectories(Path.Combine(home, "skills")).Length == 3, "removal leaves only the user's skills and edits");
            CodexSkills.Install(shipped);
            Equal(File.ReadAllText(Installed("writing-specs/SKILL.md")), "edited\n", "the edit is still the user's after disabling and enabling");
            File.Delete(Installed("writing-specs/SKILL.md"));
            CodexSkills.Remove();
            Require(Directory.GetDirectories(Path.Combine(home, "skills")).Length == 2 && !File.Exists(Path.Combine(root, "state", "codex", "skills.sharprail-default")),
                "nothing of SharpRail's remains once the user's edit is gone");
        }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", previousState); }
    }

    private static void ConfigDocs()
    {
        Require(CodexConfigDocs.EnumValues("sandbox_mode")!.SequenceEqual(["read-only", "workspace-write", "danger-full-access"]), "sandbox_mode is an enum");
        Require(CodexConfigDocs.EnumValues("approval_policy")!.SequenceEqual(["on-request", "never"]), "approval_policy's literal choices");
        Require(CodexConfigDocs.EnumValues("model") is null && CodexConfigDocs.EnumValues("hide_agent_reasoning") is null && CodexConfigDocs.EnumValues("not_a_key") is null,
            "only an a | b type is a picker");
        Equal(CodexConfigDocs.DocsUrl("model"), "https://learn.chatgpt.com/docs/config-file/config-reference#:~:text=model,-string", "a key's text fragment");
        Require(CodexConfigDocs.DocsUrl("features.hooks")!.Contains("#:~:text=features.hooks,-boolean", StringComparison.Ordinal), "a nested documented key");
        Require(CodexConfigDocs.DocsUrl("unknown_key") is null, "an undocumented key has no link");
        Require(CodexConfigDocs.AddableKeys.Contains("features.hooks") && CodexConfigDocs.AddableKeys.Contains("sandbox_mode") && !CodexConfigDocs.AddableKeys.Contains("tui") &&
            !CodexConfigDocs.AddableKeys.Any(key => key.Contains('<')), "only documented, editable leaf keys are addable");
        Equal(CodexConfigDocs.ValueShape("hide_agent_reasoning"), CodexValueShape.Switch, "boolean shape");
        Equal(CodexConfigDocs.ValueShape("project_doc_max_bytes"), CodexValueShape.Number, "number shape");
        Equal(CodexConfigDocs.ValueShape("notify"), CodexValueShape.List, "list shape");
        Equal(CodexConfigDocs.ValueShape("sandbox_mode"), null, "an enum has no free shape");
    }

    private static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);

    private static void Config(string root)
    {
        var home = Path.Combine(root, "home");
        var worktree = Path.Combine(root, "repo");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.Combine(worktree, ".codex"));
        // A repository of its own, so instruction discovery stops here rather than at the checkout holding the fixtures.
        Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        Environment.SetEnvironmentVariable("CODEX_HOME", home);

        // setKeyPath replaces, inserts before the first table, and removes.
        var text = "# mine\nmodel = \"a\"\n\n[features]\nhooks = true\n";
        Equal(CodexConfig.SetKeyPath(text, ["model"], Value("b")), "# mine\nmodel = \"b\"\n\n[features]\nhooks = true\n", "replace");
        Equal(CodexConfig.SetKeyPath(text, ["sandbox_mode"], Value("read-only")), "# mine\nmodel = \"a\"\nsandbox_mode = \"read-only\"\n\n[features]\nhooks = true\n", "insert");
        Equal(CodexConfig.SetKeyPath(text, ["model"], null), "# mine\n\n[features]\nhooks = true\n", "remove");
        Equal(CodexConfig.SetKeyPath("", ["model"], Value("x")), "model = \"x\"\n", "into an empty file");

        // Edits inside a table section, and the section appended when it is missing.
        var tables = "model = \"a\"\n\n[features]\nhooks = true\n\n[tui]\ntheme = \"dark\"\n";
        Equal(CodexConfig.SetKeyPath(tables, ["features", "memories"], Value(true)), "model = \"a\"\n\n[features]\nhooks = true\nmemories = true\n\n[tui]\ntheme = \"dark\"\n", "insert in a table");
        Require(CodexConfig.SetKeyPath(tables, ["tui", "theme"], Value("light")).Contains("[tui]\ntheme = \"light\"", StringComparison.Ordinal), "replace in a table");
        Equal(CodexConfig.SetKeyPath(tables, ["features", "hooks"], null), "model = \"a\"\n\n[features]\n\n[tui]\ntheme = \"dark\"\n", "remove in a table");
        Equal(CodexConfig.SetKeyPath(tables, ["projects", "/a b", "trust_level"], Value("trusted")), tables.TrimEnd() + "\n\n[projects.\"/a b\"]\ntrust_level = \"trusted\"\n", "append a section");

        // Tables flatten into dotted keys, with Codex's hook and project trust bookkeeping left out.
        File.WriteAllText(Path.Combine(home, "config.toml"), "[features]\nhooks = true\n[hooks.state.'x:y']\ntrusted_hash = 'h'\n[projects.\"/r\"]\ntrust_level = \"trusted\"\n");
        var flattened = CodexConfig.Resolve(worktree).Settings;
        Require(flattened.Count == 1 && flattened[0].Key == "features.hooks" && flattened[0].KeyPath.SequenceEqual(["features", "hooks"]), "flattened keys");

        // A same-named key inside a table is never touched; an edit that cannot be made in place is refused.
        Equal(CodexConfig.SetKeyPath("[profiles.fast]\nmodel = 'x'\n", ["model"], Value("y")), "model = \"y\"\n[profiles.fast]\nmodel = 'x'\n", "a same-named key in a table");
        var refused = false;
        try { CodexConfig.SetKeyPath("model = [\n \"a\",\n]\n", ["model"], Value("b")); }
        catch (InvalidOperationException error) when (error.Message.Contains("edit the file by hand", StringComparison.Ordinal)) { refused = true; }
        Require(refused, "a multi-line value is left for the user");

        // The project layer wins only when the project is trusted.
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"user\"\n[mcp_servers.docs]\nurl = \"http://d\"\n");
        File.WriteAllText(Path.Combine(worktree, ".codex", "config.toml"), "model = \"project\"\n");
        var untrusted = CodexConfig.Resolve(worktree);
        Require(!untrusted.ProjectTrusted && untrusted.Settings.Single(setting => setting.Key == "model").Value.GetString() == "user", "an untrusted project layer is skipped");
        Require(untrusted.Layers.Single(layer => layer.Scope == CodexScope.Project).Ignored, "the skipped layer says so");
        Require(untrusted.McpServers.SequenceEqual([new CodexMcpServer("docs", CodexScope.User, Path.Combine(home, "config.toml"), "http://d")]), "MCP servers are listed by name");
        CodexConfig.WriteValue(worktree, CodexWritableScope.User, ["sandbox_mode"], Value("read-only"));
        File.AppendAllText(Path.Combine(home, "config.toml"), $"\n[projects.\"{root}\"]\ntrust_level = \"trusted\"\n");
        var trusted = CodexConfig.Resolve(worktree);
        var model = trusted.Settings.Single(setting => setting.Key == "model");
        Require(trusted.ProjectTrusted && model.Value.GetString() == "project" && model.Scope == CodexScope.Project && model.Path == Path.Combine(worktree, ".codex", "config.toml"),
            "a trusted ancestor applies the project layer");
        Require(model.Shadowed.Count == 1 && model.Shadowed[0].Value.GetString() == "user" && model.Shadowed[0].Scope == CodexScope.User, "the user value is shadowed");
        Equal(trusted.Settings.Single(setting => setting.Key == "sandbox_mode").Value.GetString(), "read-only", "written value");

        // AGENTS.override.md beats AGENTS.md, and an empty file is skipped.
        File.WriteAllText(Path.Combine(home, "AGENTS.override.md"), "");
        File.WriteAllText(Path.Combine(home, "AGENTS.md"), "global");
        File.WriteAllText(Path.Combine(worktree, "AGENTS.md"), "plain");
        File.WriteAllText(Path.Combine(worktree, "AGENTS.override.md"), "override");
        var instructions = CodexConfig.Resolve(worktree).Instructions;
        Require(instructions.Count == 2 && instructions[0] == new CodexInstructions(CodexInstructionsScope.Global, Path.Combine(home, "AGENTS.md"), 6) &&
            instructions[1] == new CodexInstructions(CodexInstructionsScope.Project, Path.Combine(worktree, "AGENTS.override.md"), 8) { RelativePath = "AGENTS.override.md" },
            "the instruction chain");

        // installHooks keeps the user's hooks and is idempotent.
        File.WriteAllText(Path.Combine(home, "hooks.json"), "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"echo mine\"}]}]}}");
        Require(!CodexConfig.HooksInstalled(), "not installed yet");
        CodexConfig.InstallHooks();
        CodexConfig.InstallHooks();
        var hooks = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "hooks.json")))!["hooks"]!.AsObject();
        Equal(hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>(), "echo mine", "the user's hook stays first");
        Equal(hooks["Stop"]![1]!["hooks"]![0]!["command"]!.GetValue<string>(), CodexConfig.HookCommand, "ours follows it");
        foreach (var name in CodexConfig.HookEvents) Equal(hooks[name]!.AsArray().Count, name == "Stop" ? 2 : 1, "one of ours per event: " + name);
        Require(CodexConfig.HooksInstalled() && !CodexConfig.HooksTrusted(), "installed, not yet trusted");

        // Typed values keep their TOML type; an enum key refuses a value Codex does not accept.
        File.Delete(Path.Combine(home, "config.toml"));
        CodexConfig.WriteValue(worktree, CodexWritableScope.User, ["hide_agent_reasoning"], Value(true));
        CodexConfig.WriteValue(worktree, CodexWritableScope.User, ["project_doc_max_bytes"], Value(65536));
        CodexConfig.WriteValue(worktree, CodexWritableScope.User, ["project_doc_fallback_filenames"], Value(new[] { "CLAUDE.md" }));
        var typed = CodexConfig.Parse(File.ReadAllText(Path.Combine(home, "config.toml")));
        Require(typed["hide_agent_reasoning"]!.GetValue<bool>() && typed["project_doc_max_bytes"]!.GetValue<long>() == 65536 &&
            typed["project_doc_fallback_filenames"]!.AsArray().Single()!.GetValue<string>() == "CLAUDE.md", "typed values");
        File.Delete(Path.Combine(home, "config.toml"));
        var invalid = false;
        try { CodexConfig.WriteValue(worktree, CodexWritableScope.User, ["sandbox_mode"], Value("anything")); }
        catch (ArgumentException error) when (error.Message.Contains("sandbox_mode must be one of", StringComparison.Ordinal)) { invalid = true; }
        Require(invalid, "an enum key refuses an unknown value");
        CodexConfig.WriteValue(worktree, CodexWritableScope.User, ["sandbox_mode"], Value("read-only"));
        Equal(File.ReadAllText(Path.Combine(home, "config.toml")), "sandbox_mode = \"read-only\"\n", "a documented choice is written");

        // createInstructions writes a non-empty starter and never overwrites.
        File.Delete(Path.Combine(home, "AGENTS.md"));
        File.Delete(Path.Combine(worktree, "AGENTS.override.md"));
        File.WriteAllText(Path.Combine(worktree, "AGENTS.md"), "mine");
        Require(CodexConfig.CreateInstructions(worktree, CodexInstructionsTarget.Project) == new CodexCreatedInstructions(Path.Combine(worktree, "AGENTS.md")) { RelativePath = "AGENTS.md" },
            "the created project path");
        Equal(File.ReadAllText(Path.Combine(worktree, "AGENTS.md")), "mine", "an existing file is kept");
        CodexConfig.CreateInstructions(worktree, CodexInstructionsTarget.Global);
        Require(CodexConfig.Resolve(worktree).Instructions.Select(file => file.Scope).SequenceEqual([CodexInstructionsScope.Global, CodexInstructionsScope.Project]), "the starter counts");
    }

    // The hook command posts its stdin only inside a terminal that carries the status URL.
    private static async Task HookCommand(string root)
    {
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var request = new StringBuilder();
                int length;
                while ((length = await stream.ReadAsync(buffer, stop.Token)) > 0)
                {
                    request.Append(Encoding.UTF8.GetString(buffer, 0, length));
                    var text = request.ToString();
                    var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (split < 0) continue;
                    var declared = int.Parse(text[..split].Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    if (Encoding.UTF8.GetByteCount(text[(split + 4)..]) < declared) continue;
                    lock (received) received.Add(text[(split + 4)..]);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), stop.Token);
                    break;
                }
            }
        });
        static async Task<int> Run(string? url)
        {
            var start = new ProcessStartInfo("/bin/sh", ["-c", CodexConfig.HookCommand]) { RedirectStandardInput = true, UseShellExecute = false };
            start.Environment.Remove(CodexConfig.StatusUrlEnv);
            if (url is not null) start.Environment[CodexConfig.StatusUrlEnv] = url;
            using var process = Process.Start(start)!;
            // Outside a terminal the hook exits without reading stdin, so the write may meet a closed pipe.
            try { await process.StandardInput.WriteAsync("{\"hook_event_name\":\"Stop\"}"); process.StandardInput.Close(); }
            catch (IOException) when (url is null) { }
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        Equal(await Run(null), 0, "inert outside a terminal");
        Equal(await Run($"http://127.0.0.1:{port}/s"), 0, "posts inside one");
        stop.Cancel();
        listener.Stop();
        try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        lock (received) Require(received.SequenceEqual(["{\"hook_event_name\":\"Stop\"}"]), "the hook posts stdin verbatim, once: " + string.Join(",", received));
    }

    private static JsonElement Hook(object body) => JsonSerializer.SerializeToElement(body);

    private static void Status()
    {
        Require(CodexStatusReports.Parse(Hook(new { hook_event_name = "UserPromptSubmit", session_id = "s1", model = "gpt-5", prompt = "fix it" })) ==
            new CodexHookReport("UserPromptSubmit", CodexStatus.Running) { SessionId = "s1", Model = "gpt-5" }, "a report keeps its facts");
        Equal(CodexStatusReports.Parse(Hook(new { hook_event_name = "PermissionRequest" }))?.Status, CodexStatus.Blocked, "blocked");
        Equal(CodexStatusReports.Parse(Hook(new { hook_event_name = "Interrupt" }))?.Status, CodexStatus.Idle, "interrupt is idle");
        Require(CodexStatusReports.Parse(Hook(new { hook_event_name = "Stop", last_assistant_message = "ok", transcript_path = "/c/rollout.jsonl" })) ==
            new CodexHookReport("Stop", CodexStatus.Done) { TranscriptPath = "/c/rollout.jsonl" }, "stop keeps the transcript path");
        Require(CodexStatusReports.Parse(null) is null && CodexStatusReports.Parse(Hook(new { hook_event_name = "PreCompact" })) is null &&
            CodexStatusReports.Parse(Hook(new { })) is null, "unreadable or unknown payloads are ignored");
    }

    private static void Launch()
    {
        Equal(CodexLaunch.Line("codex", new(true, false) { Model = "gpt-5", SystemPrompt = "Be brief.", InitialPrompt = "it's broken" }),
            "codex -c \"mcp_servers.thinkrail.url=\\\"$THINKRAIL_MCP_URL\\\"\" --model gpt-5 -c 'developer_instructions=\"Be brief.\"' 'it'\\''s broken'", "the full launch line");
        Equal(CodexLaunch.Line("", new(true, true) { Resume = true }), "codex resume --last", "Windows gets no MCP override");
        Equal(CodexLaunch.Line("codex", new(false, false) { Resume = true, ResumeSessionId = "abc" }), "codex resume abc", "a resume by id");
        Equal(CodexLaunch.Line("codex", new(false, false) { Resume = true, ResumeSessionId = "$(touch /tmp/injected)" }), "codex resume '$(touch /tmp/injected)'", "a session selector is quoted");

        var presets = CodexLaunch.Menu(CodexConfigDocs.EnumValues).SelectMany(group => group).ToArray();
        CodexLaunchPreset Find(string id) => presets.Single(preset => preset.Id == id);
        Equal(CodexLaunch.Line("codex", new(false, false) { Preset = Find("fork") }), "codex fork", "a subcommand preset");
        Equal(CodexLaunch.Line("codex", new(true, false) { Preset = Find("sandbox-read-only") }), $"codex {CodexLaunch.McpOverride} -s read-only", "flags after the override");
        Require(!presets.Any(preset => preset.Id.StartsWith("model_reasoning_effort-", StringComparison.Ordinal)) && presets.All(preset => preset.Id != "yolo"), "no effort or yolo presets");
        Equal(CodexLaunch.Line("codex", new(true, false) { Preset = Find("full-auto") }), $"codex {CodexLaunch.McpOverride} --sandbox workspace-write --ask-for-approval on-request",
            "workspace write spells out its sandbox and approval");

        // Saved permissions apply to ordinary launches and yield to permission presets.
        var saved = new CodexLaunchOptions(false, false) { PermissionMode = "sandbox-danger-full-access" };
        Equal(CodexLaunch.Line("codex", saved), "codex -s danger-full-access", "the saved mode");
        Equal(CodexLaunch.Line("codex", saved with { Resume = true }), "codex resume --last -s danger-full-access", "the saved mode on a resume");
        foreach (var id in new[] { "search", "fork" })
            Require(CodexLaunch.Line("codex", saved with { Preset = Find(id) }).Contains("-s danger-full-access", StringComparison.Ordinal), "kept beside " + id);
        Require(CodexLaunch.Line("codex", saved with { Preset = new("model-gpt-6-astra", "GPT-6 Astra") { Args = "--model gpt-6-astra", Model = "gpt-6-astra" } })
            .Contains("-s danger-full-access", StringComparison.Ordinal), "kept beside a model");
        foreach (var id in new[] { "sandbox-read-only", "sandbox-workspace-write", "full-auto" })
            Equal(CodexLaunch.Line("codex", saved with { Preset = Find(id) }), $"codex {Find(id).Args}", "a permission preset replaces the saved mode");
        Equal(CodexLaunch.Line("codex", saved with { PermissionMode = "default" }), "codex", "the default adds nothing");
        Equal(CodexLaunch.Line("codex", saved with { PermissionMode = "unknown" }), "codex", "an unknown mode adds nothing");
        Equal(CodexLaunch.Line("codex", saved with { PermissionMode = "full-auto" }), "codex --sandbox workspace-write --ask-for-approval on-request", "full-auto as the saved mode");
    }

    // The shell expands the host's JSON into one TOML string, never evaluating what it holds; fork launch.test.ts.
    private static void LaunchShell(string directory)
    {
        Directory.CreateDirectory(directory);
        const string instructions = "SharpRail instructions\nQuotes \" and apostrophes ' and $HOME and `date`\n";
        static (string Value, string Url, string Line) Run(string? extra, bool append = true)
        {
            // Prints each argument NUL-terminated, then the status URL the command saw.
            var command = "sh -c 'for a; do printf \"%s\\0\" \"$a\"; done; printf \"%s\" \"$THINKRAIL_CODEX_STATUS_URL\"' sh";
            var line = CodexLaunch.Line(command, new(false, false) { AppendSystemPrompt = append, SystemPrompt = extra });
            var start = new ProcessStartInfo("/bin/sh", ["-c", line]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.Environment[CodexLaunch.PromptJsonVariable] = JsonSerializer.Serialize(instructions);
            start.Environment["THINKRAIL_CODEX_STATUS_URL"] = "http://localhost/status/token";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "the launch line runs: " + process.StandardError.ReadToEnd());
            var parts = output.Split('\0');
            Require(parts.Length == 3 && parts[0] == "-c", "one -c override: " + output.Replace('\0', '|'));
            return ((string)CodexConfig.Parse(parts[1])["developer_instructions"]!, parts[2], line);
        }

        var plain = Run(null);
        Equal(plain.Value, instructions, "the host-encoded instructions arrive as one TOML string");
        Equal(plain.Url, "http://localhost/status/token?thinkrail_prompt=1", "the status URL is tagged");
        Require(CodexLaunch.HasSharpRailPrompt(plain.Line), "the line says it carries SharpRail's instructions");
        const string extra = "Be brief.\n\"quotes\" 'apostrophe' \\path $HOME $(exit 91) `exit 92` ✨";
        Equal(Run(extra).Value, instructions + "\n\n" + extra, "extra instructions join the same override, quotes and shell syntax intact");
        var off = Run("Be brief", append: false);
        Equal(off.Value, "Be brief", "off: the launcher's own instructions remain");
        Equal(off.Url, "http://localhost/status/token", "off: the status URL is untagged");
        Require(!CodexLaunch.HasSharpRailPrompt(off.Line), "off: no tag");
        Require(!CodexLaunch.Line("codex", new(false, true) { AppendSystemPrompt = true }).Contains("developer_instructions", StringComparison.Ordinal),
            "Windows launches keep their previous behaviour");
    }

    // Instructions follow a tab's CWD from the repository root, and SharpRail's launch file is listed last; fork config.test.ts.
    private static void InstructionScopes(string root)
    {
        var home = Path.Combine(root, "home");
        var worktree = Path.Combine(root, "worktree");
        var nested = Path.Combine(worktree, "app", "src");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        Environment.SetEnvironmentVariable("CODEX_HOME", home);
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Outside repository");
        File.WriteAllText(Path.Combine(home, "AGENTS.md"), "Global");
        File.WriteAllText(Path.Combine(worktree, "AGENTS.md"), "Root");
        File.WriteAllText(Path.Combine(worktree, "app", "AGENTS.md"), "Shadowed");
        File.WriteAllText(Path.Combine(worktree, "app", "AGENTS.override.md"), "Override");
        File.WriteAllText(Path.Combine(nested, "AGENTS.override.md"), "");
        File.WriteAllText(Path.Combine(nested, "CONTEXT.md"), "Fallback");
        File.WriteAllText(Path.Combine(home, "config.toml"), "project_doc_fallback_filenames = [\"CONTEXT.md\"]");
        var scoped = CodexConfig.Resolve(worktree, nested);
        Equal(scoped.Root, nested, "the snapshot names the directory it was read from");
        Require(scoped.Instructions.Select(file => file.Path).SequenceEqual([Path.Combine(home, "AGENTS.md"), Path.Combine(worktree, "AGENTS.md"),
            Path.Combine(worktree, "app", "AGENTS.override.md"), Path.Combine(nested, "CONTEXT.md")]),
            "one file per directory from the repository root to the CWD: " + string.Join(", ", scoped.Instructions.Select(file => file.Path)));
        Equal(scoped.Instructions[^1].RelativePath, Path.Combine("app", "src", "CONTEXT.md"), "files inside the worktree keep editor paths");
        Equal(CodexConfig.Resolve(worktree).Instructions.Count, 2, "the worktree scope reads only its root");

        Directory.Delete(Path.Combine(worktree, ".git"));
        var plain = Path.Combine(worktree, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "AGENTS.md"), "CWD");
        Require(CodexConfig.Resolve(worktree, plain).Instructions.Where(file => file.Scope == CodexInstructionsScope.Project).Select(file => file.Path)
            .SequenceEqual([Path.Combine(plain, "AGENTS.md")]), "without a repository only the CWD counts");

        var outside = Path.Combine(root, "other");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "AGENTS.md"), "Other directory");
        Require(CodexConfig.Resolve(worktree, outside).Instructions.Last() == new CodexInstructions(CodexInstructionsScope.Project, Path.Combine(outside, "AGENTS.md"), 15),
            "a file outside the worktree opens by its absolute path");

        var prompt = Path.Combine(home, "thinkrail-prompt-codex.md");
        File.WriteAllText(prompt, "SharpRail guidance");
        var launched = CodexConfig.Resolve(worktree, worktree, [prompt]);
        Require(launched.Instructions[^1] == new CodexInstructions(CodexInstructionsScope.Launch, prompt, 18), "the launch file follows the AGENTS chain");
        Require(CodexConfig.Resolve(worktree).Instructions.All(file => file.Scope != CodexInstructionsScope.Launch), "and appears only when given");
    }

    private static void Resume(string home)
    {
        const string id = "01957000-1234-7000-8000-123456789abc";
        void RolloutFile(string folder = "sessions", string content = "{}\n")
        {
            var directory = Path.Combine(home, folder, "2026", "09", "18");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"rollout-2026-09-18T10-00-00-{id}.jsonl"), content);
        }
        string? Command(string command, string? session, bool mcp = false, bool windows = false, string? permissions = null) =>
            CodexResume.Command(command, session, mcp, windows, permissions, home);
        Directory.CreateDirectory(home);

        Equal(Command("codex", id), "codex resume --last", "an unsaved session falls back");
        RolloutFile();
        Equal(Command("codex", id), $"codex resume {id}", "a saved session is resumed across date directories");
        Directory.Delete(Path.Combine(home, "sessions"), true);
        RolloutFile("archived_sessions");
        RolloutFile("sessions", "");
        Require(!CodexResume.SessionExists(id, home), "empty and archived sessions are not offered");
        Equal(Command("codex", "x; touch /tmp/injected"), "codex resume --last", "an invalid id is never shell input");
        Require(!CodexResume.SessionExists("../../outside", home), "a path is not a session id");

        Equal(Command("'path with spaces/codex' -c 'model_reasoning_effort=\"high\"' --model gpt-5 --sandbox workspace-write --image 'old image.png' 'fix the build' --search", null),
            "'path with spaces/codex' -c 'model_reasoning_effort=\"high\"' --model gpt-5 --sandbox workspace-write --search resume --last",
            "quoted executable and option values survive; prompts and images do not");

        RolloutFile();
        foreach (var selector in new[] { "resume --last --all", "resume old-id", "fork old-id", "fork --last" })
            Equal(Command($"codex {selector} 'old prompt' --yolo", id), $"codex --yolo resume {id}", "replaces " + selector);
        var first = Command("codex -m model", id);
        Equal(Command(first ?? "", id), first, "revival is stable");

        Equal(Command("codex", null, mcp: true), CodexLaunch.Line("codex", new(true, false) { Resume = true }), "MCP comes from the shared composer");
        Equal(Command("codex", null, mcp: true, windows: true), "codex resume --last", "Windows has no MCP override");
        var refreshed = Command("codex -c 'mcp_servers.thinkrail.url=\"http://old\"'", null, mcp: true);
        Equal(refreshed, CodexLaunch.Line("codex", new(true, false) { Resume = true }), "a stale MCP override is replaced");
        Equal(Command(refreshed ?? "", null, mcp: true), refreshed, "repeated revival does not accumulate overrides");
        Equal(Command(refreshed ?? "", null), "codex resume --last", "turning MCP off drops it");

        // SharpRail's instructions are regenerated from the current setting exactly once; the user's own override stays.
        string? Revive(string command, bool append) => CodexResume.Command(command, null, false, false, null, home, appendSystemPrompt: append);
        var appended = CodexLaunch.Line("codex -m model", new(false, false) { AppendSystemPrompt = true, SystemPrompt = "Extra instructions" });
        var revived = Revive(appended, true);
        Equal(revived, CodexLaunch.Line("codex -m model", new(false, false) { AppendSystemPrompt = true, Resume = true }), "revival reapplies the setting");
        Equal(Revive(revived ?? "", true), revived, "and stays stable");
        Equal(Revive(appended, false), "codex -m model resume --last", "turned off, the generated override goes");
        const string own = "codex -c 'developer_instructions=\"User instructions\"'";
        Equal(Revive(CodexLaunch.Line(own, new(false, false) { AppendSystemPrompt = true }), false), own + " resume --last", "the user's own override stays");

        foreach (var command in new[] { "", "  ", "codex --model", "codex 'broken", "codex; echo bad", "codex && echo bad", "codex --unknown value" })
            Require(Command(command, id) is null, "no offer for: " + command);
        Equal(Command("codex --model gpt-6-astra", null, permissions: "full-auto"),
            "codex --model gpt-6-astra resume --last --sandbox workspace-write --ask-for-approval on-request", "revival applies the saved permissions");
    }

    private static string TokenCount(long input, long cached, long output) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-09-22T17:57:24.457Z",
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new { total_token_usage = new { input_tokens = input, cached_input_tokens = cached, cache_write_input_tokens = 0, output_tokens = output, reasoning_output_tokens = 18 } }
        }
    }) + "\n";

    private static string FunctionCall(string name, object arguments) => JsonSerializer.Serialize(new
    {
        type = "response_item",
        payload = new { type = "function_call", id = "fc_1", name, arguments = JsonSerializer.Serialize(arguments), call_id = "call_1" }
    }) + "\n";

    private static void Rollout(string root)
    {
        Directory.CreateDirectory(root);
        var rollout = Path.Combine(root, "rollout-2026-09-22T19-56-52-01a0ca43-721d-7a92-a55c-21d1381af545.jsonl");
        File.WriteAllText(rollout, TokenCount(1000, 400, 10) + TokenCount(23611, 11008, 33));
        Require(new CodexRolloutReader().Read(rollout) == new CodexRolloutFacts(new(12603, 33, 11008, 0), null), "the last token_count, cached input split out");

        File.WriteAllText(rollout, FunctionCall("update_plan", new
        {
            explanation = "start",
            plan = new object[] { new { step = "Read the code", status = "completed" }, new { step = "Fix it", status = "in_progress" }, new { step = "Bogus", status = "someday" } }
        }) + FunctionCall("wait", new { cell_id = "16" }));
        Require(new CodexRolloutReader().Read(rollout).Plan!.SequenceEqual([new CodexPlanItem("Read the code", CodexPlanStatus.Completed), new CodexPlanItem("Fix it", CodexPlanStatus.InProgress)]),
            "the last update_plan, with an unknown status dropped");

        var reader = new CodexRolloutReader();
        var later = TokenCount(5000, 1000, 50);
        File.WriteAllText(rollout, TokenCount(1000, 0, 10) + later[..40]);
        Equal(reader.Read(rollout).Usage?.Output, 10, "a half-written line is held back");
        File.AppendAllText(rollout, later[40..]);
        Equal(reader.Read(rollout).Usage, new CodexTokenUsage(4000, 50, 1000, 0), "appended lines are picked up");

        var missing = new CodexRolloutReader();
        Equal(missing.Read(Path.Combine(root, "missing.jsonl")), new CodexRolloutFacts(null, null), "a missing rollout says nothing");
        File.WriteAllText(rollout, "not json\n" + FunctionCall("update_plan", "nope"));
        Equal(new CodexRolloutReader().Read(rollout), new CodexRolloutFacts(null, null), "a malformed plan says nothing");
    }

    private static void Processes()
    {
        static ProcessSnapshot Tab(string inner, string outer) => new([new(10, 1, "zsh"), new(20, 10, outer), new(30, 20, "node"), new(40, 30, inner)]);
        Require(Tab("codex", "claude").RunsInsideAgent(10, "codex", "claude"), "Codex that Claude Code started leaves the tab to Claude Code");
        Require(!Tab("claude", "codex").RunsInsideAgent(10, "codex", "claude") && !Tab("codex", "bash").RunsInsideAgent(10, "codex", "claude") &&
            !Tab("bash", "claude").RunsInsideAgent(10, "codex", "claude"), "Codex is the tab's agent when nothing the record names sits above it");
        Require(ProcessSnapshot.ParseRows("  10     1 /bin/zsh\n  20 10 /usr/local/bin/codex\ngarbage\n").SequenceEqual([new ProcessRow(10, 1, "zsh"), new ProcessRow(20, 10, "codex")]),
            "ps rows parse to base names");
    }

    // The fixture executable's control: what each app-server method answers, and how it misbehaves.
    internal sealed record FakeControl
    {
        public JsonElement? Account { get; init; }
        public JsonElement? Limits { get; init; }
        public JsonElement? Models { get; init; }
        public string? Exit { get; init; }
        public string? Hang { get; init; }
        public string? Malformed { get; init; }
        public string? Error { get; init; }
        public string ErrorMessage { get; init; } = "quota unavailable";
        public bool Descendant { get; init; }
        public string? ArgsLog { get; init; }
        /// <summary>Posts a SessionStart hook report to the status URL the terminal handed it, as installed hooks do.</summary>
        public bool ReportSession { get; init; }
    }

    private static readonly JsonSerializerOptions ControlJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static void WriteControl(string path, FakeControl control) => File.WriteAllText(path, JsonSerializer.Serialize(control, ControlJson));

    /// <summary>Writes an executable script that runs this checks binary as a fake <c>codex</c> driven by <paramref name="control"/>.</summary>
    internal static string FakeCodexExecutable(string path, string control, string log)
    {
        var self = Environment.ProcessPath!;
        var invocation = Path.GetFileNameWithoutExtension(self) == "dotnet" ? $"'{self}' '{typeof(CodexChecks).Assembly.Location}'" : $"'{self}'";
        File.WriteAllText(path, $"#!/bin/sh\nexec {invocation} --fake-codex '{control}' '{log}' \"$@\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static readonly JsonSerializerOptions LogJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The fake <c>codex</c>: <c>--version</c>, an argv recorder, or a line-JSON <c>app-server</c>.</summary>
    internal static int FakeCodex(string[] args)
    {
        var (controlPath, logPath, rest) = (args[0], args[1], args[2..]);
        FakeControl Control() => JsonSerializer.Deserialize<FakeControl>(File.ReadAllText(controlPath), ControlJson)!;
        void Log(JsonObject entry) { lock (logPath) File.AppendAllText(logPath, entry.ToJsonString(LogJson) + "\n"); }
        if (rest.Contains("--version")) { Console.WriteLine("codex 1.0.0"); return 0; }
        if (!rest.SequenceEqual(["app-server"]))
        {
            if (Control().ArgsLog is { } argsLog) File.WriteAllText(argsLog, JsonSerializer.Serialize(rest));
            if (Control().ReportSession && Environment.GetEnvironmentVariable("THINKRAIL_CODEX_STATUS_URL") is { Length: > 0 } url)
            {
                using var http = new System.Net.Http.HttpClient();
                http.PostAsync(url, new System.Net.Http.StringContent("""{"hook_event_name":"SessionStart","session_id":"fake-session"}""",
                    Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
            }
            return 2;
        }
        var stdout = Console.OpenStandardOutput();
        void Write(string text) { var bytes = Encoding.UTF8.GetBytes(text); stdout.Write(bytes); stdout.Flush(); }
        if (Control().Descendant)
        {
            var child = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "trap '' TERM; sleep 600"]) { UseShellExecute = false })!;
            Log(new JsonObject { ["method"] = "descendant", ["pid"] = child.Id });
        }
        var initialized = false;
        using var input = new StreamReader(Console.OpenStandardInput());
        while (input.ReadLine() is { } line)
        {
            var message = JsonNode.Parse(line)!.AsObject();
            var method = message["method"]?.GetValue<string>() ?? "";
            var entry = (JsonObject)message.DeepClone();
            entry["pid"] = Environment.ProcessId;
            Log(entry);
            var control = Control();
            if (control.Exit == method) return 3;
            if (control.Hang == method) continue;
            if (control.Malformed == method) { Write("not json\n"); continue; }
            if (method == "initialized") { initialized = true; continue; }
            var error = method != "initialize" && !initialized ? "missing handshake" : control.Error == method ? control.ErrorMessage : null;
            JsonNode? result = method switch
            {
                "initialize" => new JsonObject(),
                "account/read" => control.Account is { } account ? JsonNode.Parse(account.GetRawText()) : null,
                "model/list" => new JsonObject { ["data"] = control.Models is { } models ? JsonNode.Parse(models.GetRawText()) : new JsonArray() },
                _ => control.Limits is { } limits ? JsonNode.Parse(limits.GetRawText()) : null
            };
            var reply = error is not null
                ? new JsonObject { ["id"] = message["id"]?.DeepClone(), ["error"] = new JsonObject { ["code"] = -1, ["message"] = error } }
                : new JsonObject { ["id"] = message["id"]?.DeepClone(), ["result"] = result };
            Write("{\"method\":\"account/updated\",\"params\":{}}\n");
            var text = reply.ToJsonString(LogJson) + "\n";
            Write(text[..8]);
            Thread.Sleep(5);
            Write(text[8..]);
        }
        return 0;
    }

    private static readonly JsonElement ChatGpt = JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = "test@example.com", planType = "pro" }, requiresOpenaiAuth = true });

    private static JsonElement Bucket(double used = 25, object? secondary = null, string limitName = "Codex") => JsonSerializer.SerializeToElement(new
    {
        limitId = "codex",
        limitName,
        primary = new { usedPercent = used, windowDurationMins = 300, resetsAt = 1_800_000_000 },
        secondary
    });

    private static IReadOnlyList<(string Method, int Pid)> Requests(string log) =>
        [.. File.ReadAllLines(log).Select(line => JsonNode.Parse(line)!).Select(node => (node["method"]!.GetValue<string>(), node["pid"]!.GetValue<int>()))];

    private static bool Running(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task AppServer(string root)
    {
        Directory.CreateDirectory(root);
        var index = 0;
        (CodexAccountReader Reader, Action<FakeControl> Configure, string Log, string Executable) Fixture(TimeSpan? timeout = null)
        {
            var directory = Path.Combine(root, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            var control = Path.Combine(directory, "control.json");
            var log = Path.Combine(directory, "requests.jsonl");
            var defaults = new FakeControl { Account = ChatGpt, Limits = JsonSerializer.SerializeToElement(new { rateLimits = Bucket() }) };
            void Configure(FakeControl next) => WriteControl(control, next with { Account = next.Account ?? defaults.Account, Limits = next.Limits ?? defaults.Limits });
            Configure(new());
            File.WriteAllText(log, "");
            var executable = FakeCodexExecutable(Path.Combine(directory, "codex fixture"), control, log);
            return (new CodexAccountReader(() => "\"" + executable + "\"", timeout ?? TimeSpan.FromSeconds(10)), Configure, log, executable);
        }

        // Initializes once, shares concurrent reads, and refreshes over the retained process.
        var shared = Fixture();
        var first = shared.Reader.ReadAsync();
        Require(ReferenceEquals(shared.Reader.ReadAsync(), first), "concurrent reads share one request sequence");
        var account = await first;
        Require(account is { LoggedIn: true, Email: "test@example.com", Plan: "pro", Version: "1.0.0" } && account.Usage.Single() is { UsedPercent: 25, WindowDurationMins: 300, ResetsAt: 1_800_000_000 },
            "identity and the legacy bucket");
        shared.Configure(new() { Limits = JsonSerializer.SerializeToElement(new { rateLimits = Bucket(63) }) });
        Equal((await shared.Reader.ReadAsync()).Usage[0].UsedPercent, 63, "a refresh rereads");
        Require(Requests(shared.Log).Select(request => request.Method).SequenceEqual(["initialize", "initialized", "account/read", "account/rateLimits/read", "account/read", "account/rateLimits/read"]),
            "one handshake, then reads: " + string.Join(",", Requests(shared.Log).Select(request => request.Method)));
        Equal(Requests(shared.Log).Select(request => request.Pid).Distinct().Count(), 1, "the process is retained");
        await shared.Reader.StopAsync();

        // Every named bucket, without duplicating the legacy view.
        var named = Fixture();
        named.Configure(new()
        {
            Limits = JsonSerializer.SerializeToElement(new
            {
                rateLimits = Bucket(),
                rateLimitsByLimitId = new Dictionary<string, JsonElement>
                {
                    ["codex"] = Bucket(secondary: new { usedPercent = 80, windowDurationMins = 10080, resetsAt = (long?)null }),
                    ["review"] = Bucket(0, limitName: "Code review")
                }
            })
        });
        var buckets = await named.Reader.ReadAsync();
        Require(buckets.Usage.Select(window => window.Id).SequenceEqual(["codex:primary", "codex:secondary", "review:primary"]) && buckets.Usage[2].Label == "Code review" &&
            buckets.UsageFetchedAt is not null, "named buckets win over the legacy view");
        await named.Reader.StopAsync();

        // Signed-out and non-ChatGPT accounts never ask for ChatGPT limits.
        foreach (var signedIn in new object?[] { null, new { type = "apiKey" }, new { type = "amazonBedrock" } })
        {
            var other = Fixture();
            other.Configure(new() { Account = JsonSerializer.SerializeToElement(new { account = signedIn, requiresOpenaiAuth = true }) });
            var read = await other.Reader.ReadAsync();
            Require(read.LoggedIn == (signedIn is not null) && read.Usage.Count == 0 && Requests(other.Log).All(request => request.Method != "account/rateLimits/read"),
                "no limits for " + JsonSerializer.Serialize(signedIn));
            await other.Reader.StopAsync();
        }

        // A rate-limit error keeps the identity but never old usage.
        var failing = Fixture();
        await failing.Reader.ReadAsync();
        failing.Configure(new() { Error = "account/rateLimits/read" });
        Require(await failing.Reader.ReadAsync() is { Email: "test@example.com", Usage.Count: 0, UsageError: "quota unavailable" }, "a limits error");
        await failing.Reader.StopAsync();

        // Exit, a malformed line and a hang each invalidate the process; the next read starts a fresh one.
        foreach (var mode in new[] { "exit", "malformed", "hang" })
        {
            var broken = Fixture(TimeSpan.FromSeconds(1));
            broken.Configure(mode switch { "exit" => new() { Exit = "account/read" }, "malformed" => new() { Malformed = "account/read" }, _ => new() { Hang = "account/read" } });
            var rejected = false;
            try { await broken.Reader.ReadAsync(); } catch (Exception) { rejected = true; }
            Require(rejected, mode + " rejects the read");
            broken.Configure(new());
            Require((await broken.Reader.ReadAsync()).LoggedIn, "recovers after " + mode);
            Equal(Requests(broken.Log).Select(request => request.Pid).Distinct().Count(), 2, "a fresh process after " + mode);
            await broken.Reader.StopAsync();
        }

        // A malformed account or limits response is an error, not zero usage.
        var malformed = Fixture();
        malformed.Configure(new() { Account = JsonSerializer.SerializeToElement(new { }) });
        var invalid = "";
        try { await malformed.Reader.ReadAsync(); } catch (Exception error) { invalid = error.Message; }
        Require(invalid.Contains("Invalid Codex account", StringComparison.Ordinal), "an invalid account: " + invalid);
        malformed.Configure(new() { Limits = JsonSerializer.SerializeToElement(new { rateLimits = new { primary = new { usedPercent = "25" } } }) });
        Require((await malformed.Reader.ReadAsync()).UsageError?.Contains("Invalid Codex rate-limits", StringComparison.Ordinal) == true, "invalid limits");
        await malformed.Reader.StopAsync();

        // Disposal rejects in-flight work and reaps the child.
        var disposed = Fixture();
        await disposed.Reader.ReadAsync();
        disposed.Configure(new() { Hang = "account/read" });
        var pending = disposed.Reader.ReadAsync();
        await Task.Delay(300);
        await disposed.Reader.StopAsync();
        var stopped = "";
        try { await pending; } catch (Exception error) { stopped = error.Message; }
        Equal(stopped, "Codex app-server stopped.", "pending work is rejected");
        var pid = Requests(disposed.Log)[0].Pid;
        Require(!Running(pid), "the child is reaped");
        var after = "";
        try { await disposed.Reader.ReadAsync(); } catch (Exception error) { after = error.Message; }
        Require(after.Contains("stopped", StringComparison.Ordinal), "a stopped reader stays stopped");

        // Initialization is bounded, and a missing executable is reported.
        var hanging = Fixture(TimeSpan.FromSeconds(1));
        hanging.Configure(new() { Hang = "initialize" });
        var timedOut = "";
        try { await hanging.Reader.ReadAsync(); } catch (Exception error) { timedOut = error.Message; }
        Require(timedOut.Contains("initialize timed out", StringComparison.Ordinal), "initialization is bounded: " + timedOut);
        await hanging.Reader.StopAsync();
        var missing = new CodexAppServer(hanging.Executable + "-missing");
        var notStarted = "";
        try { await missing.RequestAsync("account/read"); } catch (Exception error) { notStarted = error.Message; }
        Require(notStarted.Contains("Could not start", StringComparison.Ordinal), "a missing executable: " + notStarted);
        await missing.StopAsync();

        // Disposal also kills a wrapper's resistant descendant.
        var wrapper = Fixture();
        wrapper.Configure(new() { Descendant = true });
        await wrapper.Reader.ReadAsync();
        var descendant = Requests(wrapper.Log).First(request => request.Method == "descendant").Pid;
        Require(Running(descendant), "the descendant runs");
        await wrapper.Reader.StopAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Running(descendant) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Require(!Running(descendant), "the descendant is killed");

        var crashed = Fixture();
        crashed.Configure(new() { Descendant = true, Exit = "account/read" });
        var crashFailure = "";
        try { await crashed.Reader.ReadAsync(); } catch (Exception error) { crashFailure = error.Message; }
        Require(crashFailure.Contains("exited", StringComparison.Ordinal), "a crashed leader rejects pending work");
        descendant = Requests(crashed.Log).First(request => request.Method == "descendant").Pid;
        await crashed.Reader.StopAsync();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (Running(descendant) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Require(!Running(descendant), "a leader crash still kills its reparented descendant");

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var immediate = new CodexAppServer(wrapper.Executable);
            await immediate.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        // The executable comes from the configured command: quoted paths, no interactive arguments, no shell.
        Equal(CodexAppServer.Executable("\"/Applications/Codex CLI/codex\" --yolo"), "/Applications/Codex CLI/codex", "a quoted path");
        Equal(CodexAppServer.Executable("codex --model configured"), "codex", "a bare command");
        foreach (var command in new[] { "CODEX_HOME=/tmp codex", "\"unclosed path" })
        {
            var refused = false;
            try { CodexAppServer.Executable(command); } catch (InvalidOperationException) { refused = true; }
            Require(refused, "refused: " + command);
        }

        // The model catalog: display names as labels, read once.
        var catalog = Fixture();
        catalog.Configure(new() { Models = JsonSerializer.SerializeToElement(new object[] { new { model = "gpt-6-astra", displayName = "GPT-6 Astra" }, new { model = "gpt-5.6-sol" } }) });
        var models = new CodexModelReader(() => "\"" + catalog.Executable + "\"", TimeSpan.FromSeconds(10));
        Require((await models.ReadAsync()).SequenceEqual([new CodexModel("gpt-6-astra", "GPT-6 Astra"), new CodexModel("gpt-5.6-sol", "gpt-5.6-sol")]), "the model catalog");
        await models.StopAsync();
    }

    // A stand-in for the runtime's context: only what the Codex host half touches is real.
    private sealed class FakeHostContext(string workspace) : IPluginHostContext
    {
        public readonly TerminalRef Terminal = new(workspace, "terminal");
        public TerminalAgentRecord? Record;
        public Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>>? RouteHandler;
        public Func<TerminalRef, TerminalAgentRecord, RevivePrefill?>? Revive;
        public readonly List<object?> Published = [];
        public CodexSettings Current = new() { Command = "codex --model configured", Mcp = false, AppendSystemPrompt = false };

        public string Id => CodexManifest.Id;
        public IPluginLogger Log { get; } = new Quiet();
        public string? AssetsDirectory => null;
        public void Method<TParams, TResult>(PluginMethod<TParams, TResult> method, Func<TParams, PluginCall, CancellationToken, ValueTask<TResult>> handler) { }
        public void Publish<TPayload>(PluginChannel<TPayload> channel, TPayload payload, PluginCall? target = null) => Published.Add(payload);
        public void Route(Func<PluginHttpRequest, CancellationToken, ValueTask<PluginHttpResponse>> handler) => RouteHandler = handler;
        public string PublicBaseUrl() => "http://localhost:0/plugin/codex";
        public void ExternalFiles(Func<string, IReadOnlyList<string>> provider) { }
        public void Tool(PluginToolDefinition definition) { }
        public void TerminalEnvironment(Func<TerminalRef, IReadOnlyDictionary<string, string>> contributor) { }
        public string TerminalToken(TerminalRef terminal) => "token";
        public TerminalRef? TerminalForToken(string token) => token == "token" ? Terminal : null;
        public TerminalAgentRecord? AgentRecord(TerminalRef terminal) => Record;
        public void SetAgentRecord(TerminalRef terminal, TerminalAgentRecord? record) => Record = record;
        public void OnTerminal(Action<TerminalEvent> handler) { }
        public IReadOnlyList<TerminalProcess> Terminals() => [];
        public string? WorkspaceForProcess(int pid) => null;
        public void RevivePrefill(Func<TerminalRef, TerminalAgentRecord, RevivePrefill?> hook) => Revive = hook;
        public void WriteTerminal(TerminalRef terminal, string data) { }
        public IReadOnlyList<HostProject> Projects() => [];
        public ValueTask<IReadOnlyList<HostWorkspace>> WorkspacesAsync(string? projectId = null, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<HostWorkspace>>([]);
        public ValueTask<HostWorkspace?> WorkspaceAsync(string id, CancellationToken cancellationToken = default) => ValueTask.FromResult<HostWorkspace?>(null);
        public ValueTask WatchWorkspaceAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public void OnWorkspace(Action<WorkspaceEvent> handler) { }
        public void OnFilesChanged(Action<WorkspaceFilesChanged> handler) { }
        public T Settings<T>() where T : class => (T)(object)Current;
        public void OnSettings<T>(Action<T> handler) where T : class { }
        public T ReadState<T>(string name, T fallback) => fallback;
        public void WriteState<T>(string name, T value) { }
        public ValueTask<GitRunResult> GitAsync(string cwd, IReadOnlyList<string> args, GitRunOptions? options = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new GitRunResult(true, "", ""));
        public IPluginDependencyHandle Dependency(PluginContract contract) => throw new InvalidOperationException("unused");

        public async Task<PluginHttpResponse> Report(object extra, bool appended = false)
        {
            var body = JsonSerializer.SerializeToNode(new { hook_event_name = "SessionStart", session_id = "codex-session" })!.AsObject();
            foreach (var (key, value) in JsonSerializer.SerializeToNode(extra)!.AsObject()) body[key] = value?.DeepClone();
            return await RouteHandler!(new("POST", "status/token", appended ? "thinkrail_prompt=1" : "", new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body.ToJsonString())), CancellationToken.None);
        }

        private sealed class Quiet : IPluginLogger
        {
            public void Debug(string message, object? fields = null) { }
            public void Info(string message, object? fields = null) { }
            public void Warn(string message, object? fields = null) { }
            public void Error(string message, object? fields = null) { }
        }
    }

    private static async Task HostModule(string root)
    {
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("CODEX_HOME", ShortTemp("tr-cx-"));
        var previousState = Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR");
        Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", home);
        try { await HostModuleIn(root, home); }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", previousState); }
    }

    private static async Task HostModuleIn(string root, string home)
    {
        // Activation writes SharpRail's developer instructions where every terminal's JSON value is read from.
        var tagged = new FakeHostContext(root);
        var stopTagged = await new CodexHost().ActivateAsync(tagged);
        Equal(File.ReadAllText(Path.Combine(home, "codex", "thinkrail-prompt-codex.md")), CodexSystemPrompt.Text, "the instructions file is written");
        var skill = Path.Combine(CodexSkills.Root(), "writing-specs", "SKILL.md");
        Require(File.Exists(skill), "activation installs the workflow skills");
        var instructions = Path.Combine(home, "codex", "thinkrail-prompt-codex.md");
        File.WriteAllText(instructions, "my own instructions\n");
        Require(CodexSystemPrompt.Edited(), "an edit to the instructions is recognised as the user's");
        var again = await new CodexHost().ActivateAsync(new FakeHostContext(root));
        Equal(File.ReadAllText(instructions), "my own instructions\n", "reactivating keeps the user's edited instructions");
        await again!();
        Require(!File.Exists(skill), "disabling removes the workflow skills");
        CodexSystemPrompt.Write(reset: true);
        Require(!CodexSystemPrompt.Edited() && File.ReadAllText(instructions) == CodexSystemPrompt.Text, "reset puts SharpRail's text back");
        // An edited file from before the agent-named file moves over with its edits.
        var legacy = Path.Combine(home, "codex", "developer-instructions.md");
        File.Move(instructions, legacy); File.Move(instructions + ".sharprail-default", legacy + ".sharprail-default");
        File.WriteAllText(legacy, "older edits\n");
        CodexSystemPrompt.Write();
        Require(!File.Exists(legacy) && File.ReadAllText(instructions) == "older edits\n" && CodexSystemPrompt.Edited(), "the old instructions file moves to its agent name, edits kept");
        CodexSystemPrompt.Write(reset: true);
        // Only a launch that tags its hook URL records SharpRail's instructions as the session's source.
        await tagged.Report(new { });
        Require(!CodexLaunch.HasSharpRailPrompt(tagged.Record?.Command ?? ""), "an untagged report records no instructions");
        tagged.Record = tagged.Record! with { LaunchedByUi = true };
        await tagged.Report(new { }, appended: true);
        Require(CodexLaunch.HasSharpRailPrompt(tagged.Record?.Command ?? ""), "a tagged report records them");
        Require(tagged.Record?.LaunchedByUi == true, "hook reports retain the separately detected UI launch origin");
        await tagged.Report(new { hook_event_name = "Stop" }, appended: true);
        Require(CodexLaunch.HasSharpRailPrompt(tagged.Record?.Command ?? ""), "later reports keep them");
        await tagged.Report(new { session_id = "manually-started-session" });
        Require(!CodexLaunch.HasSharpRailPrompt(tagged.Record?.Command ?? ""), "a manually started session drops them");
        await stopTagged!();

        // Revival filters agent kinds and offers an editable, normalized fallback.
        var revive = new FakeHostContext(root);
        var dispose = await new CodexHost().ActivateAsync(revive);
        Require(revive.Revive!(revive.Terminal, new("claude", "claude")) is null, "another agent's record is not revived");
        Equal(revive.Revive!(revive.Terminal, new("codex", "codex resume stale old-prompt -m model") { SessionId = "unsaved" }), new RevivePrefill(CodexLaunch.UiLaunchPrefix + " codex -m model resume --last"), "a normalized UI fallback");
        Require(revive.Revive!(revive.Terminal, new("codex", "codex --model")) is null, "an incomplete command offers nothing");
        await dispose!();

        // A Codex hook replaces another agent's record without inheriting its command.
        var replace = new FakeHostContext(root) { Record = new("claude", "claude --model opus") { SessionId = "codex-session" } };
        dispose = await new CodexHost().ActivateAsync(replace);
        Equal((await replace.Report(new { })).Status, 200, "the hook is accepted");
        Equal(replace.Record, new TerminalAgentRecord("codex", "codex --model configured") { SessionId = "codex-session" }, "the record is Codex's");
        await dispose!();

        // A hook's rollout supplies the session's totals and plan to the pushed status.
        var rollout = Path.Combine(home, "rollout.jsonl");
        File.WriteAllText(rollout, FunctionCall("update_plan", new { plan = new object[] { new { step = "Fix it", status = "in_progress" } } }) + TokenCount(23611, 11008, 33));
        var pushes = new FakeHostContext(root);
        dispose = await new CodexHost().ActivateAsync(pushes);
        await pushes.Report(new { hook_event_name = "PostToolUse", transcript_path = rollout });
        var push = (CodexStatusPush)pushes.Published[^1]!;
        Require(push.Status == CodexStatus.Running && push.Usage == new CodexTokenUsage(12603, 33, 11008, 0) && push.Plan!.SequenceEqual([new CodexPlanItem("Fix it", CodexPlanStatus.InProgress)]),
            "the pushed status carries the rollout's facts");
        await dispose!();
    }

    private static byte[] Frame(object value)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var frame = new byte[body.Length + 4];
        BitConverter.TryWriteBytes(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<JsonNode> Response(Socket socket)
    {
        var data = new List<byte>();
        var buffer = new byte[65536];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4.5));
        while (data.Count < 4 || data.Count < 4 + BitConverter.ToInt32([.. data.Take(4)]))
        {
            var read = await socket.ReceiveAsync(buffer, timeout.Token);
            if (read == 0) throw new IOException("closed");
            data.AddRange(buffer.AsSpan(0, read));
        }
        return JsonNode.Parse(data.Skip(4).Take(BitConverter.ToInt32([.. data.Take(4)])).ToArray())!;
    }

    private static async Task<Socket> Client(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return socket;
    }

    internal static async Task<JsonNode> IdeRequest(string path, string root)
    {
        using var socket = await Client(path);
        var bytes = Frame(new { type = "request", requestId = "tui-request", sourceClientId = "codex-tui", version = 0, method = "ide-context", @params = new { workspaceRoot = root } });
        await socket.SendAsync(bytes.AsMemory(0, 2));
        await socket.SendAsync(bytes.AsMemory(2, 7));
        await socket.SendAsync(bytes.AsMemory(9));
        return await Response(socket);
    }

    private static async Task Until(Func<Task<bool>> check, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            if (await check()) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Codex: condition timed out: " + what);
    }

    private static async Task Ready(string path, string root) => await Until(async () =>
    {
        try { return (await IdeRequest(path, root))["resultType"]?.GetValue<string>() == "success"; }
        catch (Exception) { return false; }
    }, "provider for " + root);

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task IdeBridge()
    {
        if (OperatingSystem.IsWindows()) return;
        var cleanup = new List<IDisposable>();
        (CodexIdeBridge Bridge, List<Exception> Warnings) Provider(string path, string root)
        {
            var warnings = new List<Exception>();
            var bridge = new CodexIdeBridge(path, requested => Task.FromResult(requested == root),
                _ => Task.FromResult<JsonNode?>(JsonSerializer.SerializeToNode(new { activeFile = (object?)null, openTabs = new[] { new { path = root + "/file.ts", label = "file.ts" } } })),
                error => { lock (warnings) warnings.Add(error); });
            cleanup.Add(bridge);
            return (bridge, warnings);
        }
        try
        {
            // Native TUI frames route to the matching provider and survive the router owner's shutdown.
            var directory = ShortTemp("tr-ide-");
            var path = Path.Combine(directory, "ipc.sock");
            var first = Provider(path, "/one");
            await Ready(path, "/one");
            var second = Provider(path, "/two");
            await Ready(path, "/two");
            var concurrent = await Task.WhenAll(IdeRequest(path, "/one"), IdeRequest(path, "/two"));
            Equal(concurrent[0]["result"]!["ideContext"]!["openTabs"]![0]!["path"]!.GetValue<string>(), "/one/file.ts", "the first provider answers its workspace");
            Equal(concurrent[1]["result"]!["ideContext"]!["openTabs"]![0]!["path"]!.GetValue<string>(), "/two/file.ts", "the second provider answers its workspace");
            var routed = await IdeRequest(path, "/one");
            Require(routed["requestId"]!.GetValue<string>() == "tui-request" && routed["resultType"]!.GetValue<string>() == "success", "the reply keeps the TUI's request id");
            var unknown = await IdeRequest(path, "/unknown");
            Require(unknown["resultType"]!.GetValue<string>() == "error" && unknown["error"]!.GetValue<string>() == "no-client-found", "no provider for an unknown workspace");
            Equal(File.GetUnixFileMode(path), UnixFileMode.UserRead | UnixFileMode.UserWrite, "the socket is the user's only");
            first.Bridge.Dispose();
            await Ready(path, "/two");
            Require(first.Warnings.Count == 0 && second.Warnings.Count == 0, "no warnings: " + string.Join("; ", first.Warnings.Concat(second.Warnings).Select(error => error.Message)));
            second.Bridge.Dispose();
            await Until(() => Task.FromResult(!File.Exists(path)), "the owned socket is removed");

            // A provider disconnect does not remove another router's socket.
            directory = ShortTemp("tr-ide-");
            path = Path.Combine(directory, "ipc.sock");
            Provider(path, "/one");
            await Ready(path, "/one");
            var leaving = Provider(path, "/two");
            await Ready(path, "/two");
            leaving.Bridge.Dispose();
            await Ready(path, "/one");
            Equal((await IdeRequest(path, "/two"))["resultType"]!.GetValue<string>(), "error", "the departed provider no longer answers");

            // Unsafe directories and non-socket files are preserved.
            directory = ShortTemp("tr-ide-");
            path = Path.Combine(directory, "ipc.sock");
            File.WriteAllText(path, "keep this file");
            var blocked = Provider(path, "/one");
            await Until(() => Task.FromResult(blocked.Warnings.Count > 0), "a warning for a file in the way");
            Equal(File.ReadAllText(path), "keep this file", "the file is kept");
            blocked.Bridge.Dispose();
            File.Delete(path);
            File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
            var unsafeDirectory = Provider(path, "/two");
            await Until(() => Task.FromResult(unsafeDirectory.Warnings.Count > 0), "a warning for an unsafe directory");
            Require(unsafeDirectory.Warnings[0].Message.Contains("not writable by others", StringComparison.Ordinal), "the unsafe directory is named");
            unsafeDirectory.Bridge.Dispose();

            // Oversized and malformed frames close only the offending connection.
            directory = ShortTemp("tr-ide-");
            path = Path.Combine(directory, "ipc.sock");
            Provider(path, "/one");
            await Ready(path, "/one");
            foreach (var data in new[] { new byte[] { 0, 0, 0, 127 }, Frame(new { type = "request", requestId = 42 }) })
            {
                using var socket = await Client(path);
                await socket.SendAsync(data);
                var buffer = new byte[16];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Equal(await socket.ReceiveAsync(buffer, timeout.Token), 0, "the offending connection is closed");
            }
            await Ready(path, "/one");

            // Disposal during connection startup creates no listener afterwards.
            directory = ShortTemp("tr-ide-");
            path = Path.Combine(directory, "ipc.sock");
            Provider(path, "/one").Bridge.Dispose();
            await Task.Delay(1100);
            Require(!File.Exists(path), "no listener after disposal");

            // An existing router discovers the provider and gets explicit version errors.
            directory = ShortTemp("tr-ide-");
            path = Path.Combine(directory, "ipc.sock");
            using (var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                server.Bind(new UnixDomainSocketEndPoint(path));
                server.Listen(4);
                var incoming = server.AcceptAsync();
                var guest = Provider(path, "/one");
                using var socket = await incoming;
                var initialize = await Response(socket);
                Require(initialize["method"]!.GetValue<string>() == "initialize" && initialize["params"]!["clientType"]!.GetValue<string>() == "vscode", "the provider initializes as an editor client");
                await socket.SendAsync(Frame(new { type = "response", requestId = initialize["requestId"]!.GetValue<string>(), resultType = "success", method = "initialize", result = new { clientId = "editor" } }));
                await socket.SendAsync(Frame(new { type = "client-discovery-request", requestId = "discovery", request = new { method = "ide-context", version = 0, @params = new { workspaceRoot = "/one" } } }));
                var discovery = await Response(socket);
                Require(discovery["type"]!.GetValue<string>() == "client-discovery-response" && discovery["response"]!["canHandle"]!.GetValue<bool>(), "discovery answers for its workspace");
                await socket.SendAsync(Frame(new { type = "request", requestId = "future", method = "ide-context", version = 1, @params = new { workspaceRoot = "/one" } }));
                var rejected = await Response(socket);
                Require(rejected["resultType"]!.GetValue<string>() == "error" && rejected["error"]!.GetValue<string>() == "request-version-mismatch", "an unknown version is refused");
                guest.Bridge.Dispose();
                Require(File.Exists(path), "an existing router's socket is never replaced");
            }

            // A crashed router's stale socket is recovered.
            directory = ShortTemp("tr-ide-");
            path = Path.Combine(directory, "ipc.sock");
            using (var crashed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                crashed.Bind(new UnixDomainSocketEndPoint(path));
                crashed.Listen(1);
                File.Move(path, path + ".stale");
            }
            File.Move(path + ".stale", path);
            Require(File.Exists(path), "the stale socket file remains");
            var recovered = Provider(path, "/one");
            await Ready(path, "/one");
            Require(recovered.Warnings.Count == 0, "recovery is silent: " + string.Join("; ", recovered.Warnings.Select(error => error.Message)));
        }
        finally
        {
            foreach (var item in cleanup) item.Dispose();
        }
    }

    private static void Store()
    {
        var store = new CodexStore();
        store.Apply(new("w", "t", CodexStatus.Running, "PostToolUse") { Usage = new(10, 2, 5, 0), Plan = [new("Fix it", CodexPlanStatus.InProgress)] });
        store.Apply(new("w", "t", CodexStatus.Done, "Stop"));
        Require(store.Session("w", "t") is { Status: CodexStatus.Done, Usage: { Input: 10 }, Plan.Count: 1 }, "totals and the plan survive a push without them");
        store.Apply(new("w", "t", CodexStatus.Running, "PostToolUse") { Plan = [] });
        Require(store.Session("w", "t")!.Plan!.Count == 0, "an empty plan replaces the last one");
    }

    // Codex's /model flow as its TUI draws it: numbered › rows, an effort list, s for this session.
    private sealed class FakeTui(bool quick = false, string draft = "", bool sessionKey = true) : ICodexTerminalIo
    {
        private static readonly (string Name, int Efforts)[] Models = [("GPT-6-Astra (default)", 3), ("GPT-5.6 Sol", 3), ("GPT-5.6 Terra", 1), ("GPT-5.6 Luna (current)", 3)];
        private string view = "composer";
        private int highlighted = 3;
        private string typed = draft;
        public readonly List<string> Written = [];
        public string? Outcome;

        private IEnumerable<string> Rows(IEnumerable<string> names) => names.Select((name, index) => $"{(index == highlighted ? "›" : " ")} {index + 1}. {name}  A description");

        public IReadOnlyList<string> ReadLines(bool omitFaint = false) => view switch
        {
            "quick" => ["  Select Model", .. Rows(["codex-auto-fast", "All models"])],
            "models" => ["  Select Model and Effort", .. Rows(Models.Select(model => model.Name)), "  esc back"],
            "effort" => ["  Select Reasoning Level for x", .. Rows(["Low", "Medium (default)", "High"])],
            _ => [.. Outcome is null ? Array.Empty<string>() : [$"• {Outcome}"], $"› {(typed.Length > 0 ? typed : "Ask Codex to do anything")}"]
        };

        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;

        public void Write(string data)
        {
            Written.Add(data);
            if (view == "composer")
            {
                if (data == "\r" && typed == "/model") { view = quick ? "quick" : "models"; highlighted = quick ? 0 : 3; }
                else if (data != "\r") typed += data;
                return;
            }
            var size = view == "quick" ? 2 : view == "effort" ? 3 : Models.Length;
            if (data == "\x1b[B") highlighted = (highlighted + 1) % size;
            else if (data == "\x1b") view = view == "models" && quick ? "quick" : "composer";
            else if (data == "\r" && view == "quick" && highlighted == 1) { view = "models"; highlighted = 3; }
            else if (data == "\r" && view == "models")
            {
                if (Models[highlighted].Efforts > 1) { Outcome = Models[highlighted].Name; view = "effort"; highlighted = 1; }
                else { Outcome = "saved as default"; view = "composer"; }
            }
            else if (data == "s" && !sessionKey) { }
            else if (data == "s" && view == "models" && Models[highlighted].Efforts == 1) { Outcome = $"Model changed to {Models[highlighted].Name} for this session only"; view = "composer"; }
            else if (data == "s" && view == "effort") { Outcome = $"Model changed to {Outcome} effort {highlighted} for this session only"; view = "composer"; }
            if (view == "composer") typed = "";
        }
    }

    private static async Task ModelPicker()
    {
        Equal(CodexModelPicker.PickerHighlight(["  1. GPT-6-Astra (default)  x", "› 2. GPT-5.6 Luna (current)  x"]), "GPT-5.6 Luna", "the › row's display name");
        Equal(CodexModelPicker.PickerHighlight(["› 1. All models"]), "All models", "All models");
        Equal(CodexModelPicker.PickerHighlight(["› Ask Codex to do anything", "› 1 thing"]), null, "nothing at the composer");
        Equal(CodexModelPicker.ComposerDraft(["› Ask Codex to do anything"]), null, "the placeholder is not a draft");
        Equal(CodexModelPicker.ComposerDraft(["› Ask a follow-up question"]), null, "the follow-up placeholder is not a draft");
        Equal(CodexModelPicker.ComposerDraft(["› fix the build"]), "fix the build", "typed text is a draft");
        Require(CodexModelPicker.HighlightNamesModel("GPT-5.6 Luna", "gpt-5.6-luna") && CodexModelPicker.HighlightNamesModel("GPT-6-Astra", "gpt-6-astra") &&
            !CodexModelPicker.HighlightNamesModel("GPT-5.6 Sol", "gpt-5.6-luna"), "a display name names its slug");

        var efforts = new FakeTui();
        Equal(await CodexModelPicker.DriveAsync(efforts, "gpt-5.6-sol"), CodexModelPickerOutcome.Switched, "a model with efforts switches");
        Equal(efforts.Outcome, "Model changed to GPT-5.6 Sol effort 1 for this session only", "at its highlighted effort, for this session");
        var single = new FakeTui();
        Equal(await CodexModelPicker.DriveAsync(single, "gpt-5.6-terra"), CodexModelPickerOutcome.Switched, "a single-effort model switches");
        Equal(single.Outcome, "Model changed to GPT-5.6 Terra for this session only", "with the session key on its own row, never Enter");
        var quick = new FakeTui(quick: true);
        Equal(await CodexModelPicker.DriveAsync(quick, "gpt-6-astra"), CodexModelPickerOutcome.Switched, "through All models");
        Require(quick.Outcome!.Contains("GPT-6-Astra", StringComparison.Ordinal), "the quick picker's model");
        var absent = new FakeTui(quick: true);
        Equal(await CodexModelPicker.DriveAsync(absent, "gpt-9"), CodexModelPickerOutcome.NotFound, "a model not offered");
        Equal(CodexModelPicker.PickerHighlight(absent.ReadLines()), null, "the picker is backed out of");
        var old = new FakeTui(sessionKey: false);
        Equal(await CodexModelPicker.DriveAsync(old, "gpt-5.6-sol"), CodexModelPickerOutcome.NoSessionKey, "an old Codex without the session key");
        Equal(old.Outcome, "GPT-5.6 Sol", "nothing was saved as the default");
        Equal(CodexModelPicker.PickerHighlight(old.ReadLines()), null, "and the picker is closed");
        var drafted = new FakeTui(draft: "half a thought");
        Equal(await CodexModelPicker.DriveAsync(drafted, "gpt-5.6-sol"), CodexModelPickerOutcome.Draft, "a draft is left alone");
        Equal(drafted.Written.Count, 0, "nothing was typed");
    }

    private static void EditorProjection()
    {
        var editor = new EditorRef("one", "workspace", "src/main.ts", EditorKind.File, true);
        var context = CodexEditorContext.Build([editor], editor, new(3, 2, 4, 7, "unsaved selection"));
        Require(context.OpenTabs.SequenceEqual([new CodexIdeFile("main.ts", "src/main.ts")]) &&
            context.ActiveFile == new CodexIdeActiveFile("main.ts", "src/main.ts", new(new(2, 1), new(3, 6)), "unsaved selection"),
            "unsaved selected text, converted to zero-based positions exactly once");
        var selection = new EditorSelection(1, 1, 1, 8, "private");
        Require(CodexEditorContext.Build([], editor, selection) is { OpenTabs.Count: 0, ActiveFile: null }, "a closed editor leaves no selection");
        var diff = editor with { Kind = EditorKind.Diff };
        Require(CodexEditorContext.Build([diff], diff, selection) is { OpenTabs.Count: 0, ActiveFile: null }, "a diff view is not context");
        var external = editor with { Path = "C:\\Users\\me\\AGENTS.md", Kind = EditorKind.ExternalFile };
        Require(CodexEditorContext.Build([external], external, null).ActiveFile == new CodexIdeActiveFile("AGENTS.md", external.Path, new(new(0, 0), new(0, 0)), ""),
            "an external path is kept and an unselected file starts at zero");
    }
}