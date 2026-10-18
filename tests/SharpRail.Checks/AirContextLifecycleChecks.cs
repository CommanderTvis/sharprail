using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Plugins.AirContext;
using SharpRail.Plugins.AirContext.Host;
using SharpRail.Plugins.Api;

namespace SharpRail.Checks;

internal static partial class AirContextChecks
{
    private static readonly string[] ToolNames = ["context_index", "context_search", "context_status"];

    /// <summary>Settings › Plugins is the one switch: disabled, Air Context serves nothing, prompts say nothing and nothing runs.</summary>
    internal static async Task Lifecycle(string root)
    {
        var storage = Path.Combine(root, "air-context-lifecycle");
        var workspace = Path.Combine(storage, "workspace"); Directory.CreateDirectory(workspace);
        using var handler = new Handler(_ => Task.FromResult("fixture"u8.ToArray()));
        using var http = new HttpClient(handler);
        var bundle = new ContextBundle("lifecycle", "fixture", new("https://download.jetbrains.com/fixture"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("fixture"u8)));
        using var fixtures = new Fixtures();
        var created = 0;
        WorkflowRuntime? latest = null;
        fixtures.Value.Consent = directory => new(directory, http, bundle);
        fixtures.Value.Runtime = (path, log, _) => { Interlocked.Increment(ref created); return Task.FromResult<IContextRuntime>(latest = new(path, log)); };
        await using var rig = await Rig.Open(workspace, Path.Combine(storage, "state"), remote: false, activate: false);
        // A fresh profile: the plugin is off, so nothing is served, said or shown until the user turns it on.
        Require((await rig.Plugins.ListAsync()).Single(entry => entry.Id == AirContextContract.Id).Status == PluginStatus.Disabled, "A fresh profile started Air Context.");
        Require(rig.Tools(workspace).Count == 0 && rig.Runtime.ToolGuidanceSections().Count == 0, "A fresh profile served Air Context tools or instructions.");
        Require(!SharpRail.Plugins.ClaudeCode.Host.SystemPrompt.Compose(rig.Runtime.ToolGuidanceSections(), []).Contains("Air Context", StringComparison.Ordinal), "A fresh profile mentioned Air Context in the instructions.");
        await rig.Enable(true);
        var review = await rig.Consent(new());
        await rig.Consent(new("acceptAndDownload", review.ArtifactId, review.TermsSha256));
        Require((await rig.Workspace(workspace, "enable")).Enabled, "Enabling the workspace failed.");

        var terminal = new TerminalRef(workspace, "lifecycle");
        string Claude(IReadOnlyList<string> guidance) => SharpRail.Plugins.ClaudeCode.Host.SystemPrompt.Compose(guidance, []);
        string Codex(IReadOnlyList<string> guidance) => SharpRail.Plugins.Codex.Host.CodexSystemPrompt.Compose(guidance, []);
        Require(rig.Tools(workspace).Select(tool => tool.Name).Order().SequenceEqual(ToolNames), "The enabled plugin did not serve its tools.");
        var guidance = rig.Runtime.ToolGuidanceSections();
        Require(guidance.Count == 1 && Claude(guidance).Contains("`context_search`", StringComparison.Ordinal) && Codex(guidance).Contains("Air Context (`jbcontext`)", StringComparison.Ordinal),
            "The enabled plugin's paragraph did not reach the agent instructions.");
        Require(Claude(guidance).Contains("Prefer available SharpRail MCP tools; disabled plugins may hide tools:\n\n- Air Context (`jbcontext`) provides", StringComparison.Ordinal),
            "The paragraph moved out of its place in the instructions.");
        var index = rig.Tools(workspace).Single(tool => tool.Name == "context_index");
        await index.Call(new(), default);
        await rig.UntilAsync(() => Task.FromResult(latest?.Indexes == 1), "indexing to start");

        await rig.Enable(false);
        Require(rig.Tools(workspace).Count == 0, "A disabled plugin still served tools.");
        Require(rig.Runtime.ToolGuidanceSections().Count == 0, "A disabled plugin still contributed instructions.");
        foreach (var prompt in new[] { Claude(rig.Runtime.ToolGuidanceSections()), Codex(rig.Runtime.ToolGuidanceSections()) })
            Require(!prompt.Contains("Air Context", StringComparison.Ordinal) && !prompt.Contains("context_search", StringComparison.Ordinal) && !prompt.Contains("jbcontext", StringComparison.Ordinal),
                "A disabled plugin left a trace in the agent instructions.");
        Require(latest!.Disposed, "Disabling the plugin did not stop its indexing.");
        var failure = await Fails(() => rig.Workspace(workspace));
        Require(failure.Error == PluginCallError.Disabled, "A disabled plugin answered a call.");
        var failedConsent = await Fails(() => rig.Consent(new()));
        Require(failedConsent.Error == PluginCallError.Disabled, "A disabled plugin answered a consent call.");
        var (requests, legal, runtimes) = (handler.Requests, handler.LegalRequests, created);
        rig.Runtime.SetAgentRecord(terminal, new("codex", "launched-while-disabled") { LaunchedByUi = true });
        await Task.Delay(300);
        Require(handler.Requests == requests && handler.LegalRequests == legal && created == runtimes, "A disabled plugin reached the network or started the CLI.");
        rig.Runtime.SetAgentRecord(terminal, null);

        await rig.Enable(true);
        Require(rig.Tools(workspace).Select(tool => tool.Name).Order().SequenceEqual(ToolNames), "Enabling the plugin did not bring its tools back.");
        Require(rig.Runtime.ToolGuidanceSections().Count == 1, "Enabling the plugin did not bring its instructions back.");
        Require((await rig.Workspace(workspace)).Enabled, "The user's workspace enablement did not survive the plugin being off.");
        Console.WriteLine("PASS Air Context lifecycle: disabled plugin serves no tools, adds no instructions, stops indexing and does nothing on agent launches; re-enabling restores them");
    }

