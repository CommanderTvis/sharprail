using System.Net;
using System.Text;
using System.Threading.Channels;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class BrowserTransportChecks
{
    public static async Task Run(string root)
    {
        await BrowserAssetsChecks.Run(root);
        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "browser-check", stateDirectory: Path.Combine(root, "browser-transport-state"));
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
            using var invoker = new RpcChannel(new Uri(addresses[0]));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var context = new CallContext(new CallOptions(new Metadata { { "authorization", "Bearer browser-check" } }, cancellationToken: timeout.Token));
            var states = invoker.CreateService<IStateRpc>();
            var hello = await states.HandshakeAsync(new(), context);
            Require(hello.ProtocolVersion > 0, "The browser transport must preserve unary protobuf replies.");
            try
            {
                await states.HandshakeAsync(new(), new CallContext(new CallOptions(new Metadata { { "authorization", "Bearer wrong" } }, cancellationToken: timeout.Token)));
                throw new InvalidOperationException("A browser call accepted an invalid token.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }

            var workspace = invoker.CreateService<IWorkspaceRpc>();
            Require((await workspace.GetWorkspaceAsync(new(), context)).RootPath == root, "The browser workspace must remain on the host.");
            var projects = invoker.CreateService<IProjectRpc>();
            try
            {
                await projects.UndoChangeAsync(new() { ReceiptId = "unknown-browser-receipt" }, context);
                throw new InvalidOperationException("A browser call accepted an unknown undo receipt.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
            {
                Require(error.Trailers.GetValue(ProjectHeaders.ChangeCode) == "ReceiptUnknown", "Browser failures must preserve domain error trailers.");
            }
            var savePath = Path.Combine(root, "browser-save.txt");
            await File.WriteAllTextAsync(savePath, "before", timeout.Token);
            try
            {
                var savedText = new string('x', 200_000) + "🚀";
                var save = new SaveFileRequest { WorkspaceRoot = root, Path = "browser-save.txt", OriginalText = "before", Text = savedText };
                var replay = new CallContext(new CallOptions(new Metadata { { "authorization", "Bearer browser-check" }, { ReplayHeaders.Client, "browser-check" }, { ReplayHeaders.Request, "save-1" } }, cancellationToken: timeout.Token));
                await projects.SaveFileAsync(save, replay);
                await projects.SaveFileAsync(save, replay);
                Require(await File.ReadAllTextAsync(savePath, timeout.Token) == savedText, "Fragmented browser messages and mutation replay must save a UTF-8 file once.");
                try
                {
                    await projects.SaveFileAsync(save, context);
                    throw new InvalidOperationException("A browser save overwrote an externally changed file.");
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition) { }
            }
            finally { File.Delete(savePath); }
            await using (var deadline = states.WatchAsync(new(), new CallContext(new CallOptions(new Metadata { { "authorization", "Bearer browser-check" } }, deadline: DateTime.UtcNow.AddSeconds(1)))).GetAsyncEnumerator())
            {
                Require(await deadline.MoveNextAsync(), "A browser deadline subscription must start.");
                try
                {
                    await deadline.MoveNextAsync();
                    throw new InvalidOperationException("A browser RPC ignored its deadline.");
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.DeadlineExceeded) { }
            }
            await using var watch = states.WatchAsync(new(), context).GetAsyncEnumerator(timeout.Token);
            Require(await watch.MoveNextAsync(), "Browser state subscriptions must receive an initial snapshot.");
            var terminals = invoker.CreateService<ITerminalRpc>();
            var input = Channel.CreateUnbounded<TerminalInput>();
            var session = "browser-check-" + Guid.NewGuid().ToString("N");
            input.Writer.TryWrite(new() { Kind = TerminalInputKind.Attach, SessionId = session, ClientId = "browser", WorkspaceRoot = root, Columns = 80, Rows = 24 });
            await using var output = terminals.RunAsync(input.Reader.ReadAllAsync(timeout.Token), context).GetAsyncEnumerator(timeout.Token);
            Require(await output.MoveNextAsync() && output.Current.Attached, "Browser duplex calls must attach to host terminals.");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7l8AAAAASUVORK5CYII=");
            var clipboard = await terminals.SaveClipboardImageAsync(new() { SessionId = session, Png = png }, context);
            try
            {
                Require((await File.ReadAllBytesAsync(clipboard.Text, timeout.Token)).SequenceEqual(png), "Browser clipboard image bytes must be saved on the shell's host.");
                if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(clipboard.Text) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Host clipboard images must remain private.");
            }
            finally { File.Delete(clipboard.Text); }
            input.Writer.TryWrite(new() { Kind = TerminalInputKind.Data, Data = Encoding.UTF8.GetBytes("printf 'browser-duplex-check\\n'\n") });
            var text = new StringBuilder();
            while (!text.ToString().Contains("browser-duplex-check", StringComparison.Ordinal))
            {
                Require(await output.MoveNextAsync(), "The browser terminal ended before its output.");
                text.Append(Encoding.UTF8.GetString(output.Current.Data));
            }
            await terminals.CloseAsync(new() { SessionId = session }, context);
            input.Writer.TryComplete();
            Console.WriteLine("PASS browser authentication, protobuf fragmentation, replay, errors/trailers, deadlines, state watch, host clipboard and duplex PTYs");
        }
        finally { await app.StopAsync(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}