using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Checks;

/// <summary>
/// The host's plugin runtime: manifest intake, discovery, the registry and reconciler, activation ids, drain and
/// bounded disposal, dispatch, settings namespaces, tools, terminals, and the external fixture plugin installed
/// from disk and driven through the direct adapter and a real gRPC host.
/// </summary>
internal static class PluginHostChecks
{
    private sealed record Note(string Text);
    private sealed record EchoReply(string Text, string Greeting);
    private sealed record CounterReply(string Workspace, int Value);
    private sealed record ProbeSettings { public int Size { get; init; } = 3; }
    private sealed class EnabledSettings { public bool Enabled { get; init; } }

    private static readonly PluginMethod<Note, Note> Say = new("say");
    private static readonly PluginMethod<Note, Note> Other = new("other");
    private static readonly PluginChannel<Note> Notes = PluginChannel<Note>.Event("notes");

    private sealed class Module(PluginContract contract, Func<IPluginHostContext, ValueTask<PluginDisposer?>> activate) : PluginHostModule
    {
        public override PluginContract Contract => contract;
        public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context) => activate(context);
    }

    private static PluginContract Contract(string id, params PluginMethodSpec[] methods) => PluginContract.Create(id, 1, methods, [Notes]);

    private static Module Answering(string id, Action<IPluginHostContext>? also = null, PluginDisposer? disposer = null) => new(Contract(id, Say, Other), context =>
    {
        context.Method(Say, (note, _, _) => ValueTask.FromResult(note with { Text = id + ":" + note.Text }));
        also?.Invoke(context);
        return ValueTask.FromResult(disposer);
    });

    private static PluginManifest Manifest(string id, bool enabledByDefault = false, params PluginDependency[] dependsOn) =>
        new(id, id, "puzzle-2-line", "1.0.0", PluginApi.Generation, 1) { EnabledByDefault = enabledByDefault, DependsOn = dependsOn, Host = id + ".dll" };

    private sealed record Composed(PluginRuntime Runtime, HostStateStore State, List<string> Log) : IAsyncDisposable
    {
        public async Task Set(params HostStateChange[] changes)
        {
            await State.ChangeAsync(changes);
            await Runtime.Schedule();
        }

        public Task Enable(string id, bool enabled = true) => Set(HostStateChange.PluginEnabled(id, enabled));

        public async Task<PluginRosterEntry> Row(string id) => (await Runtime.ListAsync()).Single(entry => entry.Id == id);

        public async Task<object?> Call(string id, string method, object? parameters, string client = "client") =>
            await Runtime.CallAsync(new(id, method, parameters, client));

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    private static async Task<Composed> Compose(string directory, IReadOnlyList<(PluginManifest, PluginHostModule?)> builtins,
        Func<PluginHostSeams, PluginHostSeams>? adjust = null)
    {
        Directory.CreateDirectory(directory);
        var log = new List<string>();
        var state = new HostStateStore(directory);
        var seams = new PluginHostSeams
        {
            StateDirectory = directory,
            State = state,
            Builtins = builtins,
            Log = (scope, level, message) => { lock (log) log.Add($"{level} {scope}: {message}"); }
        };
        var runtime = new PluginRuntime(adjust?.Invoke(seams) ?? seams);
        await runtime.Start();
        return new(runtime, state, log);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<Task<bool>> condition, string message, int milliseconds = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + message);
            await Task.Delay(25);
        }
    }

    private static async Task<PluginCallException> Fails(Func<Task> call, string message)
    {
        try { await call(); }
        catch (PluginCallException error) { return error; }
        throw new InvalidOperationException(message);
    }

    // Collects pushes in the background, so a check can wait for one or for silence.
    private sealed class Pushes : IAsyncDisposable
    {
        private readonly Channel<object?> items = Channel.CreateUnbounded<object?>();
        private readonly CancellationTokenSource stop = new();
        private readonly Task pump;

        public Pushes(IAsyncEnumerable<object?> source) => pump = Task.Run(async () =>
        {
            try { await foreach (var item in source.WithCancellation(stop.Token)) items.Writer.TryWrite(item); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        });

        public async Task<object?> Next(string message, int milliseconds = 10000)
        {
            using var timeout = new CancellationTokenSource(milliseconds);
            try { return await items.Reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("No push arrived: " + message); }
        }

        public async Task<bool> Silent(int milliseconds = 300)
        {
            using var timeout = new CancellationTokenSource(milliseconds);
            try { await items.Reader.WaitToReadAsync(timeout.Token); return false; }
            catch (OperationCanceledException) { return true; }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            try { await pump; } catch (Exception) { }
        }
    }

    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "plugins-host");
        Require(PluginApi.Generation == 1, "PluginApi.Generation changed; a breaking change must bump it deliberately and update this pin.");
        ManifestIntake();
        Discovery(Path.Combine(directory, "discovery"));
        await Registry(Path.Combine(directory, "registry"));
        await Activation(Path.Combine(directory, "activation"));
        await Reconciler(Path.Combine(directory, "reconciler"));
        await Dispatch(Path.Combine(directory, "dispatch"));
        await Settings(Path.Combine(directory, "settings"));
        await Tools(Path.Combine(directory, "tools"));
        await Shutdown(Path.Combine(directory, "shutdown"));
        await External(Path.Combine(directory, "external"));
        await ExternalFiles(Path.Combine(directory, "external-files"));
        await Terminals(Path.Combine(directory, "terminals"));
        Console.WriteLine("PASS plugin host: generation pin, manifests, discovery, registry, activation ids, drain, reconciler, dispatch, settings, tools, terminals and the external fixture locally and over gRPC");
    }

    private static string FixtureSource()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "plugin-fixture", "fixture");
        Require(File.Exists(Path.Combine(source, PluginManifest.FileName)) && File.Exists(Path.Combine(source, "SharpRail.PluginFixture.Host.dll")),
            $"The fixture plugin is missing from {source}; build tests/SharpRail.PluginFixture.");
        return source;
    }

    internal static string Install(string pluginsRoot, string name = "fixture", Func<string, string>? manifest = null)
    {
        var target = Path.Combine(pluginsRoot, name);
        Directory.CreateDirectory(target);
        var source = FixtureSource();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        if (manifest is not null)
        {
            var path = Path.Combine(target, PluginManifest.FileName);
            File.WriteAllText(path, manifest(File.ReadAllText(path)));
        }
        return target;
    }

    private static void ManifestIntake()
    {
        const string valid = """{"id":"tidy","label":"Tidy","icon":"puzzle-2-line","version":"1.0.0","apiGeneration":1,"wireVersion":1}""";
        var (manifest, refused) = PluginDiscovery.ReadManifest(valid, "tidy");
        Require(manifest is { Id: "tidy", WireVersion: 1, EnabledByDefault: false } && refused is null, "A well-formed manifest is accepted: " + refused);
        var withTool = valid.Replace("}", ""","contributes":{"sideTools":[{"tool":"t","label":"T","icon":"i","defaultSide":"left","requiresGit":true}],"fileViewers":[]}}""");
        Require(PluginDiscovery.ReadManifest(withTool, "tidy").Manifest?.Contributes.SideTools.Single().RequiresGit == true, "A side tool may declare requiresGit.");
        var unknown = PluginDiscovery.ReadManifest(valid.Replace("}", ""","bogus":1}"""), "tidy").Refused;
        Require(unknown is not null && unknown.Contains("bogus", StringComparison.Ordinal), "An unknown manifest member is refused, naming its path: " + unknown);
        var mistyped = PluginDiscovery.ReadManifest(valid.Replace("\"wireVersion\":1", "\"wireVersion\":\"one\""), "tidy").Refused;
        Require(mistyped is not null && mistyped.Contains("$.wireVersion", StringComparison.Ordinal), "A mistyped member is refused naming its path: " + mistyped);
        var missing = PluginDiscovery.ReadManifest("""{"id":"tidy","icon":"i","version":"1","apiGeneration":1,"wireVersion":1}""", "tidy").Refused;
        Require(missing is not null && missing.Contains("label", StringComparison.OrdinalIgnoreCase), "A missing required member is refused: " + missing);
        var moved = PluginDiscovery.ReadManifest(valid, "elsewhere").Refused;
        Require(moved == "plugin id \"tidy\" does not match its directory \"elsewhere\"", "An id must equal its directory name: " + moved);
        var generation = PluginDiscovery.ReadManifest(valid.Replace("\"apiGeneration\":1", "\"apiGeneration\":2"), "tidy").Refused;
        Require(generation == "plugin tidy declares apiGeneration 2, host expects 1", "A generation mismatch names the plugin and both generations: " + generation);
        var id = PluginDiscovery.ReadManifest(valid.Replace("\"tidy\"", "\"Not An Id\""), null).Refused;
        Require(id is not null && id.Contains("not a plugin id", StringComparison.Ordinal), "A malformed id is refused: " + id);
        Console.WriteLine("PASS plugin manifests: strict shape naming the path, id against directory, generation naming both numbers");
    }

    private static void Discovery(string directory)
    {
        var root = Path.Combine(directory, "plugins");
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        File.WriteAllText(Path.Combine(root, "broken", PluginManifest.FileName), "{ not json");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Install(root);
        var outside = Path.Combine(directory, "outside");
        Install(directory, "outside");
        File.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        File.WriteAllText(Path.Combine(root, "loose-file"), "");
        var found = PluginDiscovery.Discover([root, Path.Combine(directory, "missing-root")]).ToDictionary(item => Path.GetFileName(item.Directory));
        Require(found["fixture"].Manifest?.Id == "fixture", "A valid manifest under a directory named after its id is discovered.");
        Require(found["broken"].Refused?.StartsWith("invalid manifest:", StringComparison.Ordinal) == true, "An unreadable manifest is refused, not skipped: " + found["broken"].Refused);
        Require(found["empty"].Refused?.StartsWith("cannot read sharprail-plugin.json", StringComparison.Ordinal) == true, "A directory with no manifest is refused, not skipped.");
        Require(found["linked"].Refused?.Contains("symbolic link", StringComparison.Ordinal) == true && found["linked"].Manifest is null,
            "A symlinked plugin directory is refused rather than followed.");
        Require(!found.ContainsKey("loose-file") && found.Count == 4, "Only directories are plugins, and a missing root is skipped.");
        Console.WriteLine("PASS plugin discovery: valid, unreadable and missing manifests, a missing root, a symlinked directory refused");
    }

    private static async Task Registry(string directory)
    {
        var order = new List<string>();
        Module Recording(string id) => new(Contract(id, Say), _ => { lock (order) order.Add(id); return ValueTask.FromResult<PluginDisposer?>(null); });
        var ghost = new PluginMethod<Note, Note>("ghost");
        await using var composed = await Compose(directory, [
            (Manifest("leaf", true, new PluginDependency("trunk", 1)), Recording("leaf")),
            (Manifest("trunk", true), Recording("trunk")),
            (Manifest("loop-a", true, new PluginDependency("loop-b", 1)), Recording("loop-a")),
            (Manifest("loop-b", true, new PluginDependency("loop-a", 1)), Recording("loop-b")),
            (Manifest("ghostly", true), new Module(PluginContract.Create("ghostly", 1, [Say], [PluginChannel<Note>.State("state", ghost)]), _ => ValueTask.FromResult<PluginDisposer?>(null))),
            (Manifest("twice", true), new Module(PluginContract.Create("twice", 1, [Say, Say], []), _ => ValueTask.FromResult<PluginDisposer?>(null))),
            (Manifest("owns-enabled", true), new Module(PluginContract.Create<EnabledSettings>("owns-enabled", 1, [], []), _ => ValueTask.FromResult<PluginDisposer?>(null))),
            (Manifest("misnamed", true), new Module(Contract("someone-else", Say), _ => ValueTask.FromResult<PluginDisposer?>(null)))
        ]);
        await composed.Runtime.Schedule();
        Require(order.SequenceEqual(["trunk", "leaf"]), "Activation runs dependency-first: " + string.Join(",", order));
        var roster = (await composed.Runtime.ListAsync()).ToDictionary(entry => entry.Id);
        Require(roster["leaf"] is { Status: PluginStatus.Active, DependsOn: ["trunk"] } && roster["trunk"].Origin == PluginOrigin.Builtin,
            "The roster reflects registered plugins, their dependencies and state.");
        Require(roster["loop-a"] is { Status: PluginStatus.Refused, Reason: "part of a dependency cycle" } && roster["loop-b"].Status == PluginStatus.Refused,
            "A dependency cycle refuses every plugin in it.");
        Require(roster["ghostly"].Status == PluginStatus.Refused && roster["ghostly"].Reason!.Contains("unknown snapshot method \"ghost\"", StringComparison.Ordinal),
            "A state channel naming an undeclared snapshot method is refused: " + roster["ghostly"].Reason);
        Require(roster["twice"].Status == PluginStatus.Refused && roster["twice"].Reason!.Contains("twice", StringComparison.Ordinal), "A duplicate method is refused.");
        Require(roster["owns-enabled"].Status == PluginStatus.Refused && roster["owns-enabled"].Reason!.Contains("enabled", StringComparison.Ordinal),
            "A settings type declaring enabled is refused.");
        Require(roster["misnamed"].Status == PluginStatus.Refused, "A contract for another id is refused.");
        Require(roster["trunk"].Channels.ContainsKey("notes") && roster["trunk"].Channels["notes"].Kind == PluginChannelKind.Event,
            "The roster carries the loaded contract's channels.");
        Require(composed.State.Current.Plugins.Count == roster.Count, "The roster rides the host state snapshot.");
        Console.WriteLine("PASS plugin registry: dependency order, cycle refusal, contract-intake refusals, channels on the roster");
    }

    private static async Task Activation(string directory)
    {
        IPluginHostContext? kept = null;
        var activations = 0;
        var disposed = 0;
        var lifecycle = new Module(Contract("stale", Say, Other), context =>
        {
            kept = context;
            activations++;
            context.Method(Say, (note, _, _) => ValueTask.FromResult(note));
            return ValueTask.FromResult<PluginDisposer?>(() => { disposed++; return ValueTask.CompletedTask; });
        });
        var attempts = 0;
        var flaky = new Module(Contract("flaky", Say), context =>
        {
            if (++attempts == 1) throw new InvalidOperationException("the first activation fails");
            context.Method(Say, (note, _, _) => ValueTask.FromResult(note));
            return ValueTask.FromResult<PluginDisposer?>(null);
        });
        var release = new TaskCompletionSource();
        var steps = new List<string>();
        var slow = Answering("slow", context => context.Method(Other, async (note, _, _) =>
        {
            await release.Task;
            lock (steps) steps.Add("call finished");
            return note;
        }), () => { lock (steps) steps.Add("disposed"); return ValueTask.CompletedTask; });
        await using var composed = await Compose(directory, [(Manifest("stale"), lifecycle), (Manifest("flaky"), flaky), (Manifest("slow"), slow)]);

        await composed.Enable("stale");
        Require((await composed.Row("stale")).Status == PluginStatus.Active && activations == 1, "Enabling activates the plugin.");
        await using (var pushes = new Pushes(composed.Runtime.SubscribeAsync(new("stale", "notes", null, "client"))))
        {
            kept!.Publish(Notes, new Note("live"));
            Require(PluginJson.Convert<Note>(await pushes.Next("a live publish")).Text == "live", "A live activation's publish reaches subscribers.");
            var stale = kept;
            await composed.Enable("stale", false);
            Require(disposed == 1, "Disabling runs the disposer.");
            stale.Publish(Notes, new Note("late"));
            Require(await pushes.Silent(), "A publish from a disposed activation is dropped.");
            stale.Method(Other, (note, _, _) => ValueTask.FromResult(note));
            await composed.Enable("stale");
            Require(activations == 2, "Re-enabling activates again.");
            var late = await Fails(() => composed.Call("stale", "other", new Note("x")), "A method registered after its activation ended must not answer.");
            Require(late.Error == PluginCallError.Unknown, "A late registration never reaches the next activation's table.");
            stale.Publish(Notes, new Note("stale again"));
            Require(await pushes.Silent(), "The staleness check is a live compare: an old context stays dead after re-enabling.");
            kept!.Publish(Notes, new Note("renewed"));
            Require(PluginJson.Convert<Note>(await pushes.Next("a publish after re-enabling")).Text == "renewed", "A subscription survives disable and enable.");
        }

        await composed.Enable("flaky");
        var failed = await composed.Row("flaky");
        Require(failed is { Status: PluginStatus.Failed, Reason: "the first activation fails" }, "A throwing activation fails the plugin with its message.");
        await composed.Runtime.Schedule();
        Require(attempts == 1 && (await composed.Row("flaky")).Status == PluginStatus.Failed, "A failed plugin is not retried by a later reconcile.");
        await composed.Runtime.RetryAsync("flaky");
        Require(attempts == 2 && (await composed.Row("flaky")).Status == PluginStatus.Active, "Retry moves a failed plugin back and activates it again.");

        await composed.Enable("slow");
        var call = composed.Call("slow", "other", new Note("held"));
        await Until(() => Task.FromResult(!call.IsCompleted), "the held call");
        await Task.Delay(50);
        var turningOff = composed.Enable("slow", false);
        await Until(async () => (await composed.Row("slow")).Status == PluginStatus.Disabled, "routing to stop at once");
        var refused = await Fails(() => composed.Call("slow", "say", new Note("new")), "A disabled plugin must not answer.");
        Require(refused.Error == PluginCallError.Disabled, "Routing stops before the drain: a new call answers disabled.");
        await Task.Delay(100);
        lock (steps) Require(steps.Count == 0, "The disposer waits for the in-flight call to drain.");
        release.SetResult();
        await call;
        await turningOff;
        lock (steps) Require(steps.SequenceEqual(["call finished", "disposed"]), "Drain completes before dispose: " + string.Join(",", steps));
        Console.WriteLine("PASS plugin activation: lifecycle, a throwing activation failing, retry, both staleness cases, drain before dispose");

        var stuck = new TaskCompletionSource();
        var wedged = Answering("wedged", context => context.Method(Other, async (note, _, _) => { await stuck.Task; return note; }),
            async () => await stuck.Task);
        await using var bounded = await Compose(directory + "-bounded", [(Manifest("wedged"), wedged)],
            seams => seams with { DrainTimeout = TimeSpan.FromMilliseconds(100), DisposeTimeout = TimeSpan.FromMilliseconds(100) });
        await bounded.Enable("wedged");
        _ = bounded.Call("wedged", "other", new Note("never"));
        await Task.Delay(50);
        var started = DateTime.UtcNow;
        await bounded.Enable("wedged", false);
        Require(DateTime.UtcNow - started < TimeSpan.FromSeconds(3) && (await bounded.Row("wedged")).Status == PluginStatus.Disabled,
            "A drain that never empties and a disposer that never settles are both bounded.");
        lock (bounded.Log)
            Require(bounded.Log.Any(line => line.Contains("in flight", StringComparison.Ordinal)) && bounded.Log.Any(line => line.Contains("did not settle", StringComparison.Ordinal)),
                "Both timeouts log a warning.");
        stuck.SetResult();
        Console.WriteLine("PASS plugin activation: drain and disposer timeouts are bounded and logged");
    }

    private static async Task Reconciler(string directory)
    {
        var baseAttempts = 0;
        var throwing = new Module(Contract("base-failing", Say), _ =>
        {
            baseAttempts++;
            throw new InvalidOperationException("base cannot start");
        });
        var gate = new TaskCompletionSource();
        var gated = new Module(Contract("gated", Say), async _ => { await gate.Task; return null; });
        await using var composed = await Compose(directory, [
            (Manifest("defaulted", true), Answering("defaulted")),
            (Manifest("trunk"), Answering("trunk")),
            (Manifest("branch", false, new PluginDependency("trunk", 1)), Answering("branch")),
            (Manifest("twig", false, new PluginDependency("branch", 1)), Answering("twig")),
            (Manifest("bystander"), Answering("bystander")),
            (Manifest("base-failing"), throwing),
            (Manifest("leans", false, new PluginDependency("base-failing", 1)), Answering("leans")),
            (Manifest("needs-absent", false, new PluginDependency("absent", 1)), Answering("needs-absent")),
            (Manifest("needs-v2", false, new PluginDependency("trunk", 2)), Answering("needs-v2")),
            (Manifest("gated"), gated)
        ]);
        Require((await composed.Row("defaulted")).Status == PluginStatus.Active, "Start enables a builtin plugin whose manifest defaults it on.");
        await composed.Set(HostStateChange.PluginEnabled("trunk", true), HostStateChange.PluginEnabled("branch", true), HostStateChange.PluginEnabled("twig", true),
            HostStateChange.PluginEnabled("bystander", true));
        Require((await composed.Row("twig")).Status == PluginStatus.Active, "A dependency chain activates in one pass.");
        await composed.Enable("defaulted", false);
        Require((await composed.Row("defaulted")).Status == PluginStatus.Disabled, "Settings turn a plugin off.");

        await composed.Enable("trunk", false);
        var settings = composed.State.Current.PluginSettings;
        Require((await composed.Row("branch")).Status == PluginStatus.Disabled && (await composed.Row("twig")).Status == PluginStatus.Disabled,
            "Turning a dependency off turns off its transitive dependents.");
        Require(!settings["branch"].GetProperty("enabled").GetBoolean() && !settings["twig"].GetProperty("enabled").GetBoolean() &&
            settings["bystander"].GetProperty("enabled").GetBoolean() && (await composed.Row("bystander")).Status == PluginStatus.Active,
            "The cascade writes only the dependents' namespaces.");
        await composed.Enable("branch");
        var blocked = await composed.Row("branch");
        Require(blocked is { Status: PluginStatus.Refused } && blocked.Reason == "depends on trunk, which is off",
            "A plugin enabled while its dependency is off is refused, naming it: " + blocked.Reason);
        await composed.Enable("trunk");
        Require((await composed.Row("branch")).Status == PluginStatus.Active && (await composed.Row("twig")).Status == PluginStatus.Disabled,
            "Turning a dependency back on brings back what is enabled, and nothing the cascade turned off.");

        await composed.Enable("needs-absent");
        Require((await composed.Row("needs-absent")).Reason == "depends on absent, which is not installed", "A missing dependency refuses the dependent, naming it.");
        await composed.Enable("needs-v2");
        Require((await composed.Row("needs-v2")).Reason == "depends on trunk at wire version 2, but it is at 1", "A dependency at another wire version is refused.");

        await composed.Set(HostStateChange.PluginEnabled("base-failing", true), HostStateChange.PluginEnabled("leans", true));
        var leans = await composed.Row("leans");
        Require((await composed.Row("base-failing")).Status == PluginStatus.Failed && leans.Status != PluginStatus.Active,
            "A dependent is not activated in the pass its dependency fails: " + leans.Status + " " + leans.Reason);
        Require(leans.Reason!.Contains("base-failing", StringComparison.Ordinal), "The dependent's row names the dependency that failed.");
        await composed.Runtime.Schedule();
        Require(baseAttempts == 1, "A failed plugin is not retried on its own.");

        await composed.State.ChangeAsync([HostStateChange.PluginEnabled("gated", true)]);
        var first = composed.Runtime.Schedule();
        var second = composed.Runtime.Schedule();
        var third = composed.Runtime.Schedule();
        Require(ReferenceEquals(first, second) && ReferenceEquals(second, third), "Overlapping schedules coalesce into the running task.");
        gate.SetResult();
        await third;
        Require((await composed.Row("gated")).Status == PluginStatus.Active, "The coalesced run converges.");
        Console.WriteLine("PASS plugin reconciler: default enable, settings disable, cascade, dependency refusals, failure in the same pass, coalescing");
    }

    private static async Task Dispatch(string directory)
    {
        await using var composed = await Compose(directory, [(Manifest("speaker"), Answering("speaker"))]);
        Require((await Fails(() => composed.Call("nobody", "say", new Note("x")), "An unknown plugin must fail.")).Error == PluginCallError.Unknown, "An unknown plugin answers Unknown.");
        Require((await Fails(() => composed.Call("speaker", "say", new Note("x")), "A disabled plugin must fail.")).Error == PluginCallError.Disabled, "A disabled plugin answers Disabled.");
        await composed.Enable("speaker");
        Require((await Fails(() => composed.Call("speaker", "shout", new Note("x")), "An unknown method must fail.")).Error == PluginCallError.Unknown, "An unknown method answers Unknown.");
        Require(PluginJson.Convert<Note>(await composed.Call("speaker", "say", new Note("typed"))).Text == "speaker:typed", "In process, params pass as the declared type.");
        Require(PluginJson.Convert<Note>(await composed.Call("speaker", "say", JsonSerializer.SerializeToElement(new { text = "json" }))).Text == "speaker:json",
            "A JSON element deserializes into the params type.");
        var invalid = await Fails(() => composed.Call("speaker", "say", JsonSerializer.SerializeToElement(new { text = 5 })), "Invalid params must fail.");
        Require(invalid.Error == PluginCallError.InvalidParams && invalid.Message.Contains("plugin.speaker.say", StringComparison.Ordinal) &&
            invalid.Message.Contains("$.text", StringComparison.Ordinal), "A validation error names the method and the path: " + invalid.Message);
        var extra = await Fails(() => composed.Call("speaker", "say", JsonSerializer.SerializeToElement(new { text = "a", loud = true })), "Unmapped members must fail.");
        Require(extra.Error == PluginCallError.InvalidParams, "Unknown members are refused.");
        Require((await Fails(() => composed.Call("speaker", "say", null), "Null params must fail.")).Error == PluginCallError.InvalidParams, "Missing params are refused.");
        Require((await Fails(async () => await composed.Runtime.SubscribeAsync(new("speaker", "nowhere", null, "client")).GetAsyncEnumerator().MoveNextAsync(),
            "An undeclared channel must fail.")).Error == PluginCallError.Unknown, "Subscribing to an undeclared channel answers Unknown.");
        Console.WriteLine("PASS plugin dispatch: unknown plugin and method, disabled, strict params naming the path");
    }

    private static async Task Settings(string directory)
    {
        var seen = new List<ProbeSettings>();
        var probe = new Module(PluginContract.Create<ProbeSettings>("probe", 1, [Say], []), context =>
        {
            context.OnSettings<ProbeSettings>(value => { lock (seen) seen.Add(value); });
            context.Method(Say, (note, _, _) => ValueTask.FromResult(note with { Text = context.Settings<ProbeSettings>().Size.ToString() }));
            return ValueTask.FromResult<PluginDisposer?>(null);
        });
        // Written before the contract loads, unvalidated; found invalid once it does.
        var store = new HostStateStore(directory);
        await store.ChangeAsync([HostStateChange.PluginSettings("probe", """{"enabled":true,"size":"big"}""")]);
        await using var composed = await Compose(directory, [(Manifest("probe"), probe)]);
        Require((await composed.Row("probe")).Status == PluginStatus.Active, "An enabled namespace activates its plugin.");
        var space = composed.State.Current.PluginSettings["probe"];
        Require(space.GetProperty("size").GetInt32() == 3 && space.GetProperty("enabled").GetBoolean(),
            "An invalid stored namespace falls back to defaults, keeping enabled: " + space);
        lock (composed.Log) Require(composed.Log.Count(line => line.Contains("using the defaults", StringComparison.Ordinal)) == 1, "The fallback warns once.");

        await composed.Set(HostStateChange.PluginSettings("probe", """{"size":7}"""));
        await composed.Set(HostStateChange.PluginSettings("unloaded", """{"anything":[1,2]}"""));
        var settings = composed.State.Current.PluginSettings;
        Require(settings["probe"].GetProperty("size").GetInt32() == 7 && settings["probe"].GetProperty("enabled").GetBoolean() &&
            settings["unloaded"].GetProperty("anything").GetArrayLength() == 2, "Two namespaces written separately both survive.");
        Require(PluginJson.Convert<Note>(await composed.Call("probe", "say", new Note(""))).Text == "7", "A host half reads its validated settings.");
        await Until(() => { lock (seen) return Task.FromResult(seen.Any(value => value.Size == 7)); }, "the settings observer");
        var revision = composed.State.Current.Revision;
        try
        {
            await composed.State.ChangeAsync([HostStateChange.Setting("theme", "light"), HostStateChange.PluginSettings("probe", """{"size":"huge"}""")]);
            throw new InvalidOperationException("An invalid namespace was accepted.");
        }
        catch (ArgumentException error) { Require(error.Message.Contains("$.size", StringComparison.Ordinal), "The refusal names the path: " + error.Message); }
        Require(composed.State.Current.Revision == revision && composed.State.Current.Settings.Theme != "light", "An invalid namespace rejects the whole batch.");
        await composed.Set(HostStateChange.PluginSettings("probe", null));
        Require(composed.State.Current.PluginSettings["probe"].EnumerateObject().Any() == false && (await composed.Row("probe")).Status == PluginStatus.Disabled,
            "An empty value resets the namespace, enablement included.");
        await composed.Set(HostStateChange.PluginEnabled("probe", true));
        Require(composed.State.Current.PluginSettings["probe"].GetProperty("size").GetInt32() == 3, "Defaults are filled alongside enabled.");
        try
        {
            await composed.State.ChangeAsync([HostStateChange.PluginPaths(["relative/path"])]);
            throw new InvalidOperationException("A relative plugin path was accepted.");
        }
        catch (ArgumentException) { }
        await composed.State.ChangeAsync([HostStateChange.PluginPaths([Path.Combine(directory, "extra")])]);
        Require(new HostStateStore(directory).Current is { PluginPaths: [var extraRoot] } persisted && extraRoot.EndsWith("extra", StringComparison.Ordinal) &&
            persisted.PluginSettings.ContainsKey("unloaded"), "Plugin roots and namespaces persist.");
        Console.WriteLine("PASS plugin settings: two namespaces survive, invalid refuses the batch, defaults beside enabled, invalid-on-load fallback, plugin roots");
    }

    private static async Task Tools(string directory)
    {
        var release = new TaskCompletionSource();
        var runs = 0;
        var disposedAt = DateTime.MaxValue;
        var tooling = new Module(Contract("tooling", Say), context =>
        {
            context.Tool(new PluginTool<Note>("tool_note", "Note", "Echoes a note.", async (note, call, _) =>
            {
                Interlocked.Increment(ref runs);
                if (note.Text == "hold") await release.Task;
                return new PluginToolResult($"{note.Text}@{call.WorkspaceId}");
            }));
            context.Tool(new PluginTool<Note>("spec_get", "Clash", "Takes core's name.", (_, _, _) => ValueTask.FromResult(new PluginToolResult(""))));
            return ValueTask.FromResult<PluginDisposer?>(() => { disposedAt = DateTime.UtcNow; return ValueTask.CompletedTask; });
        });
        await using var composed = await Compose(directory, [(Manifest("tooling"), tooling)]);
        Require(composed.Runtime.McpTools(null, directory).Count == 0, "A disabled plugin contributes no tools.");
        await composed.Enable("tooling");
        var tools = composed.Runtime.McpTools(new TerminalRef(directory, "tab"), directory);
        Require(tools.Select(tool => tool.Name).SequenceEqual(["tool_note"]), "A tool clashing with core's name is refused at registration.");
        lock (composed.Log) Require(composed.Log.Any(line => line.Contains("spec_get was refused", StringComparison.Ordinal)), "The clash is logged.");
        var tool = tools.Single();
        Require(tool.InputSchema["properties"]?["text"] is not null && tool.InputSchema["required"]?.AsArray().Any(item => item!.GetValue<string>() == "text") == true,
            "The input schema comes from the parameters record: " + tool.InputSchema.ToJsonString());
        var invalid = await tool.Call(new JsonObject { ["text"] = 5 }, CancellationToken.None);
        Require(invalid.Error && invalid.Text.Contains("$.text", StringComparison.Ordinal) && runs == 0, "Arguments are validated before the run: " + invalid.Text);
        var ok = await tool.Call(new JsonObject { ["text"] = "hi" }, CancellationToken.None);
        Require(!ok.Error && ok.Text == "hi@" + directory, "A tool runs with its terminal's workspace.");
        var held = tool.Call(new JsonObject { ["text"] = "hold" }, CancellationToken.None);
        await Until(() => Task.FromResult(runs == 2), "the held tool run");
        var turningOff = composed.Enable("tooling", false);
        await Task.Delay(100);
        Require(disposedAt == DateTime.MaxValue && composed.Runtime.McpTools(null, directory).Count == 0, "A running tool call counts toward the drain, and the tool leaves the table.");
        release.SetResult();
        Require((await held).Text == "hold@" + directory, "A tool call already running finishes.");
        await turningOff;
        Require(disposedAt != DateTime.MaxValue, "The disposer runs after the tool call finished.");

        var (status, body) = await McpServer.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""), directory,
            [new McpServer.McpTool("extra_tool", "Extra", "An added tool.", new JsonObject { ["type"] = "object" }, (_, _) => Task.FromResult(("ran", false)))]);
        Require(status == 200 && body!["result"]!["tools"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()).SequenceEqual(["spec_grep", "spec_get", "extra_tool"]),
            "Added tools are listed after core's.");
        Console.WriteLine("PASS plugin tools: schema from the record, arguments validated first, name clash refused, a running call drains before dispose");
    }

    private static async Task Shutdown(string directory)
    {
        var order = new List<string>();
        var dependencyAnswer = PluginCallError.Failed;
        var lower = Answering("lower", disposer: () => { lock (order) order.Add("lower"); return ValueTask.CompletedTask; });
        var upper = new Module(Contract("upper", Say), context =>
        {
            var handle = context.Dependency(lower.Contract);
            return ValueTask.FromResult<PluginDisposer?>(async () =>
            {
                lock (order) order.Add("upper");
                try { await handle.RequestAsync(Say, new Note("bye")); }
                catch (PluginCallException error) { dependencyAnswer = error.Error; }
            });
        });
        var composed = await Compose(directory, [(Manifest("lower", true), lower), (Manifest("upper", true, new PluginDependency("lower", 1)), upper)]);
        Require((await composed.Row("upper")).Status == PluginStatus.Active, "A dependent activates on its dependency.");
        await composed.Runtime.DisposeAsync();
        await Until(() => { lock (order) return Task.FromResult(order.Count == 2 && dependencyAnswer != PluginCallError.Failed); }, "both disposers");
        lock (order) Require(order.SequenceEqual(["upper", "lower"]), "Shutdown runs disposers dependents-first: " + string.Join(",", order));
        Require(dependencyAnswer == PluginCallError.Disabled, "Every plugin is already off when the first disposer runs.");
        Require(composed.State.Current.Plugins.All(entry => entry.Status != PluginStatus.Active), "Shutdown publishes the stopped roster.");
        Console.WriteLine("PASS plugin shutdown: every plugin stops before any disposer, dependents first");
    }

    private static async Task External(string directory)
    {
        var plugins = Path.Combine(directory, "local", "plugins");
        Install(plugins);
        await using var loopback = new LoopbackServer(null);
        await using var composed = await Compose(Path.Combine(directory, "local"), [], seams => seams with { PublicBaseUrl = () => loopback.BaseUrl });
        loopback.Plugins = composed.Runtime;
        await Exercise("local", new LocalPluginAdapter(composed.Runtime), new LocalStateAdapter(composed.State));

        Require(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == "SharpRail.Plugins.Api") == 1,
            "A shared assembly shipped in a plugin directory is not loaded a second time.");
        using (var http = new HttpClient())
        {
            var hello = await http.GetAsync(loopback.BaseUrl + "/plugin/fixture/hello");
            Require(hello.StatusCode == HttpStatusCode.OK && await hello.Content.ReadAsStringAsync() == "hello", "An active plugin's route is served on the loopback server.");
            Require((await http.GetAsync(loopback.BaseUrl + "/plugin/nobody/hello")).StatusCode == HttpStatusCode.NotFound, "An unknown plugin's route is 404.");
            await composed.Enable("fixture", false);
            Require((await http.GetAsync(loopback.BaseUrl + "/plugin/fixture/hello")).StatusCode == HttpStatusCode.NotFound, "A disabled plugin's route is 404.");
            await composed.Enable("fixture");
        }

        // An unchanged rescan leaves the active plugin alone; a changed manifest reloads it.
        var counter = PluginJson.Convert<CounterReply>(await composed.Call("fixture", "bump", new { workspace = "rescan" }));
        await composed.Runtime.RescanAsync();
        Require((await composed.Row("fixture")).Status == PluginStatus.Active, "Rescanning an unchanged plugin leaves it active.");
        var manifestPath = Path.Combine(plugins, "fixture", PluginManifest.FileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"1.0.0\"", "\"1.0.1\""));
        await composed.Runtime.RescanAsync();
        await Until(async () => (await composed.Row("fixture")) is { Status: PluginStatus.Active, Version: "1.0.1" }, "the changed manifest to reload");
        Require(PluginJson.Convert<CounterReply>(await composed.Call("fixture", "counter", new { workspace = "rescan" })).Value == counter.Value,
            "Plugin state survives replacing the plugin.");

        Install(plugins, "elder", text => text.Replace("\"fixture\"", "\"elder\"").Replace("\"apiGeneration\": 1", "\"apiGeneration\": 2"));
        Install(plugins, "needy", text => text.Replace("\"fixture\"", "\"needy\"").Replace("\"wireVersion\": 1,", "\"wireVersion\": 1, \"dependsOn\": [{ \"id\": \"absent\", \"wireVersion\": 1 }],"));
        await composed.Runtime.RescanAsync();
        var elder = await composed.Row("__refused:elder");
        Require(elder is { Status: PluginStatus.Refused, Reason: "plugin elder declares apiGeneration 2, host expects 1" }, "A plugin built for another generation is refused, naming both.");
        await composed.Enable("needy");
        Require((await composed.Row("needy")) is { Status: PluginStatus.Refused, Reason: "depends on absent, which is not installed" }, "An external plugin's missing dependency is refused.");
        var elderManifest = Path.Combine(plugins, "elder", PluginManifest.FileName);
        File.WriteAllText(elderManifest, File.ReadAllText(elderManifest).Replace("\"apiGeneration\": 2", "\"apiGeneration\": 1"));
        var roster = await composed.Runtime.RescanAsync();
        Require(roster.All(entry => entry.Id != "__refused:elder") && roster.Single(entry => entry.Id == "elder").Status == PluginStatus.Disabled,
            "A rescan promotes a fixed manifest out of refused, arriving disabled.");
        await composed.Enable("elder");
        Require((await composed.Row("elder")).Status == PluginStatus.Refused, "A host half whose contract names another id is refused.");
        Directory.Delete(Path.Combine(plugins, "elder"), recursive: true);
        Require((await composed.Runtime.RescanAsync()).All(entry => entry.Id != "elder"), "A plugin removed from disk leaves the roster on rescan.");

        var remoteState = Path.Combine(directory, "remote");
        Install(Path.Combine(remoteState, "plugins"));
        await using var server = RemoteServer.Create(directory, IPAddress.Loopback, 0, "plugin-token", remoteState);
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remotePlugins = new RemotePluginAdapter(address, "plugin-token");
            using var remoteState2 = new RemoteStateAdapter(address, "plugin-token");
            await Exercise("remote", remotePlugins, remoteState2);
            using var stranger = new RemotePluginAdapter(address, "wrong-token");
            try
            {
                await stranger.ListAsync();
                throw new InvalidOperationException("The plugin service accepted a bad token.");
            }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.Unauthenticated) { }
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS external fixture plugin: shared assemblies from the default context, route on the loopback server, rescan reload and promotion, generation and dependency refusals");
    }

    // The same scenario through the direct adapter and a gRPC host: install from disk arrives disabled, enable,
    // call, channel publish and snapshot, bad params naming the path, settings namespaces, disable, the file read.
    private static async Task Exercise(string mode, IPluginService plugins, IHostStateService state)
    {
        await Until(async () => (await plugins.ListAsync()).Any(entry => entry.Id == "fixture"), mode + " discovery");
        var row = (await plugins.ListAsync()).Single(entry => entry.Id == "fixture");
        Require(row is { Status: PluginStatus.Disabled, Origin: PluginOrigin.External, Ui: "SharpRail.PluginFixture.UI.dll", Assets: "assets", Label: "Fixture" } &&
            row.Contributes.SideTools.Single() is { Tool: "board", DefaultSide: PluginToolSide.Right } && row.Contributes.FileViewers.Single().Extensions.SequenceEqual(["fixture"]),
            $"{mode}: an installed external plugin arrives disabled with its manifest contributions.");
        Require((await state.GetStateAsync()).Plugins.Any(entry => entry.Id == "fixture"), $"{mode}: the roster rides the state snapshot.");
        Require((await state.GetStateAsync()).Platform is not null, $"{mode}: the snapshot names the host platform.");
        Require((await Fails(() => plugins.CallAsync(new("fixture", "echo", new { text = "x" }, "client")).AsTask(), "A disabled plugin must fail.")).Error == PluginCallError.Disabled,
            $"{mode}: a disabled plugin answers Disabled.");
        Require((await Fails(() => plugins.CallAsync(new("nobody", "echo", null, "client")).AsTask(), "An unknown plugin must fail.")).Error == PluginCallError.Unknown,
            $"{mode}: an unknown plugin answers Unknown.");

        await state.ChangeAsync([HostStateChange.PluginEnabled("fixture", true)]);
        await Until(async () => (await plugins.ListAsync()).Single(entry => entry.Id == "fixture").Status == PluginStatus.Active, mode + " activation");
        var active = (await state.GetStateAsync()).Plugins.Single(entry => entry.Id == "fixture");
        Require(active.Channels["counter"] is { Kind: PluginChannelKind.State, Snapshot: "counter", Key: ["workspace"] } && active.Channels["pings"].Kind == PluginChannelKind.Event,
            $"{mode}: an active plugin's channels reach the roster.");
        var echo = PluginJson.Convert<EchoReply>(await plugins.CallAsync(new("fixture", "echo", new { text = "hi" }, "client")));
        Require(echo == new EchoReply("hi", "hello"), $"{mode}: a method answers with default settings.");
        Require((await Fails(() => plugins.CallAsync(new("fixture", "shout", new { text = "x" }, "client")).AsTask(), "An unknown method must fail.")).Error == PluginCallError.Unknown,
            $"{mode}: an unknown method answers Unknown.");
        var invalid = await Fails(() => plugins.CallAsync(new("fixture", "echo", new { text = 5 }, "client")).AsTask(), "Bad params must fail.");
        Require(invalid.Error == PluginCallError.InvalidParams && invalid.Message.Contains("$.text", StringComparison.Ordinal), $"{mode}: bad params name the path: {invalid.Message}");

        await state.ChangeAsync([HostStateChange.PluginSettings("fixture", """{"greeting":"hey"}""")]);
        await state.ChangeAsync([HostStateChange.PluginSettings("neighbour", """{"kept":true}""")]);
        var spaces = (await state.GetStateAsync()).PluginSettings;
        Require(spaces["fixture"].GetProperty("greeting").GetString() == "hey" && spaces["fixture"].GetProperty("enabled").GetBoolean() &&
            spaces["neighbour"].GetProperty("kept").GetBoolean(), $"{mode}: two namespaces written separately both survive.");
        Require(PluginJson.Convert<EchoReply>(await plugins.CallAsync(new("fixture", "echo", new { text = "hi" }, "client"))).Greeting == "hey",
            $"{mode}: a settings change reaches the host half.");
        try
        {
            await state.ChangeAsync([HostStateChange.PluginSettings("fixture", """{"greeting":5}""")]);
            throw new InvalidOperationException($"{mode}: an invalid namespace was accepted.");
        }
        catch (Exception error) when (error is ArgumentException or Grpc.Core.RpcException) { }

        var workspace = "workspace-" + mode;
        await using (var mine = new Pushes(plugins.SubscribeAsync(new("fixture", "counter", new { workspace }, "client"))))
        await using (var others = new Pushes(plugins.SubscribeAsync(new("fixture", "counter", new { workspace = "elsewhere" }, "client"))))
        {
            CounterReply bumped = new("", 0);
            object? pushed = null;
            // A remote subscription registers when its stream starts; bump until it is listening.
            for (var attempt = 0; attempt < 40 && pushed is null; attempt++)
            {
                bumped = PluginJson.Convert<CounterReply>(await plugins.CallAsync(new("fixture", "bump", new { workspace }, "client")));
                try { pushed = await mine.Next("counter", 250); } catch (TimeoutException) { }
            }
            Require(pushed is not null && PluginJson.Convert<CounterReply>(pushed) == bumped, $"{mode}: a publish reaches the matching keyed subscription.");
            var snapshot = PluginJson.Convert<CounterReply>(await plugins.CallAsync(new("fixture", "counter", new { workspace }, "client")));
            Require(snapshot == bumped, $"{mode}: the snapshot method returns the published state.");
            Require(await others.Silent(), $"{mode}: a keyed publish skips a subscription for another key.");

            await state.ChangeAsync([HostStateChange.PluginEnabled("fixture", false)]);
            await Until(async () => (await plugins.ListAsync()).Single(entry => entry.Id == "fixture").Status == PluginStatus.Disabled, mode + " disable");
            Require((await Fails(() => plugins.CallAsync(new("fixture", "echo", new { text = "x" }, "client")).AsTask(), "A disabled plugin must fail.")).Error == PluginCallError.Disabled,
                $"{mode}: disabling stops dispatch.");
            await state.ChangeAsync([HostStateChange.PluginEnabled("fixture", true)]);
            await Until(async () => (await plugins.ListAsync()).Single(entry => entry.Id == "fixture").Status == PluginStatus.Active, mode + " re-enable");
            var again = PluginJson.Convert<CounterReply>(await plugins.CallAsync(new("fixture", "bump", new { workspace }, "client")));
            Require(PluginJson.Convert<CounterReply>(await mine.Next("after re-enabling")) == again && again.Value == bumped.Value + 1,
                $"{mode}: a subscription survives disable and enable, and state survives the toggle.");
        }

        using (var cancel = new CancellationTokenSource())
        {
            await using var stream = plugins.SubscribeAsync(new("fixture", "pings", null, "client"), cancel.Token).GetAsyncEnumerator(cancel.Token);
            var next = stream.MoveNextAsync().AsTask();
            await Task.Delay(100);
            await cancel.CancelAsync();
            var ended = false;
            try { await next; }
            catch (OperationCanceledException) { ended = true; }
            Require(ended, $"{mode}: cancelling a subscription ends it as cancelled, not as a dropped transport.");
        }

        var manifest = await plugins.ReadFileAsync("fixture", PluginManifest.FileName);
        Require(manifest is not null && Encoding.UTF8.GetString(manifest).Contains("\"fixture\"", StringComparison.Ordinal), $"{mode}: the plugin file read returns the file.");
        Require(await plugins.ReadFileAsync("fixture", "assets/icon.svg") is { Length: > 0 }, $"{mode}: assets are readable.");
        Require(await plugins.ReadFileAsync("fixture", "SharpRail.PluginFixture.Host.dll") is { Length: > 0 }, $"{mode}: the plugin's assemblies are readable.");
        Require(await plugins.ReadFileAsync("fixture", "../../state.json") is null && await plugins.ReadFileAsync("fixture", "/etc/hosts") is null,
            $"{mode}: the file read refuses paths escaping the plugin directory.");
        Require(await plugins.ReadFileAsync("fixture", "missing.txt") is null && await plugins.ReadFileAsync("nobody", "x") is null, $"{mode}: a missing file reads as null.");
        Require((await plugins.RescanAsync()).Single(entry => entry.Id == "fixture").Status == PluginStatus.Active, $"{mode}: rescan answers the roster.");
        Console.WriteLine($"PASS {mode} external fixture plugin: install from disk, enable, call, keyed channel and snapshot, strict params, namespaces, disable and re-enable, file read");
    }

    // An active plugin's exact absolute files read and save through the ordinary project service, locally and
    // remotely; nothing else outside the workspace does, and disabling the plugin withdraws them.
    private static async Task ExternalFiles(string directory)
    {
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        var exposed = Path.Combine(directory, "outside", "config.toml");
        var hidden = Path.Combine(directory, "outside", "secret.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(exposed)!);
        File.WriteAllText(exposed, "a = 1\n");
        File.WriteAllText(hidden, "b = 2\n");
        Module Exposer() => new(Contract("exposer", Say), context =>
        {
            context.ExternalFiles(root => root == Path.GetFullPath(workspace) ? [exposed] : []);
            return ValueTask.FromResult<PluginDisposer?>(null);
        });
        async Task Exercise(string mode, IProjectServices project, Func<bool, Task> enable)
        {
            await project.OpenProjectAsync(workspace);
            Require((await project.ReadFileAsync(exposed)).Text == "a = 1\n", $"{mode}: an exposed absolute file reads.");
            await project.SaveFileAsync(new(Path.GetFullPath(workspace), exposed, "a = 1\n", "a = 2\n"));
            Require(File.ReadAllText(exposed) == "a = 2\n", $"{mode}: an exposed absolute file saves.");
            File.WriteAllText(exposed, "a = 1\n");
            try
            {
                await project.ReadFileAsync(hidden);
                throw new InvalidOperationException($"{mode}: an unexposed absolute file was read.");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or Grpc.Core.RpcException) { }
            await enable(false);
            try
            {
                await project.ReadFileAsync(exposed);
                throw new InvalidOperationException($"{mode}: a disabled plugin's file was still readable.");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or Grpc.Core.RpcException) { }
        }

        await using (var composed = await Compose(Path.Combine(directory, "local"), [(Manifest("exposer", true), Exposer())]))
            await Exercise("local", new LocalProjectAdapter(new ProjectServices(workspace, composed.State, composed.Runtime.AllowsExternalFile)),
                enabled => composed.Enable("exposer", enabled));

        await using var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, "external-files", Path.Combine(directory, "remote"),
            plugins: seams => seams with { Builtins = [(Manifest("exposer", true), Exposer())] });
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var runtime = server.Services.GetRequiredService<PluginRuntime>();
            await Until(async () => (await runtime.ListAsync()).Single().Status == PluginStatus.Active, "the remote exposer");
            using var project = new RemoteProjectAdapter(address, "external-files");
            using var state = new RemoteStateAdapter(address, "external-files");
            await Exercise("remote", project, async enabled =>
            {
                await state.ChangeAsync([HostStateChange.PluginEnabled("exposer", enabled)]);
                await Until(async () => (await runtime.ListAsync()).Single().Status == (enabled ? PluginStatus.Active : PluginStatus.Disabled), "the remote toggle");
            });
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS plugin external files: exact absolute files read and save locally and over gRPC, others refused, withdrawn on disable");
    }

    private static async Task Terminals(string directory)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Console.WriteLine("SKIP plugin terminal checks: PTYs require macOS or Linux.");
            return;
        }
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);
        var events = new List<TerminalEvent>();
        var tab = new TerminalRef(Path.GetFullPath(workspace), "tab-1");
        var agent = new TerminalAgentRecord("probe-agent", "probe --resume") { SessionId = "s-1" };
        IPluginHostContext? context = null;
        Module Reviver() => new(Contract("reviver", Say), activation =>
        {
            context = activation;
            activation.TerminalEnvironment(terminal => new Dictionary<string, string> { ["SHARPRAIL_PROBE"] = terminal.TabKey });
            activation.RevivePrefill((terminal, record) => record.Kind == "probe-agent" ? new RevivePrefill(record.Command, Submit: true) : null);
            activation.OnTerminal(change => { lock (events) events.Add(change); });
            return ValueTask.FromResult<PluginDisposer?>(null);
        });

        await using var pty = new PtyTerminalService("/bin/sh");
        await using var loopback = new LoopbackServer(pty);
        await using var composed = await Compose(Path.Combine(directory, "local"), [(Manifest("reviver", true), Reviver())],
            seams => seams with { Terminals = pty, PublicBaseUrl = () => loopback.BaseUrl });
        loopback.Plugins = composed.Runtime;
        Install(Path.Combine(directory, "local", "plugins"));
        await composed.Runtime.RescanAsync();
        await composed.Enable("fixture");
        _ = loopback.BaseUrl;
        context!.SetAgentRecord(tab, agent);
        Require(composed.State.Current.TerminalAgents.Single() == new TerminalAgent(tab, agent) && context.AgentRecord(tab) == agent, "An agent record is stored on host state.");
        Require(new HostStateStore(Path.Combine(directory, "local")).Current.TerminalAgents.Count == 1, "Agent records persist.");

        var session = PtyTerminalService.SessionFor(tab);
        var attached = await pty.AttachAsync(new(session, workspace, "client", 100, 30) { TabKey = tab.TabKey });
        try
        {
            Require(attached.Prefill == new TerminalPrefill("probe --resume", true), "A shell starting for a tab with an agent record carries the revive prefill.");
            var text = new StringBuilder(Encoding.UTF8.GetString(attached.Replay.Span));
            var reading = Task.Run(async () =>
            {
                try { await foreach (var chunk in attached.ReadAsync()) lock (text) text.Append(Encoding.UTF8.GetString(chunk.Span)); }
                catch (Exception) { }
            });
            async Task<string> Await(string pattern)
            {
                await Until(() => { lock (text) return Task.FromResult(Regex.IsMatch(text.ToString(), pattern)); }, "terminal output " + pattern + ": " + text);
                lock (text) return Regex.Match(text.ToString(), pattern).Groups[1].Value;
            }
            await attached.WriteAsync("printf 'ENV_%s_%s_\\n' \"$SHARPRAIL_PROBE\" \"$SHARPRAIL_FIXTURE\"\r"u8.ToArray());
            await Await("ENV_tab-1_1_");
            context.WriteTerminal(tab, "printf 'HOST_%s_\\n' written\r");
            await Await("HOST_written_");
            await attached.WriteAsync("printf 'MCP_%s_\\n' \"$THINKRAIL_MCP_URL\"\r"u8.ToArray());
            var url = await Await(@"MCP_(http://127\.0\.0\.1:\d+/mcp/[0-9a-f]+)_");
            Require(context.TerminalForToken(url[(url.LastIndexOf('/') + 1)..]) == tab && context.TerminalToken(tab) == url[(url.LastIndexOf('/') + 1)..],
                "The terminal's MCP token resolves to its tab, and minting it again returns the same token.");
            using var http = new HttpClient();
            async Task<JsonNode?> Mcp(string method, object? parameters = null)
            {
                using var reply = await http.PostAsync(url, new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json"));
                return JsonNode.Parse(await reply.Content.ReadAsStringAsync());
            }
            var names = (await Mcp("tools/list"))!["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
            Require(names.Contains("spec_get") && names.Contains("fixture_echo"), "The terminal's MCP table lists core's tools and the active plugin's: " + string.Join(",", names));
            var echoed = await Mcp("tools/call", new { name = "fixture_echo", arguments = new { text = "through mcp" } });
            Require(echoed!["result"]!["content"]![0]!["text"]!.GetValue<string>() == "through mcp", "A plugin tool runs over the terminal's MCP route.");
            var wrong = await Mcp("tools/call", new { name = "fixture_echo", arguments = new { text = 5 } });
            Require(wrong!["result"]!["isError"]?.GetValue<bool>() == true && wrong["result"]!["content"]![0]!["text"]!.GetValue<string>().Contains("$.text", StringComparison.Ordinal),
                "Plugin tool arguments are validated, naming the path.");
            await attached.WriteAsync("printf 'PID_%s_\\n' \"$$\"\r"u8.ToArray());
            var pid = int.Parse(await Await(@"PID_(\d+)_"));
            Require(context.Terminals().Single(process => process.Terminal == tab).Pid == pid, "The process table lists the tab's shell.");
            Require(context.WorkspaceForProcess(pid) == tab.WorkspaceId, "A shell's pid maps to its workspace.");
            lock (events) Require(events.OfType<TerminalSpawned>().Any(spawned => spawned.Terminal == tab && spawned.Pid == pid), "Spawn is observed with the shell's pid.");
            lock (events) Require(events.OfType<TerminalAgentChanged>().Any(changed => changed.Record == agent), "Agent record changes are observed.");
        }
        finally { await attached.DisposeAsync(); }
        await pty.CloseAsync(session);
        Require(composed.State.Current.TerminalAgents.Count == 0, "Closing the tab drops its agent record.");
        await Until(() => { lock (events) return Task.FromResult(events.OfType<TerminalClosed>().Any(closed => closed.Terminal == tab)); }, "the close event");

        // Over gRPC the tab key and the prefill ride the attach.
        var remoteDirectory = Path.Combine(directory, "remote");
        await using var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, "terminal-plugins", remoteDirectory,
            plugins: seams => seams with { Builtins = [(Manifest("reviver", true), Reviver())] });
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            await Until(async () => (await server.Services.GetRequiredService<PluginRuntime>().ListAsync()).Single().Status == PluginStatus.Active, "the remote reviver");
            context!.SetAgentRecord(tab, agent);
            using var remoteState = new RemoteStateAdapter(address, "terminal-plugins");
            Require((await remoteState.GetStateAsync()).TerminalAgents.Single() == new TerminalAgent(tab, agent), "Agent records travel on the remote snapshot.");
            using var terminals = new RemoteTerminalAdapter(address, "terminal-plugins");
            var remote = await terminals.AttachAsync(new(session, workspace, "client") { TabKey = tab.TabKey });
            Require(remote.Prefill == new TerminalPrefill("probe --resume", true), "The prefill rides the remote attachment.");
            await remote.DisposeAsync();
            await terminals.CloseAsync(session);
            Require((await remoteState.GetStateAsync()).TerminalAgents.Count == 0, "Closing a remote tab drops its agent record.");
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS plugin terminals: environment contributors, revive prefill locally and over gRPC, host-side write, tokens, MCP plugin tools, process table, lifecycle, agent records");
    }
}