    /// <summary>The state an installation had before Air Context became a plugin moves with it: consent, enablement and hiding.</summary>
    internal static async Task Migration(string root)
    {
        // A fresh profile gets the default (off), with no entry written.
        var fresh = Path.Combine(root, "air-context-migration", "fresh");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, HostStateStore.FileName), new JsonObject { ["Settings"] = new JsonObject { ["Theme"] = "kept" }, ["ContextWorkspaces"] = new JsonArray() }.ToJsonString());
        Require(!new HostStateStore(fresh).Current.PluginSettings.ContainsKey(AirContextContract.Id), "A profile that never used Air Context got an explicit setting.");
        await using (var rig = await Rig.Open(fresh, fresh, remote: false, activate: false))
            Require((await rig.Plugins.ListAsync()).Single(entry => entry.Id == AirContextContract.Id).Status == PluginStatus.Disabled, "A fresh profile started Air Context.");

        foreach (var hidden in new[] { true, false })
        {
            var directory = Path.Combine(root, "air-context-migration", hidden ? "hidden" : "used");
            var workspace = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspace);
            using var handler = new Handler(_ => Task.FromResult("fixture"u8.ToArray()));
            using var http = new HttpClient(handler);
            var bundle = new ContextBundle("migration", "fixture", new("https://download.jetbrains.com/fixture"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("fixture"u8)));
            var legacy = new ContextConsent(Path.Combine(directory, "context"), http, bundle);
            var terms = await legacy.ReviewAsync(default);
            var accepted = await legacy.ExecuteAsync(new("acceptAndDownload", terms.ArtifactId, terms.TermsSha256), default);
            Require(accepted.State == "downloaded", "The legacy fixture was not provisioned.");
            File.WriteAllText(Path.Combine(directory, HostStateStore.FileName), new JsonObject
            {
                ["Settings"] = new JsonObject { ["Theme"] = "kept", ["AirContextHidden"] = hidden },
                ["ContextWorkspaces"] = new JsonArray(workspace, "relative/not-absolute")
            }.ToJsonString());

            var store = new HostStateStore(directory);
            var has = store.Current.PluginSettings.TryGetValue(AirContextContract.Id, out var entry) && entry.TryGetProperty("enabled", out var flag);
            Require(has && entry.GetProperty("enabled").GetBoolean() == !hidden, "A hidden profile must be explicitly disabled and a used one explicitly enabled.");
            Require(store.Current.Settings.Theme == "kept", "The migration lost another setting.");
            var text = File.ReadAllText(Path.Combine(directory, HostStateStore.FileName));
            Require(!text.Contains("AirContextHidden", StringComparison.Ordinal) && !text.Contains("ContextWorkspaces", StringComparison.Ordinal), "The old keys stayed in the state file.");
            Require(!Directory.Exists(Path.Combine(directory, "context")) && Directory.Exists(Path.Combine(directory, "plugin-state", AirContextContract.Id, "files")),
                "The CLI storage did not move into the plugin's directory.");
            Require(Enabled(directory).SequenceEqual([workspace]), "The enabled workspaces did not move (or an invalid path did): " + string.Join(", ", Enabled(directory)));
            var moved = new ContextConsent(Path.Combine(directory, "plugin-state", AirContextContract.Id, "files"), http, bundle);
            var status = await moved.ExecuteAsync(new("status"), default);
            Require(status.State == "downloaded" && Path.GetFileName(status.Installation) == Path.GetFileName(accepted.Installation), "The download did not survive the move.");
            Require((await moved.ReviewAsync(default)).AcceptedAtUtc == accepted.AcceptedAtUtc, "The user's acknowledgement did not survive the move.");

            // A second start neither migrates again nor overrides what the user chose since.
            if (hidden) await store.ChangeAsync([HostStateChange.PluginEnabled(AirContextContract.Id, true)]);
            var reopened = new HostStateStore(directory);
            Require(reopened.Current.PluginSettings[AirContextContract.Id].GetProperty("enabled").GetBoolean(), "Reopening re-ran the migration.");

            using var fixtures = new Fixtures();
            fixtures.Value.Consent = path => new(path, http, bundle);
            await using var rig = await Rig.Open(workspace, directory, remote: false);
            Require((await rig.Consent(new("status"))).State == "downloaded" && (await rig.Workspace(workspace)).Enabled, "The plugin did not see the migrated consent and enablement.");
        }
        Console.WriteLine("PASS Air Context migration: fresh stays off, used becomes enabled, hidden becomes disabled once; consent receipt, download and enabled workspaces move into the plugin's state");
    }
}