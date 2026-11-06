using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Core.Plugins.Isolation;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Isolation.UI;

namespace SharpRail.Checks;

/// <summary>
/// <c>--plugin-isolated-ui</c>: an external plugin with only a UI half, installed from disk (the outsider fixture), runs
/// in its own runner process behind the isolation seam (src/SharpRail.Plugins.Api/Security.SPEC.md). The host refuses it
/// without the seam, a runner or the sandbox the seam asks for; with them it starts one process, the app records what
/// the plugin registered, the plugin's settings round-trip through that process, no plugin assembly loads into this
/// process, disabling ends the process, a crash fails the plugin and retry starts a new one.
/// </summary>
internal static class PluginIsolatedUIChecks
{
    private const string Id = "outsider-ui";
    private const string Greeting = "Hello from outside (outsider)";

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        if (PluginChannelChecks.Runner() is not { } runner)
        {
            Console.WriteLine("SKIP plugin isolated UI: these checks run self-contained, and the framework-dependent runner needs a shared .NET runtime");
            return;
        }
        Task.Run(() => Refusals(Path.Combine(root, "isolated-refusals"), runner)).GetAwaiter().GetResult();
        Console.WriteLine("PASS plugin isolated UI refusals: no seam, no runner, a seam that needs a sandbox and a plugin with a host half each refuse an enabled external plugin with a plain reason and start nothing; a UI half that cannot load fails with the runner's reason");
        App(Path.Combine(root, "isolated-app"), runner);
        Console.WriteLine("PASS plugin isolated UI: installed from disk and enabled, its one runner process activates the UI half; the app's registry holds its side tool, settings section and file viewer, whose views show a placeholder; its settings round-trip through the process across restarts; no plugin assembly or load context is in this process; disabling ends the process, a crash fails the plugin with a reason, retry starts a new process, and shutdown leaves none");
    }

    /// <summary>The fixture's staged directory: the manifest, the UI assembly, its private dependency and its assets.</summary>
    private static string Staged => Path.Combine(AppContext.BaseDirectory, "plugin-fixture", Id);

    /// <summary>Copies the outsider fixture into a plugins root as <paramref name="id"/>, with its manifest changed if asked.</summary>
    internal static void Install(string pluginsRoot, string id = Id, Action<JsonObject>? change = null)
    {
        Require(File.Exists(Path.Combine(Staged, PluginManifest.FileName)), "The checks' build must stage the outsider fixture at " + Staged);
        var target = Path.Combine(pluginsRoot, id);
        foreach (var file in Directory.EnumerateFiles(Staged, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(Staged, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy, overwrite: true);
        }
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(target, PluginManifest.FileName)))!.AsObject();
        manifest["id"] = id;
        change?.Invoke(manifest);
        File.WriteAllText(Path.Combine(target, PluginManifest.FileName), manifest.ToJsonString());
    }

    internal static PluginIsolation Seam(PluginChannelChecks.RunnerLaunch runner) => new()
    {
        Runner = runner.Program,
        RunnerEnvironment = new Dictionary<string, string> { ["DOTNET_ROOT"] = runner.DotnetRoot },
        RequireSandbox = false,
        StartTimeout = PluginChannelChecks.Patience,
    };

    private static async Task Refusals(string directory, PluginChannelChecks.RunnerLaunch runner)
    {
        async Task<PluginRosterEntry> Enabled(string name, PluginIsolation? seam, string id)
        {
            var root = Path.Combine(directory, name);
            var plugins = Path.Combine(root, "plugins");
            Install(plugins);
            Install(plugins, "broken-ui", manifest => manifest["ui"] = "Missing.dll");
            PluginHostChecks.InstallExternal(plugins, "outsider");
            var state = new HostStateStore(root);
            await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = root, State = state, Builtins = [], Isolation = seam, Log = (_, _, _) => { } });
            await runtime.Start();
            await state.ChangeAsync([HostStateChange.PluginEnabled(id, true)]);
            await runtime.Schedule();
            Require(runtime.RunnerPid(id) is null, $"{name}: a refused or failed external plugin keeps no runner.");
            return (await runtime.ListAsync()).Single(entry => entry.Id == id);
        }

        Require(await Enabled("no-seam", null, Id) is { Status: PluginStatus.Refused, Reason: PluginHostChecks.ExternalRefusal },
            "Without the isolation seam an enabled external plugin is refused as before.");
        Require(await Enabled("no-runner", Seam(runner) with { Runner = null }, Id) is { Status: PluginStatus.Refused, Reason: "This build has no plugin runner." },
            "A seam without a runner refuses external plugins, saying so.");
        Require(await Enabled("sandbox", Seam(runner) with { RequireSandbox = true }, Id) is { Status: PluginStatus.Refused, Reason: "This host cannot sandbox external plugins yet." },
            "A seam that needs a sandbox refuses external plugins until one exists.");
        Require(await Enabled("host-half", Seam(runner), "outsider") is { Status: PluginStatus.Refused, Reason: "External plugins with a host half are not supported in this version." },
            "An external plugin with a host half is refused.");
        var broken = await Enabled("broken", Seam(runner), "broken-ui");
        Require(broken is { Status: PluginStatus.Failed, Reason: "The UI assembly Missing.dll does not exist." }, "A UI half the runner cannot load fails with its reason: " + broken.Reason);
    }

    private static void App(string root, PluginChannelChecks.RunnerLaunch runner)
    {
        var workspace = Path.Combine(root, "workspace");
        var profile = Path.Combine(root, "profile");
        Install(Path.Combine(profile, "plugins"));
        Directory.CreateDirectory(workspace);
        int pid;
        using (var app = new E2eWorkspace(workspace, profileRoot: profile, isolation: Seam(runner)))
        {
            var runtime = app.PluginRuntime!;
            var loader = app.Workbench.PluginLoader;
            var window = app.Window;
            E2eWorkspace.Until(() => loader.Registry.Roster.Any(entry => entry.Id == Id));
            Require(loader.Registry.Roster.Single(entry => entry.Id == Id) is { Origin: PluginOrigin.External, Status: PluginStatus.Disabled },
                "The outsider arrives disabled, as every external plugin does.");

            int Activate(int activations)
            {
                E2eWorkspace.Until(() => loader.Registry.Active.Contains(Id) && runtime.RunnerPid(Id) is not null);
                E2eWorkspace.Until(() => Settings(app) is { } settings && settings["activations"]?.GetValue<int>() == activations);
                return runtime.RunnerPid(Id)!.Value;
            }

            Change(app, HostStateChange.PluginEnabled(Id, true));
            pid = Activate(1);
            Require(Alive(pid), "The outsider runs in its own process.");
            Require(loader.Registry.SideTools.Any(row => row is { PluginId: Id, Value.Tool: "board" }) &&
                loader.Registry.SettingsSections.Any(row => row is { PluginId: Id, Value.Label: "Outsider" }) &&
                loader.Registry.FileViewers.Any(row => row.PluginId == Id),
                "The app records the side tool, settings section and file viewer the outsider registered in its process.");
            Require(Settings(app)!["greeting"]?.GetValue<string>() == Greeting, "The outsider read its asset through its private dependency and wrote it to its own settings.");
            E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "GestureToastMessage" && text.Text == Greeting + ". Activations: 1."));
            window.Layout.RestoreTool("plugin:" + Id + ":board");
            E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "IsolatedPluginPlaceholder" && text.Text == RemotePluginUIModule.Placeholder));
            Require(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name!.StartsWith("SharpRail.PluginFixture.Outsider", StringComparison.Ordinal)) &&
                !AssemblyLoadContext.All.Any(context => context.Name?.StartsWith("plugin:", StringComparison.Ordinal) == true),
                "No plugin assembly or load context is in the host's or the app's process.");

            // Disabling ends the process after the drain; enabling starts a new one, which reads the count back.
            Change(app, HostStateChange.PluginEnabled(Id, false));
            E2eWorkspace.Until(() => loader.Registry.Roster.Single(entry => entry.Id == Id).Status == PluginStatus.Disabled && !loader.Registry.Active.Contains(Id));
            E2eWorkspace.Until(() => !Alive(pid));
            Require(loader.Registry.SideTools.All(row => row.PluginId != Id), "Disabling removes what the outsider registered.");
            Change(app, HostStateChange.PluginEnabled(Id, true));
            var second = Activate(2);
            Require(second != pid, "Enabling again starts a new process.");

            // A crash fails the plugin with a reason, for this client too; retry starts a new process.
            Process.GetProcessById(second).Kill();
            E2eWorkspace.Until(() => loader.Registry.Roster.Single(entry => entry.Id == Id) is { Status: PluginStatus.Failed, Reason: { } reason } &&
                reason.StartsWith("The plugin's process", StringComparison.Ordinal) && !loader.Registry.Active.Contains(Id));
            Require(runtime.RunnerPid(Id) is null, "A failed plugin keeps no runner.");
            var retried = app.Workbench.Plugins!.RetryAsync(Id);
            E2eWorkspace.Until(() => retried.IsCompleted);
            pid = Activate(3);
            Require(pid != second && Alive(pid), "Retry starts a new process.");
        }
        Require(!Alive(pid), "Shutting the host down ends every runner.");
    }

    private static JsonObject? Settings(E2eWorkspace app) =>
        app.Workbench.State.Current.PluginSettings.TryGetValue(Id, out var space) && space.ValueKind == JsonValueKind.Object ? JsonObject.Create(space) : null;

    private static void Change(E2eWorkspace app, HostStateChange change)
    {
        var changed = app.Workbench.State.ChangeAsync(change);
        E2eWorkspace.Until(() => changed.IsCompleted);
        changed.GetAwaiter().GetResult();
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}