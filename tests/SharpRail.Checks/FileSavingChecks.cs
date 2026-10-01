using System.Net;

using Grpc.Core;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class FileSavingChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "editor-saves"); Directory.CreateDirectory(root);
        var core = new ProjectServices(root);
        await Exercise(new LocalProjectAdapter(core), root);
        await using var server = RemoteServer.Create(root, IPAddress.Loopback, 0, "editor-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "editor-test");
            await Exercise(remote, root);
            using var unauthorized = new RemoteProjectAdapter(new Uri(address), "wrong-token");
            try { await unauthorized.SaveFileAsync(new(root, "code.cs", "", "bad")); throw new InvalidOperationException("Unauthenticated save accepted."); }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS editor saves locally and over gRPC, conflict/workspace/path checks, encoding, permissions and cancellation");
    }

    private static async Task Exercise(IProjectServices host, string root)
    {
        const string original = "\uFEFF// café 😀\r\nclass A {}\r\n";
        var path = Path.Combine(root, "code.cs");
        await File.WriteAllTextAsync(path, original);
        UnixFileMode mode = default;
        if (!OperatingSystem.IsWindows())
        {
            mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, mode);
        }
        await host.OpenProjectAsync(root);
        var read = await host.ReadFileAsync("code.cs");
        Require(read.Text == original, "File read lost BOM or line endings.");
        var text = original.Replace("class A", "class B", StringComparison.Ordinal);
        await host.SaveFileAsync(new(root, "code.cs", original, text));
        Require((await host.ReadFileAsync("code.cs")).Text == text, "Saved UTF-8 text differs.");
        if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(path) == mode, "Save changed executable permissions.");
        await Reject(host, new(root, "code.cs", original, "stale"));
        await Reject(host, new(Path.Combine(root, "other"), "code.cs", text, "wrong workspace"));
        await Reject(host, new(root, "../outside.txt", "", "outside"));
        await Reject(host, new(root, "missing.cs", "", "created"));
        var link = Path.Combine(root, "link.cs");
        File.CreateSymbolicLink(link, path);
        try { await Reject(host, new(root, "link.cs", text, "linked")); }
        finally { File.Delete(link); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await host.SaveFileAsync(new(root, "code.cs", text, "canceled"), canceled.Token); throw new InvalidOperationException("Canceled save accepted."); }
        catch (OperationCanceledException) { }
        catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled) { }
        Require((await host.ReadFileAsync("code.cs")).Text == text, "A rejected save changed the file.");
        Require(!Directory.EnumerateFiles(root, ".sharprail-save-*").Any(), "Save left temporary files behind.");
        var large = new string('x', FileLimits.EditableBytes - 64) + "\n";
        await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), large);
        Require((await host.ReadFileAsync("large.txt")).Text.Length == large.Length, "A file near the editing limit could not be opened.");
        var edited = "y" + large[1..];
        await host.SaveFileAsync(new(root, "large.txt", large, edited));
        Require(File.ReadAllText(Path.Combine(root, "large.txt")) == edited, "A file near the editing limit could not be saved.");
        await Reject(host, new(root, "large.txt", edited, edited + new string('z', 128)));
        File.Delete(Path.Combine(root, "large.txt"));
    }

    private static async Task Reject(IProjectServices host, FileSaveRequest request)
    {
        try { await host.SaveFileAsync(request); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException || error is RpcException { StatusCode: StatusCode.FailedPrecondition }) { return; }
        throw new InvalidOperationException("Unsafe save was accepted.");
    }
}