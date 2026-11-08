using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Core.Plugins.Isolation;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Isolation;
using SharpRail.UI.Plugins;

namespace SharpRail.Checks;

/// <summary>
/// <c>--plugin-surface-remote</c>: an external plugin's views on remote clients over WebSocket RPC
/// (src/SharpRail.Plugins.Api/Security.SPEC.md, UI halves). A frame larger than a surface message is split under the
/// 1 MiB cap, and a client that leaves 1,024 of the plugin's calls unread loses its session at the next. Then two
/// headless workbenches, each behind its own relay, on one remote host with the isolation seam: both show the outsider
/// fixture's side tool with the plugin's own pixels from one runner process; one client's popups show and take input
/// over the session, and its scale 2 renders while the other stays at 1; a click in one updates the other's view;
/// one client's connection drops and the other keeps working; back within the grace, the dropped client resumes its
/// session (its typed note kept, no new activation) and repaints from a keyframe; a plugin that floods one client while
/// the app's UI thread is blocked ends that client's session with the reason, and the other keeps working; a client that
/// closes ends only its session; and past the grace a dropped client's session ends and it opens a new one.
/// </summary>
internal static class PluginSurfaceRemoteChecks
{
    private const string Id = "outsider-ui";
    private const string Token = "plugin-surface-remote";

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Split();
        Console.WriteLine($"PASS plugin surface remote split: a frame message of 3 MiB of tiles travels as surface messages of at most {PluginSurfaceWire.MessageBytes / 1024} KiB, its moves in the first, Last only on the last, the tiles and bytes in order, and every piece keeps to the client's frame caps");
        Task.Run(Unread).GetAwaiter().GetResult();
        Console.WriteLine($"PASS plugin surface remote unread: a remote client holds {RemoteIsolatedUISession.MostUnread} unread plugin calls, and the next one ends its session with the reason and abandons the calls left unread");
        if (PluginChannelChecks.Runner() is not { } runner)
        {
            Console.WriteLine("SKIP plugin surface remote: these checks run self-contained, and the framework-dependent runner needs a shared .NET runtime");
            return;
        }
        App(Path.Combine(root, "surface-remote"), runner);
    }

    private static void Split()
    {
        // 768 tiles of 4,000 bytes: about 3 MiB, as a 4K surface repainted at once.
        var tiles = Enumerable.Range(0, 768).Select(index => new SurfaceTile(index % 32 * 64, index / 32 * 64, 64, 64, TileCodecKind.Qoi, 4000)).ToList();
        var data = Enumerable.Range(0, tiles.Count * 4000).Select(index => (byte)(index * 31)).ToArray();
        var header = new SurfaceFrameHeader(1, 5, 2048, 1536, 1, 2048, 1536, true, [new(0, 0, 2048, 1536, 0, 64)], tiles);
        var pieces = HostedSurfaceSession.Split(9, new SurfaceFrame(header, data)).ToList();
        Require(pieces.Count >= 4, $"A 3 MiB frame message splits into several surface messages: {pieces.Count}.");
        Require(pieces.All(piece => RpcWire.Measure(piece) <= PluginSurfaceWire.MessageBytes), "Every surface message stays under 1 MiB.");
        Require(pieces[0].Frame!.Moves.Count == 1 && pieces.Skip(1).All(piece => piece.Frame!.Moves.Count == 0), "Moves travel in the first message only.");
        Require(pieces.Select(piece => piece.Frame!.Last).SequenceEqual(pieces.Select((_, index) => index == pieces.Count - 1)), "Only the last message is the frame's last.");
        Require(pieces.SelectMany(piece => piece.Frame!.Data).SequenceEqual(data) && pieces.Sum(piece => piece.Frame!.Tiles.Count) == tiles.Count &&
            pieces.All(piece => piece.Surface == 9 && piece.Frame!.Sequence == 5 && piece.Frame.Tiles.Sum(tile => tile.Length) == piece.Frame.Data.Length),
            "The pieces carry the tiles and their bytes in order.");
        // The client holds every piece to the frame caps again: they read as one frame, taken once acknowledged.
        var client = new SurfaceFrameState { Sequence = 4, Width = 2048, Height = 1536 };
        foreach (var piece in pieces)
        {
            var frame = piece.Frame!;
            var received = new SurfaceFrameHeader(9, frame.Sequence, frame.Width, frame.Height, frame.Scale, frame.DesiredWidth, frame.DesiredHeight, frame.Last,
                [.. frame.Moves.Select(move => new SurfaceMove(move.X, move.Y, move.Width, move.Height, move.Dx, move.Dy))],
                [.. frame.Tiles.Select(tile => new SurfaceTile(tile.X, tile.Y, tile.Width, tile.Height, (TileCodecKind)tile.Codec, tile.Length))], frame.Keyframe);
            Require(SurfaceFrames.Violation(received, frame.Data.Length, client) is null, "Every piece of a split frame keeps to the client's caps: " + SurfaceFrames.Violation(received, frame.Data.Length, client));
            client.Accept(received);
        }
        Require(client.Unacknowledged && client.Acknowledge(5), "The pieces end as one frame the client acknowledges.");
        var unfinished = HostedSurfaceSession.Split(9, new SurfaceFrame(header with { Last = false }, data)).ToList();
        Require(unfinished.All(piece => !piece.Frame!.Last), "A frame's message that was not its last stays unfinished in every piece.");
    }

    // A plugin that sends calls faster than the app reads them: the client keeps as many unread as the local host does.
    private static async Task Unread()
    {
        using var host = new RemoteIsolatedUIHost(new Uri("http://127.0.0.1:9/"), Token);
        var session = new RemoteIsolatedUISession(host);
        await using (session)
        {
            static PluginSurfaceDown Call(long seq, bool answered = false) =>
                new() { Kind = PluginSurfaceDownKind.UiCall, Seq = seq, Call = seq, Member = 1, Payload = "{}", Answered = answered };
            session.Receive(Call(1, answered: true));
            for (var seq = 2; seq <= RemoteIsolatedUISession.MostUnread; seq++) session.Receive(Call(seq));
            Require(!session.Calls.Completion.IsCompleted, $"A client keeps its session with {RemoteIsolatedUISession.MostUnread} calls unread.");
            Require(session.Calls.TryPeek(out var first) && first.Answered, "The unread calls wait in order.");
            session.Receive(Call(RemoteIsolatedUISession.MostUnread + 1));
            Require(session.Calls.Completion.IsFaulted && session.Calls.Completion.Exception!.InnerException is IOException { Message: "The plugin sent more than its client could take." },
                "One more unread call ends the client's session with the reason.");
            Require(first!.Reply.IsCanceled, "The calls left unread are abandoned.");
            Require(!session.Calls.TryRead(out _), "The client takes no further calls.");
        }
    }

    private static void App(string root, PluginChannelChecks.RunnerLaunch runner)
    {
        var workspace = Path.Combine(root, "workspace");
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(workspace);
        PluginIsolatedUIChecks.Install(Path.Combine(state, "plugins"));
        var seam = PluginIsolatedUIChecks.Seam(runner);
        seam = seam with { RunnerEnvironment = new Dictionary<string, string>(seam.RunnerEnvironment) { [SurfaceProtocol.DiagnosticsVariable] = "1" } };
        var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, Token, state, plugins: seams => seams with { Isolation = seam });
        E2eWorkspace? a = null, b = null;
        try
        {
            Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            var port = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            var runtime = server.Services.GetRequiredService<PluginRuntime>();
            var sessions = server.Services.GetRequiredService<PluginSurfaceSessions>();
            // Each client behind its own relay: clients of one address share a connection in one process.
            using var relayA = new CutProxy(port);
            using var relayB = new CutProxy(port);
            using var hostA = new RemoteIsolatedUIHost(relayA.Endpoint, Token);
            using var hostB = new RemoteIsolatedUIHost(relayB.Endpoint, Token);
            var recordedB = new Recorded(hostB);
            UiSessionRelay.RelaySurface? Held(RemoteSurface surface) => surface.Surface.Number is > 0 and var number ? runtime.Surface(Id, number) : null;

            a = new E2eWorkspace(relayA.Endpoint, Token, workspace, Path.Combine(root, "profile-a"), workspace, isolated: hostA);
            E2eWorkspace.Until(() => a.Window.WorkspaceMounted && a.Workbench.PluginLoader.Registry.Roster.Any(entry => entry.Id == Id));
            var enabled = a.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(Id, true));
            E2eWorkspace.Until(() => enabled.IsCompleted && a.Workbench.PluginLoader.Registry.Active.Contains(Id) && PluginSurfaceChecks.Clicks(a) is not null);
            var pid = runtime.RunnerPid(Id);
            a.Window.Layout.RestoreTool("plugin:" + Id + ":board");
            var boardA = PluginSurfaceChecks.Surface(a.Window, _ => true);
            var shownA = PluginSurfaceChecks.Settle(boardA, "client A's side tool", relay: Held);
            Require(PluginSurfaceChecks.Opaque(shownA) > 500, "Client A's side tool shows what the plugin drew.");
            Console.WriteLine($"PASS plugin surface remote render: over WebSocket RPC the side tool's {shownA.Width}x{shownA.Height} device pixels equal the plugin's own render");

            b = new E2eWorkspace(relayB.Endpoint, Token, workspace, Path.Combine(root, "profile-b"), workspace, isolated: recordedB);
            E2eWorkspace.Until(() => b.Window.WorkspaceMounted && b.Workbench.PluginLoader.Registry.Active.Contains(Id));
            b.Window.Layout.RestoreTool("plugin:" + Id + ":board");
            var boardB = PluginSurfaceChecks.Surface(b.Window, _ => true);
            var shownB = PluginSurfaceChecks.Settle(boardB, "client B's side tool", relay: Held);
            Require(runtime.RunnerPid(Id) == pid && pid is not null, "A second client is served by the plugin's one process.");
            Require(sessions.Count == 2 && boardA.Surface.Number != boardB.Surface.Number, "Each client holds its own session and surface in that process.");
            Console.WriteLine($"PASS plugin surface remote clients: two clients each show the side tool from the plugin's one process {pid}, each with its own session and top-level");

            // A's popups travel over the session as surfaces of their own, and A's scale is A's alone: B keeps rendering at 1.
            shownA = PluginSurfaceChecks.Popups(a, a.Window, boardA, shownA, Held, "plugin surface remote");
            // The extras' dialog counts one click.
            shownA = PluginSurfaceChecks.Extras(a, a.Window, boardA, shownA, Held, "plugin surface remote");
            shownA = PluginSurfaceChecks.Scaled(a.Window, boardA, shownA, Held, "plugin surface remote", () =>
            {
                Require(boardB.FrameScale == 1, "Client B renders at its own scale while client A's is 2.");
                PluginSurfaceChecks.Settle(boardB, "client B's side tool while A is at scale 2", relay: Held);
            });
            shownB = PluginSurfaceChecks.Settle(boardB, "client B's side tool after A's popups", relay: Held);
            Console.WriteLine("PASS plugin surface remote per-client scale: client A at scale 2 and client B at scale 1 render from the one process at the same time, each matching the plugin's render");

            // A click in A counts in the plugin's settings, which B's context mirrors, so B's view redraws with the count.
            var before = shownB.Pixels;
            PluginSurfaceChecks.Click(a.Window, boardA, shownA.Bounds["OutsiderClick"]);
            E2eWorkspace.Until(() => PluginSurfaceChecks.Clicks(a) == 4 && PluginSurfaceChecks.Clicks(b) == 4);
            shownB = PluginSurfaceChecks.Settle(boardB, "client B's side tool after A's click", before, Held);
            shownA = PluginSurfaceChecks.Settle(boardA, "client A's side tool after its click", relay: Held);
            Console.WriteLine("PASS plugin surface remote input: a click in client A reaches the plugin, and client B's view of the count redraws");

            // A types in its note, which only its control in the plugin's process holds.
            boardA.Focus(NavigationMethod.Tab);
            E2eWorkspace.Until(() => boardA.IsFocused);
            PluginSurfaceChecks.Press(a.Window, Key.Tab, PhysicalKey.Tab);
            before = shownA.Pixels;
            a.Window.KeyTextInput("kept");
            shownA = PluginSurfaceChecks.Settle(boardA, "client A's side tool after typing", before, Held);
            var typed = shownA.Pixels;
            var activations = Activations(a);
            var surfaceA = boardA.Surface;
            var keyframes = boardA.Keyframes;

            // A's connection drops; B keeps working meanwhile.
            relayA.Cut();
            before = shownB.Pixels;
            PluginSurfaceChecks.Click(b.Window, boardB, shownB.Bounds["OutsiderClick"]);
            E2eWorkspace.Until(() => PluginSurfaceChecks.Clicks(b) == 5);
            shownB = PluginSurfaceChecks.Settle(boardB, "client B's side tool while A is away", before, Held);
            Console.WriteLine("PASS plugin surface remote drop: with client A's connection cut, client B's clicks still reach the plugin and its view redraws");

            // Back within the grace: the same session and surface go on, repainted from a keyframe, the note and count intact.
            var away = Stopwatch.StartNew();
            E2eWorkspace.Until(() => away.Elapsed > TimeSpan.FromSeconds(1));
            relayA.Allow();
            E2eWorkspace.Until(() => boardA.Keyframes > keyframes);
            E2eWorkspace.Until(() => PluginSurfaceChecks.Clicks(a) == 5);
            shownA = PluginSurfaceChecks.Settle(boardA, "client A's side tool after it resumed", typed, Held);
            Require(ReferenceEquals(boardA.Surface, surfaceA) && boardA.Child is null, "Client A's side tool keeps its surface across the drop.");
            Require(Activations(a) == activations && sessions.Count == 2 && runtime.RunnerPid(Id) == pid, "Resuming opens no new session and activates nothing again.");
            before = shownA.Pixels;
            PluginSurfaceChecks.Click(a.Window, boardA, shownA.Bounds["OutsiderClick"]);
            E2eWorkspace.Until(() => PluginSurfaceChecks.Clicks(a) == 6);
            shownA = PluginSurfaceChecks.Settle(boardA, "client A's side tool clicked after it resumed", before, Held);
            Console.WriteLine($"PASS plugin surface remote resume: back within the grace client A resumes its session, repaints from a keyframe with its typed note and B's click shown, and its clicks reach the plugin again ({boardA.Keyframes - keyframes} keyframe)");

            // B's plugin floods B with one-way calls while the app's UI thread is blocked (nothing pumps it below): B's session
            // ends with the reason once 1,024 wait unread, on the host too, and A's goes on.
            var flooded = recordedB.Opened[^1];
            PluginSurfaceChecks.Click(b.Window, boardB, shownB.Bounds["OutsiderFlood"]);
            var blocked = Stopwatch.StartNew();
            while (!(sessions.Count == 1 && flooded.Calls.Completion.IsCompleted) && blocked.Elapsed < PluginChannelChecks.Patience) Thread.Sleep(20);
            Require(sessions.Count == 1, "A plugin that floods a client whose UI thread is blocked ends that client's session on the host.");
            Require(flooded.Calls.Completion is { IsFaulted: true, Exception.InnerException: IOException { Message: "The plugin sent more than its client could take." } },
                "The flooded client's session ends with the reason.");
            before = shownA.Pixels;
            PluginSurfaceChecks.Click(a.Window, boardA, shownA.Bounds["OutsiderClick"]);
            E2eWorkspace.Until(() => PluginSurfaceChecks.Clicks(a) == 7);
            shownA = PluginSurfaceChecks.Settle(boardA, "client A's side tool after B was flooded", before, Held);
            Require(ReferenceEquals(boardA.Surface, surfaceA) && runtime.RunnerPid(Id) == pid, "The other client keeps its session and surface in the same process.");
            E2eWorkspace.Until(() => recordedB.Opened.Count == 2 && sessions.Count == 2 && b.Workbench.PluginLoader.Registry.Active.Contains(Id));
            Console.WriteLine($"PASS plugin surface remote flood: a plugin that floods client B while the UI thread is blocked ends B's session with the reason at {RemoteIsolatedUISession.MostUnread} unread calls, client A's clicks still reach the plugin, and B opens a new session");

            // A client that closes ends only its own session.
            b.Workbench.PluginLoader.Stop();
            b.Dispose();
            b = null;
            E2eWorkspace.Until(() => sessions.Count == 1);
            before = shownA.Pixels;
            PluginSurfaceChecks.Click(a.Window, boardA, shownA.Bounds["OutsiderClick"]);
            E2eWorkspace.Until(() => PluginSurfaceChecks.Clicks(a) == 8);
            PluginSurfaceChecks.Settle(boardA, "client A's side tool after B closed", before, Held);
            Require(runtime.RunnerPid(Id) == pid, "The plugin's process outlives a client that closes.");
            Console.WriteLine("PASS plugin surface remote close: a client that closes ends its session alone, and the other's view keeps working in the same process");

            // Past the grace a dropped session ends; the client opens a new one when it is back, and the plugin activates again.
            sessions.Grace = TimeSpan.FromSeconds(1);
            relayA.Cut();
            E2eWorkspace.Until(() => sessions.Count == 0);
            relayA.Allow();
            E2eWorkspace.Until(() => sessions.Count == 1 && Activations(a) > activations && a.Workbench.PluginLoader.Registry.Active.Contains(Id));
            Console.WriteLine("PASS plugin surface remote grace: past the grace a dropped client's session ends, and back again the client opens a new one");
        }
        finally
        {
            b?.Dispose();
            a?.Dispose();
            Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }

    private static int Activations(E2eWorkspace app) =>
        app.Workbench.State.Current.PluginSettings.TryGetValue(Id, out var space) && space.ValueKind == JsonValueKind.Object && space.TryGetProperty("activations", out var count) ? count.GetInt32() : 0;

    /// <summary>A client's isolated host that keeps every session it opened, so a check reads why one ended.</summary>
    private sealed class Recorded(IIsolatedUIHost inner) : IIsolatedUIHost
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<IIsolatedUISession> opened = new();

        public IReadOnlyList<IIsolatedUISession> Opened => [.. opened];

        public async ValueTask<IIsolatedUISession> OpenAsync(string pluginId, string clientKey, CancellationToken cancellationToken = default)
        {
            var session = await inner.OpenAsync(pluginId, clientKey, cancellationToken).ConfigureAwait(false);
            opened.Enqueue(session);
            return session;
        }
    }
}