using System.Runtime.InteropServices.JavaScript;

using Tereditor.Plugins.Pi.Host;

namespace Tereditor.UI.Browser;

/// <summary>Pi running inside this tab (<c>tabpi</c> in <c>tabhost.js</c>), reached as the lines it would have written to a pipe.</summary>
internal sealed partial class TabPiLink : IPiLink
{
    /// <summary>Where the tab keeps Pi's credentials, shared with Settings › Model providers.</summary>
    public const string AgentDirectory = "/home/tab/.pi/agent";

    [JSImport("piOpen", "tabhost")]
    private static partial Task<string> Open(string cwd, string authPath, string? model, string? thinking);
    [JSImport("piVersion", "tabhost")]
    private static partial Task<string?> Version();
    [JSImport("piSend", "tabhost")]
    private static partial void Send(string id, string line);
    [JSImport("piNext", "tabhost")]
    private static partial Task<string?> Next(string id);
    [JSImport("piClose", "tabhost")]
    private static partial void End(string id);

    private readonly string id;
    private int ended;

    private TabPiLink(string id) => this.id = id;

    public static async Task<IPiLink> StartAsync(string cwd, string? model, string? thinking, CancellationToken cancellationToken)
    {
        var id = await TabGit.OnPage(() => Open(cwd, AgentDirectory + "/auth.json", model, thinking)).WaitAsync(cancellationToken);
        return new TabPiLink(id);
    }

    /// <summary>The version of the Pi bundled into the page.</summary>
    public static Task<string?> VersionAsync() => TabGit.OnPage(Version);

    public bool Alive => Volatile.Read(ref ended) == 0;

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await TabGit.OnPage(() => Next(id)).WaitAsync(cancellationToken);
        if (line is null) Interlocked.Exchange(ref ended, 1);
        return line;
    }

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!Alive) throw new InvalidOperationException("Pi is not running.");
        return new(TabGit.OnPage(() => { Send(id, line); return Task.FromResult(0); }));
    }

    public ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref ended, 1) == 1) return ValueTask.CompletedTask;
        return new(TabGit.OnPage(() => { End(id); return Task.FromResult(0); }));
    }

    public void Dispose() => _ = CloseAsync();
}
