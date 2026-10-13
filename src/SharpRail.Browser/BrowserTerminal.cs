using System.Runtime.InteropServices.JavaScript;

using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Terminal;

namespace SharpRail.UI.Browser;

internal sealed class BrowserTerminal : Border, ITerminalBackend
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITerminalBackend? backend;
    private bool agentNewline;
    private bool disposed;

    internal BrowserTerminal(TerminalLaunch launch, ITerminalService terminals) => Started = Start(launch, terminals);
    public Control View => this;
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;
    public bool Yielded => backend?.Yielded ?? false;
    public bool Watching => backend?.Watching ?? false;

    private async Task Start(TerminalLaunch launch, ITerminalService terminals)
    {
        try
        {
            using var loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            loading.CancelAfter(TimeSpan.FromSeconds(30));
            await JSHost.ImportAsync("restty-bridge", "../restty-bridge.js", loading.Token).WaitAsync(loading.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            var restty = new ResttyTerminal(launch, terminals);
            backend = restty;
            Child = restty.View;
            await restty.RendererReady.WaitAsync(loading.Token);
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            backend?.Dispose();
            backend = new SkiaTerminal(launch, terminals);
            Child = backend.View;
        }
        lifetime.Token.ThrowIfCancellationRequested();
        backend!.SetAgentNewline(agentNewline);
        // Host attachment and streaming failures are not renderer-loading failures.
        await backend.Started;
        _ = Observe();
    }

    private async Task Observe()
    {
        try
        {
            await Task.WhenAny(backend!.Exited, backend.Detached).WaitAsync(lifetime.Token);
            if (backend.Detached.IsCompleted) detached.TrySetResult();
            else exited.TrySetResult(await backend.Exited);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { exited.TrySetException(error); }
    }

    public ValueTask<bool> IsBusyAsync() => backend?.IsBusyAsync() ?? ValueTask.FromResult(false);
    public ValueTask CloseAsync() => backend?.CloseAsync() ?? ValueTask.CompletedTask;
    public void FocusTerminal() => backend?.FocusTerminal();
    public void Write(string data) => backend?.Write(data);
    public string ReadScreen() => backend?.ReadScreen() ?? "";
    public void SetAgentNewline(bool enabled) { agentNewline = enabled; backend?.SetAgentNewline(enabled); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        backend?.Dispose();
    }
}