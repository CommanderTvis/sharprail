using Grpc.Core;

using ProtoBuf.Grpc;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed class StateRpc(IHostStateService host, IHostApplicationLifetime lifetime, RequestReplayCache replay) : IStateRpc
{
    public async ValueTask<HandshakeReply> HandshakeAsync(HandshakeRequest request, CallContext context = default)
    {
        var handshake = await host.GetHandshakeAsync(context.CancellationToken);
        return new() { ProtocolVersion = handshake.ProtocolVersion, HostVersion = handshake.HostVersion };
    }

    public async ValueTask<StateReply> GetStateAsync(StateRequest request, CallContext context = default)
        => Map(await host.GetStateAsync(context.CancellationToken));

    public ValueTask<StateReply> ChangeAsync(StateChangeRequest request, CallContext context = default) => replay.RunAsync(context, request, async token =>
    {
        try
        {
            return Map(await host.ChangeAsync(request.Changes.Select(change => new HostStateChange(change.Kind, change.Key, change.Value)).ToArray(), token));
        }
        catch (HostException error) { throw HostErrors.ToRpc(error); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    });

    public async IAsyncEnumerable<StateReply> WatchAsync(StateRequest request, CallContext context = default)
    {
        // Streams end with the host so a graceful shutdown does not wait for watchers.
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        await foreach (var state in host.WatchAsync(watch.Token)) yield return Map(state);
    }

    public async IAsyncEnumerable<LifecycleMessage> WatchLifecycleAsync(StateRequest request, CallContext context = default)
    {
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        await foreach (var item in host.WatchLifecycleAsync(watch.Token))
            yield return new()
            {
                Channel = item.Channel,
                Kind = item.Kind,
                ProjectRoot = item.ProjectRoot,
                WorkspaceId = item.WorkspaceId,
                Workspace = item.Workspace is null ? null : WorkspaceMessages.Map(item.Workspace)
            };
    }

    private static StateReply Map(HostState state) => new()
    {
        Revision = state.Revision,
        Settings = new()
        {
            Theme = state.Settings.Theme,
            ThemeMode = state.Settings.ThemeMode,
            SystemLight = state.Settings.SystemLight,
            SystemDark = state.Settings.SystemDark,
            FileLineWidth = state.Settings.FileLineWidth,
            FileLineWidthUnbounded = !state.Settings.FileLineWidthBounded,
            MarkdownLineWidth = state.Settings.MarkdownLineWidth,
            MarkdownLineWidthUnbounded = !state.Settings.MarkdownLineWidthBounded,
            TerminalReplayKb = state.Settings.TerminalReplayKb
        },
        Presets = state.Presets.Select(preset => new PresetMessage { Name = preset.Name, Layout = preset.Layout }).ToList(),
        Projects = state.Projects.ToList(),
        RecentProjects = state.RecentProjects.ToList(),
        ProjectRecords = state.ProjectRecords.Select(record => new ProjectRecordMessage { Id = record.Id, Path = record.Path, Slug = record.Slug, LastOpened = record.LastOpened }).ToList(),
        Labels = state.WorkspaceLabels.Select(entry => new LabelMessage { Path = entry.Key, Label = entry.Value }).ToList(),
        Bases = state.WorkspaceBases.Select(entry => new WorkspaceRefMessage { Path = entry.Key, Reference = entry.Value }).ToList(),
        DiffBases = state.WorkspaceDiffBases.Select(entry => new WorkspaceRefMessage { Path = entry.Key, Reference = entry.Value }).ToList(),
        Workspaces = state.Workspaces.Select(WorkspaceMessages.Map).ToList(),
        PluginSettings = state.PluginSettings.Select(entry => new PluginSettingsMessage { Id = entry.Key, Json = entry.Value.GetRawText() }).ToList(),
        PluginPaths = state.PluginPaths.ToList(),
        Plugins = PluginWire.Map(state.Plugins),
        TerminalAgents = state.TerminalAgents.Select(agent => new TerminalAgentMessage
        {
            WorkspaceId = agent.Terminal.WorkspaceId,
            TabKey = agent.Terminal.TabKey,
            Kind = agent.Record.Kind,
            Command = agent.Record.Command,
            SessionId = agent.Record.SessionId ?? "",
            Cwd = agent.Record.Cwd ?? "",
            Model = agent.Record.Model ?? ""
        }).ToList(),
        TerminalTitles = state.TerminalTitles.Select(title => new TerminalTitleMessage
        { WorkspaceId = title.Terminal.WorkspaceId, TabKey = title.Terminal.TabKey, Title = title.Title }).ToList(),
        Platform = state.Platform is { } platform ? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(platform.ToString()) : ""
    };
}