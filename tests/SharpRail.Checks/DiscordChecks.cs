using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Discord;
using SharpRail.Plugins.Discord.Host;
using SharpRail.UI;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

// The Discord builtin plugin: the fork's presence and lifecycle tests against the host half, and its e2e
// (enable from Settings › Plugins, the application id's validation and persistence, a blocked project kept)
// against the app with a fake Discord on a real Unix socket, so the developer's own Discord is never reached.
internal static class DiscordChecks
{
    private const long Started = 1_700_000_000_000;
    private static readonly DiscordSettings Ready = new() { ApplicationId = "1234567890123456789" };
    private static readonly DiscordPresence AtWork = new("p1", "thinkrail", "src/host/server.ts");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task RunHostAsync(string root)
    {
        Decisions();
        await LifecycleAsync();
        await StopDuringHandshakeAsync();
        await IpcFramesAsync();
        await TransportAsync(Path.Combine(root, "discord-transport"));
        Console.WriteLine("PASS Discord presence decision, status mapping and connection lifecycle against a fake Discord");
    }

    private static async Task IpcFramesAsync()
    {
        var saved = Environment.GetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR");
        var directory = Directory.CreateTempSubdirectory("srd-").FullName;
        Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", directory);
        using var discord = new FakeDiscord(Path.Combine(directory, "discord-ipc-0"));
        using var client = new DiscordIpc();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Require(DiscordIpc.SocketCandidates().SequenceEqual(Enumerable.Range(0, 10).Select(index => Path.Combine(directory, $"discord-ipc-{index}"))),
                "The override isolates all ten socket candidates.");
            await client.ConnectAsync(Ready.ApplicationId, () => closed.TrySetResult());
            var ping = new JsonObject { ["nonce"] = "fragmented-ping" };
            await discord.SendAsync(3, ping, fragmented: true);
            while (!discord.Pongs.Any(pong => JsonNode.DeepEquals(pong, ping))) await Task.Delay(10, timeout.Token);
            await discord.SendAsync(1, new JsonObject { ["evt"] = "ERROR", ["data"] = new JsonObject { ["message"] = "Invalid application" } });
            while (client.LastError != "Invalid application") await Task.Delay(10, timeout.Token);
            discord.CloseClients();
            await closed.Task.WaitAsync(timeout.Token);
            Require(!client.Connected, "The remote socket close clears connection state and notifies its owner.");
        }
        finally
        {
            client.Close();
            Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", saved);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("PASS Discord IPC: isolated candidates, fragmented ping/pong, server errors and socket closure");
    }

