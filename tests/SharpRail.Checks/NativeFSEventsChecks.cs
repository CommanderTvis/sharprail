using System.Net;
using System.Runtime.InteropServices;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.SpecDialect;

namespace SharpRail.Checks;

internal static class NativeFSEventsChecks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadSnapshot([Out] long[] values, int count);
    private static readonly ReadSnapshot? Probe = Load();
    internal sealed record State(long Created, long Released, long Live, long Running, long Peak, long FailedStarts, long Untracked);

    private static ReadSnapshot? Load()
    {
        if (Environment.GetEnvironmentVariable("SHARPRAIL_FSEVENTS_PROBE") is not { Length: > 0 } path) return null;
        var library = NativeLibrary.Load(path);
        return Marshal.GetDelegateForFunctionPointer<ReadSnapshot>(NativeLibrary.GetExport(library, "SharpRailFSEventsSnapshot"));
    }

    internal static State? Snapshot()
    {
        if (Probe is null) return null;
        var values = new long[7];
        Require(Probe(values, values.Length) == 1, "The native FSEvents probe rejected its snapshot ABI.");
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6]);
    }

    internal static async Task ProveInterception(string root)
    {
        if (Snapshot() is not { } before) return;
        var path = Path.Combine(root, "native-fsevents-control");
        Directory.CreateDirectory(path);
        using (var known = new FileSystemWatcher(path) { EnableRaisingEvents = true })
        {
            var active = Snapshot()!;
            Require(active.Created > before.Created && active.Live > before.Live && active.Running > before.Running,
                "DYLD interposition did not observe a known enabled FileSystemWatcher; native coverage is not proven.");
        }
        await Returned(before);
        Console.WriteLine("PASS native FSEvents interposition observes known FileSystemWatcher allocation, start and release");
    }

    internal static void Plateau(State? before)
    {
        if (before is null) return;
        var after = Snapshot()!;
        Require(after.Created == before.Created && after.Live == before.Live && after.Running == before.Running && after.Untracked == before.Untracked,
            "Repeated reads/reconnects must not allocate or retain additional native FSEventStreams.");
    }

    internal static async Task Returned(State? before)
    {
        if (before is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var after = Snapshot()!;
            Require(after.Created - after.Released == after.Live && after.Untracked == before.Untracked,
                "Native stream accounting must balance without untracked release/start/stop operations.");
            if (after.Live == before.Live && after.Running == before.Running) return;
            await Task.Delay(20, timeout.Token);
        }
    }

    internal static async Task Run(string root)
    {
        Require(Probe is not null && OperatingSystem.IsMacOS(), "Run scripts/check-fsevents.sh on macOS to inject the test-only native probe.");
        await WatcherHygieneChecks.Run(root);
        var baseline = Snapshot()!;
        using var git = new IsolatedGit(Path.Combine(root, "native-fsevents-git"));
        var project = IsolatedGit.Repository(Path.Combine(root, "native-fsevents-host"),
            ("SPEC.md", "---\nid: native-hygiene\ntype: module-design\n---\n# Native hygiene\n"),
            ("BLUEPRINT.md", "# Blueprint\n"));
        for (var cycle = 0; cycle < 3; cycle++)
        {
            await using (var server = RemoteServer.Create(project, IPAddress.Loopback, 0, "native-hygiene", Path.Combine(root, "native-fsevents-state-" + cycle)))
            {
                await server.StartAsync();
                var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var state = new RemoteStateAdapter(address, "native-hygiene");
                await state.ChangeAsync([HostStateChange.OpenProject(project)]);
                var hostBaseline = Snapshot()!;
                State? admitted = null;
                for (var reconnect = 0; reconnect < 8; reconnect++)
                {
                    using var plugins = new RemotePluginAdapter(address, "native-hygiene");
                    await plugins.CallAsync(new(SpecDialectContract.Id, SpecDialectContract.Graph.Name, new SpecGraphParams(project), "native-hygiene"));
                    await plugins.CallAsync(new("blueprint", BlueprintContract.Get.Name, new BlueprintScope(project), "native-hygiene"));
                    if (admitted is null)
                    {
                        admitted = Snapshot();
                        var workspace = WorkspaceWatches.Accounting().Roots.Single(entry => entry.Root == project);
                        Require(workspace.Instances.Count > 1 && workspace.Instances.All(instance => instance.Enabled) &&
                            workspace.Instances.Any(instance => instance.Path == Path.Combine(project, ".git")) &&
                            workspace.Instances.Any(instance => instance.Path == Path.Combine(project, ".git", "refs")),
                            "The real Git fixture must admit enabled workspace, Git metadata and recursive refs watchers.");
                        Require(admitted!.Live >= hostBaseline.Live + workspace.Instances.Count &&
                            admitted.Running >= hostBaseline.Running + workspace.Instances.Count,
                            "Native interception must include the Git metadata/refs streams, not only the workspace stream.");
                        Console.WriteLine($"PASS native Git watcher admission: cycle={cycle + 1}, managed-instances={workspace.Instances.Count}, native-live-added={admitted.Live - hostBaseline.Live}, native-running-added={admitted.Running - hostBaseline.Running}");
                    }
                    else Plateau(admitted);
                }
                await server.StopAsync();
            }
            await Returned(baseline);
        }
        var final = Snapshot()!;
        Console.WriteLine($"PASS native FSEvents host hygiene: 3 real Git host start/stop/dispose cycles, Git metadata/refs plateau across 24 RPC reconnects; live={final.Live}, running={final.Running}, baseline={baseline.Live}/{baseline.Running}, created={final.Created}, released={final.Released}, peak={final.Peak}, failed-starts={final.FailedStarts}, untracked={final.Untracked}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}