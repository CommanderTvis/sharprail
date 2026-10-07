using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;

namespace SharpRail.Host.Remote;

/// <summary>A runtime-switchable gRPC listener over a caller-owned embedded host.</summary>
public sealed class HostListener(string root, HostStateStore state, ITerminalService? terminals, PluginRuntime plugins) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WebApplication? server;
    private Uri? endpoint;
    private bool disposed;

    public IPAddress Address { get; private set; } = IPAddress.Loopback;
    public int Port { get; private set; } = 54123;
    public string Token { get; private set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public Uri? Endpoint => Volatile.Read(ref endpoint);
    /// <summary>Raised after a listener starts or stops, on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Starts listening without moving the embedded client's adapters onto the wire.</summary>
    public Task StartAsync(IPAddress address, int port, string token, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (server is not null) throw new InvalidOperationException("The host is already listening.");
            var candidate = RemoteServer.CreateListener(root, address, port, token, state, terminals, plugins);
            try
            {
                await candidate.StartAsync(cancellationToken);
                var listening = candidate.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                server = candidate;
                Address = address; Port = new Uri(listening).Port; Token = token;
                Volatile.Write(ref endpoint, new Uri(listening));
            }
            catch
            {
                await candidate.DisposeAsync();
                throw;
            }
        }
        finally { gate.Release(); }
        Changed?.Invoke();
    }, cancellationToken);

    /// <summary>Disconnects remote clients while the embedded host and its shells continue running.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(cancellationToken);
        try { await StopCore(); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }, cancellationToken);

    private async Task StopCore()
    {
        if (server is not { } active) return;
        try { await active.StopAsync(); }
        finally
        {
            await active.DisposeAsync();
            server = null;
            Volatile.Write(ref endpoint, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; await StopCore().ConfigureAwait(false); }
        finally { gate.Release(); }
        Changed?.Invoke();
    }
}