    private static async Task TransportAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var socketDirectory = Directory.CreateTempSubdirectory("srd-").FullName;
        var saved = Environment.GetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR");
        Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", socketDirectory);
        using var discord = new FakeDiscord(Path.Combine(socketDirectory, "discord-ipc-0"));
        await using var server = RemoteServer.Create(directory, IPAddress.Loopback, 0, "discord", Path.Combine(directory, "state"));
        try
        {
            await server.StartAsync();
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var state = new RemoteStateAdapter(address, "discord");
            using var plugins = new RemotePluginAdapter(address, "discord");
            var runtime = server.Services.GetRequiredService<PluginRuntime>();
            Require((await plugins.ListAsync()).Single(entry => entry.Id == "discord").Status == PluginStatus.Disabled,
                "Remote presence is disabled before it is chosen.");
            await state.ChangeAsync([HostStateChange.PluginEnabled("discord", true)]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await plugins.ListAsync(timeout.Token)).Single(entry => entry.Id == "discord").Status != PluginStatus.Active)
                await Task.Delay(25, timeout.Token);
            Require(await plugins.ReadFileAsync("discord", "assets/discord.svg") is { Length: > 0 }, "The fork's Discord mark is served by the host.");
            async Task<DiscordStatus> Call(string method, object parameters) =>
                PluginJson.Convert<DiscordStatus>(await plugins.CallAsync(new("discord", method, parameters, "discord-client"), timeout.Token));
            await using var frames = plugins.SubscribeAsync(new("discord", "status", null, "discord-client"), timeout.Token).GetAsyncEnumerator(timeout.Token);
            var receiving = frames.MoveNextAsync().AsTask();
            await Task.Delay(100, timeout.Token);
            var published = await Call("presence", new DiscordPresenceParams(AtWork));
            Require(published.Published == new DiscordPublished("thinkrail", "Editing server.ts"), "The remote call returns the published file and project.");
            Require(await receiving.WaitAsync(timeout.Token), "The remote status channel delivers a frame.");
            while (PluginJson.Convert<DiscordStatus>(frames.Current).Published is null)
                Require(await frames.MoveNextAsync(), "The channel remains open until it reports the presence.");
            Require(PluginJson.Convert<DiscordStatus>(frames.Current).Published == published.Published, "The push agrees with the call.");
            var local = PluginJson.Convert<DiscordStatus>(await runtime.CallAsync(new("discord", "status", new DiscordStatusParams(), "local-client")));
            Require(await Call("status", new DiscordStatusParams()) == local, "Local and remote status are identical.");
            await state.ChangeAsync([HostStateChange.PluginSettings("discord", """{"shareFileName":false}""")]);
            var hidden = await Call("presence", new DiscordPresenceParams(AtWork));
            Require(hidden.Published == new DiscordPublished("thinkrail"), "The remote settings update withholds the file name.");
            while (!discord.Activities.Any(activity => (string?)activity?["state"] == "thinkrail" && activity?["details"] is null))
                await Task.Delay(20, timeout.Token);
            await state.ChangeAsync([HostStateChange.PluginEnabled("discord", false)]);
            while ((await plugins.ListAsync(timeout.Token)).Single(entry => entry.Id == "discord").Status != PluginStatus.Disabled)
                await Task.Delay(25, timeout.Token);
            try { await Call("status", new DiscordStatusParams()); throw new InvalidOperationException("Disabled Discord accepted a call."); }
            catch (PluginCallException error) when (error.Error == PluginCallError.Disabled) { }
        }
        finally
        {
            await server.StopAsync();
            Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", saved);
            Directory.Delete(socketDirectory, true);
        }
        Console.WriteLine("PASS Discord transport: disabled lifecycle, SVG asset, presence, status push and redaction over gRPC");
    }

    private static async Task StopDuringHandshakeAsync()
    {
        var saved = Environment.GetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR");
        var directory = Directory.CreateTempSubdirectory("srd-").FullName;
        Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", directory);
        using var discord = new FakeDiscord(Path.Combine(directory, "discord-ipc-0"), holdHandshake: true);
        var reports = 0;
        var runtime = new DiscordRuntime(() => Ready, _ => Interlocked.Increment(ref reports));
        try
        {
            var publishing = runtime.PublishPresenceAsync(AtWork);
            await discord.HandshakeSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.Stop();
            discord.ReleaseHandshake();
            await publishing.WaitAsync(TimeSpan.FromSeconds(5));
            Require(discord.Activities.Count == 0 && reports == 0, "A handshake completing after disable cannot publish or revive its connection.");
            await runtime.GetStatusAsync();
            Require(discord.Activities.Count == 0, "A stopped activation cannot reconnect.");
        }
        finally
        {
            runtime.Stop();
            Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", saved);
            Directory.Delete(directory, true);
        }
    }

    private static void Decisions()
    {
        Require(PresenceDecision.For(AtWork, Ready, Started) == new PresenceDecision.Publish(new("Editing server.ts", "thinkrail", Started)),
            "The project and the focused file are published.");
        var nested = PresenceDecision.For(AtWork with { FilePath = "clients/acme/contracts/pricing.ts" }, Ready, Started);
        Require(nested is PresenceDecision.Publish { Activity.Details: "Editing pricing.ts" } && !nested.ToString().Contains("acme", StringComparison.Ordinal),
            "The file is named, never the path that leads to it.");
        var blocked = PresenceDecision.For(AtWork, Ready with { BlockedProjectIds = ["p1"] }, Started);
        Require(blocked is PresenceDecision.Clear && !blocked.ToString().Contains("server.ts", StringComparison.Ordinal),
            "A blocked project stays off Discord entirely, not merely anonymous.");
        var withheld = PresenceDecision.For(AtWork, Ready with { ShareFileName = false }, Started);
        Require(withheld is PresenceDecision.Publish { Activity: { Details: null, State: "thinkrail" } }, "File sharing off drops the file name but keeps the project.");
        var noFile = PresenceDecision.For(AtWork with { FilePath = null }, Ready, Started);
        Require(noFile is PresenceDecision.Publish { Activity.Details: null } && !withheld.ToString().Contains("server.ts", StringComparison.Ordinal),
            "A withheld name says nothing rather than claiming no file is open.");
        Require(PresenceDecision.For(AtWork, Ready with { ApplicationId = "" }, Started) is PresenceDecision.Silent &&
            PresenceDecision.For(AtWork, Ready with { ApplicationId = "not-a-snowflake" }, Started) is PresenceDecision.Silent,
            "Nothing is published until an application id is configured.");
        var first = (PresenceDecision.Publish)PresenceDecision.For(AtWork, Ready, Started);
        var second = (PresenceDecision.Publish)PresenceDecision.For(AtWork with { FilePath = "other.ts" }, Ready, Started);
        Require(first.Activity.StartedAt == second.Activity.StartedAt, "The timer holds across a file change.");

        Require(PresenceDecision.Status(first, true, null) == new DiscordStatus(DiscordConnectionState.Connected, new("thinkrail", "Editing server.ts")),
            "The status reports the exact pair Discord received.");
        Require(PresenceDecision.Status(first, false, "Discord is not running on this machine.") is { State: DiscordConnectionState.Unavailable, Published: null },
            "A connection failure is preferred over claiming to be connected.");
        Require(PresenceDecision.Status(blocked, true, null) == new DiscordStatus(DiscordConnectionState.Connected, null, "thinkrail is blocked from Discord."),
            "The status says why nothing is published for a blocked project.");
        var noProject = PresenceDecision.For(null, Ready, Started);
        Require(noProject == new PresenceDecision.Clear("No project is open.") &&
            PresenceDecision.Status(noProject, true, null) == new DiscordStatus(DiscordConnectionState.Connected, null, "No project is open."),
            "No project clears the presence and reports why.");
        Require(PresenceDecision.Status(first, false, null).State == DiscordConnectionState.Connecting &&
            PresenceDecision.Status(new PresenceDecision.Silent("Configure it"), false, "Unavailable").State == DiscordConnectionState.Unconfigured,
            "Unconfigured settings take precedence over connection failure; otherwise an unfinished connection is reported.");
    }

    private static async Task LifecycleAsync()
    {
        var saved = Environment.GetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR");
        var empty = Directory.CreateTempSubdirectory("srd-").FullName;
        var socketDirectory = Directory.CreateTempSubdirectory("srd-").FullName;
        Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", empty);
        var runtime = new DiscordRuntime(() => Ready, _ => { });
        try
        {
            runtime.ApplySettingsChange();
            Require((await runtime.GetStatusAsync()).State == DiscordConnectionState.Unavailable, "A failed first attempt reports unavailable.");
            using var discord = new FakeDiscord(Path.Combine(socketDirectory, "discord-ipc-0"));
            Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", socketDirectory);
            // Still within the retry floor: the socket exists now, but nothing looks again until the floor clears.
            Require((await runtime.GetStatusAsync()).State == DiscordConnectionState.Unavailable, "A socket appearing within the retry floor is not noticed.");
            runtime.ApplySettingsChange();
            var state = (await runtime.GetStatusAsync()).State;
            for (var attempt = 0; state == DiscordConnectionState.Connecting && attempt < 100; attempt++)
            {
                await Task.Delay(20);
                state = (await runtime.GetStatusAsync()).State;
            }
            Require(state == DiscordConnectionState.Connected, "A settings change clears the retry floor, so the next status connects: " + state);
        }
        finally
        {
            runtime.Stop();
            Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", saved);
            Directory.Delete(empty, true);
            Directory.Delete(socketDirectory, true);
        }
    }

    private static T Find<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    private static bool Has(Control scope, string name)
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == name && item.IsEffectivelyVisible);
    }

    private static SettingsWindow OpenSection(WorkbenchWindow window)
    {
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
        E2eWorkspace.Until(() => Has(settings, "Settings_plugin_discord_discord"));
        settings.ShowSection("plugin:discord:discord");
        E2eWorkspace.Until(() => Has(settings, "DiscordSettings"));
        return settings;
    }

    private static void Close(WorkbenchWindow window, SettingsWindow settings)
    {
        settings.Close();
        E2eWorkspace.Until(() => !window.OwnedWindows.Any());
    }

    // Types into the field and moves focus away, which is when the section saves it.
    private static void Commit(SettingsWindow settings, string text)
    {
        var field = Find<TextBox>(settings, "DiscordApplicationId");
        field.Focus();
        E2eWorkspace.Until(() => field.IsFocused);
        field.Text = text;
        Find<ToggleSwitch>(settings, "DiscordShareFileName").Focus();
        E2eWorkspace.Until(() => !field.IsFocused);
    }

    private static string? StoredApplicationId(HostStateStore state) =>
        state.Current.PluginSettings.GetValueOrDefault("discord") is { ValueKind: JsonValueKind.Object } stored &&
        stored.TryGetProperty("applicationId", out var id) ? id.GetString() : null;

    internal static void RunUi(string root)
    {
        RunUiScenario(root, remote: false);
        RunUiScenario(root, remote: true);
    }

    private static void RunUiScenario(string root, bool remote)
    {
        var saved = Environment.GetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR");
        var socketDirectory = Directory.CreateTempSubdirectory("srd-").FullName;
        Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", socketDirectory);
        using var discord = new FakeDiscord(Path.Combine(socketDirectory, "discord-ipc-0"));
        var mode = remote ? "remote" : "local";
        var workspace = Path.Combine(root, "discord-ui-" + mode, "workspace");
        var profileRoot = Path.Combine(root, "discord-ui-" + mode, "profile");
        var stateDirectory = remote ? Path.Combine(root, "discord-ui-" + mode, "state") : profileRoot;
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "notes.txt"), "Discord file-name fixture.");
        var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "discord-ui", stateDirectory) : null;
        try
        {
            if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            using var app = server is null ? new E2eWorkspace(workspace, profileRoot: profileRoot)
                : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                    "discord-ui", workspace, profileRoot, workspace);
            E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
            var window = app.Window;
            var loader = app.Workbench.PluginLoader;
            var state = app.State ?? (HostStateStore)server!.Services.GetRequiredService<IHostStateService>();

            // Off by default: Rich Presence publishes to everyone who can see the profile, so it waits to be chosen.
            E2eWorkspace.Until(() => loader.Registry.Roster.Any(entry => entry.Id == "discord"));
            Require(loader.Registry.Roster.Single(entry => entry.Id == "discord").Status == PluginStatus.Disabled, "Discord is off by default.");
            window.ShowSettings("Plugins");
            E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
            var plugins = window.OwnedWindows.OfType<SettingsWindow>().Single();
            Find<ToggleSwitch>(Find<Border>(plugins, "PluginRow_discord"), "PluginToggle").IsChecked = true;
            E2eWorkspace.Until(() => loader.Registry.Active.Contains("discord"));
            Close(window, plugins);

            // Enabling is the only step: the presence wears ThinkRail's own application out of the box, and what is open reaches Discord.
            var settings = OpenSection(window);
            Require(DiscordValues.IsApplicationId(Find<TextBox>(settings, "DiscordApplicationId").Text ?? ""), "The application id field starts with a snowflake.");
            E2eWorkspace.Until(() => state.Current.Projects.Count > 0);
            var projectName = app.Workbench.State.Label(state.Current.Projects[0]);
            E2eWorkspace.Until(() => discord.Activities.Any(activity => (string?)activity?["state"] == projectName));
            E2eWorkspace.Until(() => Find<TextBlock>(settings, "DiscordStatus").Text?.StartsWith("On Discord", StringComparison.Ordinal) == true);

            var opening = window.OpenDocumentAsync("notes.txt");
            E2eWorkspace.Until(() => opening.IsCompleted);
            opening.GetAwaiter().GetResult();
            E2eWorkspace.Until(() => discord.Activities.LastOrDefault()?["details"]?.GetValue<string>() == "Editing notes.txt");
            Find<ToggleSwitch>(settings, "DiscordShareFileName").IsChecked = false;
            E2eWorkspace.Until(() => discord.Activities.LastOrDefault() is JsonObject current && current["state"] is not null && current["details"] is null);
            Find<ToggleSwitch>(settings, "DiscordShareFileName").IsChecked = true;
            E2eWorkspace.Until(() => discord.Activities.LastOrDefault()?["details"]?.GetValue<string>() == "Editing notes.txt");

            // Clearing the field is a choice, and the one that puts it back in the silent state.
            Commit(settings, "");
            E2eWorkspace.Until(() => Find<TextBlock>(settings, "DiscordStatus").Text?.Contains("application id", StringComparison.Ordinal) == true);
            Require(StoredApplicationId(state) == "", "An empty application id is stored as the choice it is.");

            // An invalid id stays in the field but never reaches the host; a valid one is kept across a reload.
            Commit(settings, "not-a-snowflake");
            E2eWorkspace.Settle(200);
            Require(Find<TextBox>(settings, "DiscordApplicationId").Text == "not-a-snowflake" && StoredApplicationId(state) == "",
                "An invalid application id is rejected without clearing what was typed.");
            Close(window, settings);
            settings = OpenSection(window);
            Require(Find<TextBox>(settings, "DiscordApplicationId").Text == "", "The rejected id does not come back.");
            Commit(settings, "1234567890123456789");
            E2eWorkspace.Until(() => StoredApplicationId(state) == "1234567890123456789");
            Close(window, settings);
            settings = OpenSection(window);
            Require(Find<TextBox>(settings, "DiscordApplicationId").Text == "1234567890123456789", "A valid id is kept.");
            Require(StoredApplicationId(new HostStateStore(stateDirectory)) == "1234567890123456789", "The id is persisted with the host's state.");

            // A blocked project stays blocked across a reload.
            E2eWorkspace.Until(() => Has(settings, "DiscordBlock"));
            var block = settings.GetLogicalDescendants().OfType<CheckBox>().First(box => box.Name == "DiscordBlock");
            var project = (string)block.Tag!;
            Require(block.IsChecked == false, "A project starts unblocked.");
            app.Click(block, freshGesture: false);
            E2eWorkspace.Until(() => block.IsChecked == true);
            E2eWorkspace.Until(() => state.Current.PluginSettings.GetValueOrDefault("discord") is { ValueKind: JsonValueKind.Object } stored &&
                stored.TryGetProperty("blockedProjectIds", out var ids) && ids.EnumerateArray().Any(id => id.GetString() == project));
            Close(window, settings);
            settings = OpenSection(window);
            Require(settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "DiscordBlock" && Equals(box.Tag, project)).IsChecked == true,
                "A blocked project stays blocked.");
            E2eWorkspace.Until(() => Find<TextBlock>(settings, "DiscordStatus").Text?.Contains("is blocked from Discord", StringComparison.Ordinal) == true);
            Close(window, settings);
            window.ShowSettings("Plugins");
            E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
            plugins = window.OwnedWindows.OfType<SettingsWindow>().Single();
            Find<ToggleSwitch>(Find<Border>(plugins, "PluginRow_discord"), "PluginToggle").IsChecked = false;
            E2eWorkspace.Until(() => !loader.Registry.Active.Contains("discord"));
            Require(!Has(plugins, "Settings_plugin_discord_discord"), "Disable removes the Discord settings contribution.");
            Close(window, plugins);
        }
        finally
        {
            if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("SHARPRAIL_DISCORD_IPC_DIR", saved);
            Directory.Delete(socketDirectory, true);
        }
        Console.WriteLine($"PASS {mode} Discord UI: enable, file presence/redaction, application id and blocked projects kept, disable");
    }

    // A stand-in for Discord's IPC server: answers the handshake with READY and records every activity it is sent.
    private sealed class FakeDiscord : IDisposable
    {
        private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly List<JsonNode?> activities = [];
        private readonly List<JsonNode?> pongs = [];
        private readonly List<Socket> clients = [];
        private readonly TaskCompletionSource<Socket> readyClient = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource handshakeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandshakeSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeDiscord(string path, bool holdHandshake = false)
        {
            if (!holdHandshake) handshakeRelease.TrySetResult();
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen();
            _ = AcceptAsync();
        }

        public IReadOnlyList<JsonNode?> Activities { get { lock (activities) return [.. activities]; } }
        public IReadOnlyList<JsonNode?> Pongs { get { lock (pongs) return [.. pongs]; } }
        public void ReleaseHandshake() => handshakeRelease.TrySetResult();
        public async Task SendAsync(int op, JsonNode payload, bool fragmented = false)
        {
            var connection = await readyClient.Task;
            var frame = Frame(op, payload);
            if (fragmented)
            {
                await connection.SendAsync(frame.AsMemory(0, 3), SocketFlags.None);
                await Task.Delay(20);
                await connection.SendAsync(frame.AsMemory(3), SocketFlags.None);
            }
            else await connection.SendAsync(frame, SocketFlags.None);
        }

        public void CloseClients()
        {
            lock (clients)
                foreach (var client in clients) client.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptAsync();
                    lock (clients) clients.Add(client);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }

        private async Task ServeAsync(Socket client)
        {
            using var _ = client;
            var pending = new List<byte>();
            var buffer = new byte[4096];
            try
            {
                while (await client.ReceiveAsync(buffer, SocketFlags.None) is > 0 and var read)
                {
                    pending.AddRange(buffer.AsSpan(0, read));
                    while (pending.Count >= 8)
                    {
                        var op = BinaryPrimitives.ReadInt32LittleEndian(pending.GetRange(0, 4).ToArray());
                        var length = BinaryPrimitives.ReadInt32LittleEndian(pending.GetRange(4, 4).ToArray());
                        if (pending.Count < 8 + length) break;
                        var body = JsonNode.Parse(Encoding.UTF8.GetString(pending.GetRange(8, length).ToArray()));
                        pending.RemoveRange(0, 8 + length);
                        if (op == 0)
                        {
                            HandshakeSeen.TrySetResult();
                            await handshakeRelease.Task;
                            await client.SendAsync(Frame(0, new JsonObject { ["evt"] = "READY" }), SocketFlags.None);
                            readyClient.TrySetResult(client);
                        }
                        else if (op == 4)
                            lock (pongs) pongs.Add(body?.DeepClone());
                        else if (body?["cmd"]?.GetValue<string>() == "SET_ACTIVITY")
                            lock (activities) activities.Add(body["args"]?["activity"]?.DeepClone());
                    }
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }

        private static byte[] Frame(int op, JsonNode payload)
        {
            var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
            var frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, op);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
            body.CopyTo(frame, 8);
            return frame;
        }

        public void Dispose()
        {
            listener.Dispose();
            handshakeRelease.TrySetResult();
            CloseClients();
        }
    }
}