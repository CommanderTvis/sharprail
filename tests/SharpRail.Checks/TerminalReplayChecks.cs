using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

// How much output a terminal keeps for replay is a host setting, read when a shell starts.
internal static partial class TerminalHostChecks
{
    private static async Task ReplaySize(string root, string workspace)
    {
        var directory = Path.Combine(root, "replay-state");
        var store = new HostStateStore(directory);
        Require(store.Current.Settings.TerminalReplayKb == HostSettings.DefaultTerminalReplayKb, "The replay size must default to 64 KiB.");
        foreach (var invalid in new[] { "1025", "-1", "64.5", "" })
            try { await store.ChangeAsync([HostStateChange.Setting("terminal-replay", invalid)]); throw new InvalidOperationException("An invalid replay size was accepted: " + invalid); }
            catch (ArgumentException) { }
        await store.ChangeAsync([HostStateChange.Setting("terminal-replay", "1024")]);
        Require(new HostStateStore(directory).Current.Settings.TerminalReplayKb == 1024, "The replay size must persist.");
        File.WriteAllText(Path.Combine(directory, HostStateStore.FileName), "{\"Settings\":{\"TerminalReplayKb\":99999}}");
        Require(new HostStateStore(directory).Current.Settings.TerminalReplayKb == HostSettings.MaxTerminalReplayKb, "A loaded replay size must be clamped.");

        var budget = 0;
        await using (var local = new PtyTerminalService(replayBytes: () => budget))
        {
            Require(await Replayed(local, workspace) == 0, "A replay size of zero must replay nothing.");
            budget = 256 * 1024;
            Require(await Replayed(local, workspace) is > 128 * 1024 and <= 256 * 1024 + 64, "A 256 KiB replay size must keep more than the default.");
            budget = int.MaxValue;
            Require(await Replayed(local, workspace) <= TerminalRecorder.MaxSnapshotBytes + 64, "The replay size must be capped at 1 MiB.");
        }
        await using (var local = new PtyTerminalService())
            Require(await Replayed(local, workspace) is > 32 * 1024 and <= TerminalRecorder.SnapshotBytes + 64, "Without a setting the replay stays at 64 KiB.");

        // A remote host reads its own shared setting, zero included, and a restarted host revives a large screen.
        var state = Path.Combine(root, "replay-remote");
        var id = "replay-" + Guid.NewGuid().ToString("N");
        var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "replay-token", state);
        await app.StartAsync();
        try
        {
            using var settings = new RemoteStateAdapter(Address(app), "replay-token");
            using var terminals = new RemoteTerminalAdapter(Address(app), "replay-token");
            Require((await settings.GetStateAsync()).Settings.TerminalReplayKb == HostSettings.DefaultTerminalReplayKb, "A remote host must report the default replay size.");
            Require((await settings.ChangeAsync([HostStateChange.Setting("terminal-replay", "0")])).Settings.TerminalReplayKb == 0, "Zero must survive the wire as zero.");
            Require(await Replayed(terminals, workspace) == 0, "A remote host must apply a replay size of zero.");
            try { await settings.ChangeAsync([HostStateChange.Setting("terminal-replay", "2000")]); throw new InvalidOperationException("A remote host accepted an oversized replay size."); }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.InvalidArgument) { }
            await settings.ChangeAsync([HostStateChange.Setting("terminal-replay", "256")]);
            Require(await Replayed(terminals, workspace, id, close: false) > 128 * 1024, "A remote host must apply a larger replay size to terminals opened afterwards.");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
        app = RemoteServer.Create(root, IPAddress.Loopback, 0, "replay-token", state);
        await app.StartAsync();
        try
        {
            using var terminals = new RemoteTerminalAdapter(Address(app), "replay-token");
            await using var revived = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
            Require(revived.Created && revived.Replay.Length > 128 * 1024, $"A restarted host must revive a screen larger than the default, got {revived.Replay.Length} bytes.");
            await terminals.CloseAsync(id);
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
        Console.WriteLine("PASS host terminal replay size: default 64 KiB, validated, clamped and persisted, zero replays nothing, a larger size applies to terminals opened afterwards locally and remotely, and survives a host restart");
    }

    // Writes more than a megabyte of lines, then reattaches a fresh view and returns the size of its replay.
    private static async Task<int> Replayed(ITerminalService terminals, string workspace, string? id = null, bool close = true)
    {
        id ??= "replay-" + Guid.NewGuid().ToString("N");
        var (session, screen) = await Open(terminals, id, workspace);
        await screen.Run("i=0; while [ $i -lt 20000 ]; do printf '%0100d\\n' $i; i=$((i+1)); done; printf 'REPLAY_%s\\n' FILLED");
        await screen.WaitFor("REPLAY_FILLED", "replay size");
        await session.DisposeAsync();
        await using var again = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
        var length = again.Replay.Length;
        if (close) await terminals.CloseAsync(id);
        return length;
    }

    // Settings shows the host's value and changes it through the host: the selection moves when the broadcast arrives.
    internal static void ReplaySetting(string root)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(root, "replay-setting"), openFiles: false);
        var settings = new SettingsWindow(app.Window, () => { });
        settings.Show(app.Window);
        settings.ShowSection("Terminal");
        try
        {
            Button Choice(int kb) => settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "TerminalReplay_" + kb);
            bool Active(int kb) => Choice(kb).GetLogicalDescendants().OfType<Control>().Count(control => Grid.GetColumn(control) == 1) == 1;
            Require(new[] { 0, 16, 64, 256, 1024 }.All(kb => Active(kb) == (kb == 64)), "The replay choices must show the host's default.");
            app.Click(Choice(256));
            E2E.E2eWorkspace.Until(() => app.State!.Current.Settings.TerminalReplayKb == 256 && Active(256) && !Active(64));
            // Another client's change moves the selection here too.
            app.State!.ChangeAsync([HostStateChange.Setting("terminal-replay", "0")]).AsTask().GetAwaiter().GetResult();
            E2E.E2eWorkspace.Until(() => Active(0) && !Active(256));
        }
        finally { settings.Close(); }
        Console.WriteLine("PASS terminal replay Settings: the choices show the host's value, a click changes it through the host, and another client's change moves the selection");
    }
}