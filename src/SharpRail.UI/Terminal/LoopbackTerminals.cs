using SharpRail.Host.Abstractions;
using SharpRail.Host.Remote;

namespace SharpRail.UI.Terminal;

// The app's own terminal sessions, starting the host's loopback server before the first shell so every shell
// gets the MCP route. Starting it binds a socket, so it runs off the caller's thread.
public sealed class LoopbackTerminals(ITerminalService terminals, LoopbackServer loopback) : ITerminalService
{
    public async ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => loopback.BaseUrl, cancellationToken);
        return await terminals.AttachAsync(request, cancellationToken);
    }

    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => terminals.IsBusyAsync(sessionId, cancellationToken);
    public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => terminals.CloseAsync(sessionId, cancellationToken);
}