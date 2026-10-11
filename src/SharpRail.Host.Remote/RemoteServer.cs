using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Server.Kestrel.Core;

using ProtoBuf.Grpc.Server;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Tailscale;

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
        var builder = Builder(address, port);
        builder.Services.AddSingleton<IWorkspaceHost>(new WorkspaceHost(root));
        var state = new HostStateStore(stateDirectory);
        builder.Services.AddSingleton<IHostStateService>(state);
        // Sessions belong to the host: they outlive client connections and end when the host stops.
        var pty = terminals as PtyTerminalService ?? (terminals is null && (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) ? new PtyTerminalService(recordingsDirectory: stateDirectory is null ? null : Path.Combine(stateDirectory, "terminals"), replayBytes: () => state.Current.Settings.TerminalReplayKb * 1024) : null);
        state.Terminals = pty;
        if (terminals is null) builder.Services.AddSingleton<ITerminalService>(_ => pty ?? new PtyTerminalService());
        else builder.Services.AddSingleton(terminals);
        var loopback = new LoopbackServer(pty, state);
        var tailscale = new TailscaleServing();
        var seams = new PluginHostSeams { StateDirectory = stateDirectory, State = state, PublicBaseUrl = () => loopback.BaseUrl, Terminals = pty, Builtins = BuiltinPlugins.Create(tailscale) };
        var runtime = new PluginRuntime(plugins?.Invoke(seams) ?? seams);
        loopback.Plugins = runtime;
        builder.Services.AddSingleton(runtime);
        builder.Services.AddSingleton(new ProjectSessions(root, state, runtime.AllowsExternalFile));
        builder.Services.AddSingleton<RequestReplayCache>();
        builder.Services.AddSingleton(services => services.GetRequiredService<ITerminalService>() as ITerminalCatalogService ?? new MemoryTerminalCatalog());
        builder.Services.AddCodeFirstGrpc(options => options.MaxReceiveMessageSize = FileLimits.SaveMessageBytes);
        var app = builder.Build();
        Map(app, token, tailscale);
        tailscale.EnsureListening = (tailnetAddress, _) =>
        {
            if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && address.ToString() != tailnetAddress)
                throw new IOException("Start this host with SHARPRAIL_BIND set to its Tailscale address.");
            var endpoint = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
                .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
            return ValueTask.FromResult(new UriBuilder("http", tailnetAddress, new Uri(endpoint).Port).Uri.ToString());
        };
        state.WorkspaceRemoved += path => _ = Task.Run(async () => await app.Services.GetRequiredService<ITerminalCatalogService>().CloseWorkspaceAsync(path));
        runtime.Start();
        // Terminals need the MCP route from their first shell; plugins start it themselves when they ask for its URL.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (pty is not null) _ = loopback.BaseUrl;
        });
        // Plugins stop first, before the terminals and the loopback server they reach.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            Task.Run(async () =>
            {
                await runtime.DisposeAsync();
                await loopback.DisposeAsync();
            }).GetAwaiter().GetResult();
        });
        return app;
    }

    /// <summary>Exposes an existing host. Stopping or disposing this listener never disposes the supplied
    /// state, terminals or plugins; their owner continues to serve direct local calls.</summary>
    public static WebApplication CreateListener(string root, IPAddress address, int port, string token,
        HostStateStore state, ITerminalService? terminals, PluginRuntime plugins, TailscaleServing? tailscale = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        var builder = Builder(address, port, embedded: true);
        builder.Services.AddSingleton<IHostLifetime, ListenerLifetime>();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddSingleton<IWorkspaceHost>(new WorkspaceHost(root));
        builder.Services.AddSingleton<IHostStateService>(state);
        builder.Services.AddSingleton<ITerminalService>(terminals ?? new UnavailableTerminals());
        builder.Services.AddSingleton(services => services.GetRequiredService<ITerminalService>() as ITerminalCatalogService ?? new MemoryTerminalCatalog());
        builder.Services.AddSingleton<RequestReplayCache>();
        builder.Services.AddSingleton(plugins);
        builder.Services.AddSingleton(new ProjectSessions(root, state, plugins.AllowsExternalFile));
        builder.Services.AddCodeFirstGrpc(options => options.MaxReceiveMessageSize = FileLimits.SaveMessageBytes);
        var app = builder.Build();
        Map(app, token, tailscale);
        return app;
    }

    private static WebApplicationBuilder Builder(IPAddress address, int port, bool embedded = false)
    {
        var builder = embedded ? WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = ["--hostBuilder:reloadConfigOnChange=false"]
        }) : WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = FileLimits.SaveMessageBytes;
            options.Listen(address, port, endpoint => endpoint.Protocols = HttpProtocols.Http2);
        });
        return builder;
    }

    private static void Map(WebApplication app, string token, TailscaleServing? tailscale)
    {
        RequireToken(app, token, tailscale);
        app.MapGrpcService<WorkspaceRpc>();
        app.MapGrpcService<ProjectRpc>();
        app.MapGrpcService<StateRpc>();
        app.MapGrpcService<TerminalRpc>();
        app.MapGrpcService<PluginRpc>();
        app.MapGrpcService<TerminalCatalogRpc>();
    }

    private sealed class ListenerLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnavailableTerminals : ITerminalService
    {
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) =>
            throw new PlatformNotSupportedException("This host does not support terminals.");
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static void RequireToken(WebApplication app, string token, TailscaleServing? tailscale)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"Bearer {token}"));
        app.Use(async (context, next) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(authorization));
            var paired = authorization.StartsWith("Bearer ", StringComparison.Ordinal) && tailscale?.Authenticate?.Invoke(authorization[7..]) == true;
            if (!CryptographicOperations.FixedTimeEquals(expected, actual) && !paired)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!paired) { await next(context); return; }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var watching = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                        if (tailscale?.Authenticate?.Invoke(authorization[7..]) == true) continue;
                        context.Abort();
                        return;
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            });
            try { await next(context); }
            finally { await lifetime.CancelAsync(); await watching; }
        });
    }
}