using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Server.Kestrel.Core;

using ProtoBuf.Grpc.Server;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;

namespace SharpRail.Host.Remote;

public static class RemoteServer
{
    /// <summary>Starts a host; shared state persists in <paramref name="stateDirectory"/>, or only in memory without one.
    /// The host owns its terminals unless the caller supplies and owns them. External plugins install under
    /// <c>&lt;stateDirectory&gt;/plugins</c>; <paramref name="plugins"/> adjusts the runtime's seams, as checks do.</summary>
    public static WebApplication Create(string root, IPAddress address, int port, string token, string? stateDirectory = null, ITerminalService? terminals = null,
        Func<PluginHostSeams, PluginHostSeams>? plugins = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = FileLimits.SaveMessageBytes;
            options.Listen(address, port, endpoint => endpoint.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddSingleton<IWorkspaceHost>(new WorkspaceHost(root));
        var state = new HostStateStore(stateDirectory);
        builder.Services.AddSingleton<IHostStateService>(state);
        // Sessions belong to the host: they outlive client connections and end when the host stops.
        var pty = terminals as PtyTerminalService ?? (terminals is null && (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ? new PtyTerminalService(recordingsDirectory: stateDirectory is null ? null : Path.Combine(stateDirectory, "terminals"), replayBytes: () => state.Current.Settings.TerminalReplayKb * 1024) : null);
        if (terminals is null) builder.Services.AddSingleton<ITerminalService>(_ => pty ?? new PtyTerminalService());
        else builder.Services.AddSingleton(terminals);
        var loopback = new LoopbackServer(pty);
        var seams = new PluginHostSeams { StateDirectory = stateDirectory, State = state, PublicBaseUrl = () => loopback.BaseUrl, Terminals = pty };
        var runtime = new PluginRuntime(plugins?.Invoke(seams) ?? seams);
        loopback.Plugins = runtime;
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(new ProjectSessions(root, state, runtime.AllowsExternalFile));
        builder.Services.AddSingleton<RequestReplayCache>();
        builder.Services.AddSingleton(services => services.GetRequiredService<ITerminalService>() as ITerminalCatalogService ?? new MemoryTerminalCatalog());
        builder.Services.AddCodeFirstGrpc(options => options.MaxReceiveMessageSize = FileLimits.SaveMessageBytes);
        var app = builder.Build();
        RequireToken(app, token);
        app.MapGrpcService<WorkspaceRpc>();
        app.MapGrpcService<ProjectRpc>();
        app.MapGrpcService<StateRpc>();
        app.MapGrpcService<TerminalRpc>();
        app.MapGrpcService<TerminalCatalogRpc>();
        state.WorkspaceRemoved += path => _ = Task.Run(async () => await app.Services.GetRequiredService<ITerminalCatalogService>().CloseWorkspaceAsync(path));
        app.MapGrpcService<PluginRpc>();
        runtime.Start();
        // Terminals need the MCP route from their first shell; plugins start it themselves when they ask for its URL.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (pty is not null) _ = loopback.BaseUrl;
        });
        // Plugins stop first, before the terminals and the loopback server they reach.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            loopback.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
        return app;
    }

    private static void RequireToken(WebApplication app, string token)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"Bearer {token}"));
        app.Use(async (context, next) =>
        {
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
    }
}