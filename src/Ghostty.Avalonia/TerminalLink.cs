using Avalonia;
using Avalonia.Controls;

namespace Ghostty.Avalonia;

/// <summary>Opens links the terminal itself recognised, keeping file links inside the embedding application.</summary>
internal static class TerminalLinkLauncher
{
    /// <summary>The system launcher, replaceable by checks.</summary>
    internal static Func<Visual, Uri, Task> Launch = async (visual, uri) =>
    {
        if (TopLevel.GetTopLevel(visual)?.Launcher is { } launcher) await launcher.LaunchUriAsync(uri);
    };

    /// <summary>
    /// Web links go to the system. A file URL never does (that would reveal it in Finder or a default app): it opens
    /// through the resolver and the application's editor when it names an existing file on the terminal's host.
    /// </summary>
    internal static async Task Open(Visual owner, string url, Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? resolve,
        Func<TerminalLink, Task>? open)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (!uri.IsFile) { await Launch(owner, uri); return; }
        if (resolve is null || open is null) return;
        // OSC 8 file links name the program's host (file://host/path); the terminal's host resolves the path.
        var local = new UriBuilder(uri) { Host = "" }.Uri.AbsoluteUri;
        var links = await Task.Run(() => resolve(local, CancellationToken.None));
        if (links.FirstOrDefault(link => link.Start == 0 && !link.IsDirectory) is { } target) await open(target);
    }
}

/// <summary>A host-resolved file reference, with UTF-16 offsets and one-based source coordinates.</summary>
public sealed record TerminalLink(int Start, int Length, string Path, int Line, int Column, bool IsDirectory);

internal sealed record TerminalContextLink(TerminalLink Link, Func<Task> Activate);

internal sealed class TerminalLinkHover(Action changed) : IDisposable
{
    internal Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? Resolve;
    internal Func<TerminalLink, Task>? Open;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? contextPending;
    private string? text;
    private int offset = -1;
    private IReadOnlyList<TerminalLink> links = [];
    private readonly Dictionary<string, (DateTime Time, IReadOnlyList<TerminalLink> Links)> cache = [];
    private DateTime requested;
    private bool disposed;
    internal TerminalLink? Link => links.FirstOrDefault(link => offset >= link.Start && offset < link.Start + link.Length);

    internal void Update(string? value, int index)
    {
        offset = index;
        if (disposed || Resolve is null || value is null)
        {
            pending?.Cancel(); pending = null; text = null; links = [];
            return;
        }
        if (text == value && (pending is not null || DateTime.UtcNow - requested < TimeSpan.FromSeconds(1))) return;
        pending?.Cancel(); pending = null;
        text = value; links = []; requested = DateTime.UtcNow;
        if (cache.TryGetValue(value, out var found) && requested - found.Time < TimeSpan.FromSeconds(1))
        { links = found.Links; return; }
        var call = new CancellationTokenSource();
        pending = call;
        _ = Find(value, call);
    }

    private async Task Find(string value, CancellationTokenSource call)
    {
        try
        {
            await Task.Delay(60, call.Token);
            var result = await Task.Run(() => Resolve!(value, call.Token), call.Token);
            if (disposed || call.IsCancellationRequested || pending != call) return;
            links = result;
            requested = DateTime.UtcNow;
            if (cache.Count >= 16) cache.Clear();
            cache[value] = (DateTime.UtcNow, result);
            pending = null;
            changed();
        }
        catch (Exception) { if (pending == call) { pending = null; links = []; } }
        finally { call.Dispose(); }
    }

    internal async Task<TerminalContextLink?> ResolveContext()
    {
        contextPending?.Cancel();
        if (disposed || text is not { } value || Resolve is not { } resolve || Open is not { } open) return null;
        var index = offset;
        using var call = new CancellationTokenSource();
        contextPending = call;
        try
        {
            var links = await Task.Run(() => resolve(value, call.Token), call.Token);
            if (disposed || call.IsCancellationRequested || contextPending != call) return null;
            var link = links.FirstOrDefault(link => index >= link.Start && index < link.Start + link.Length);
            return link is null ? null : new(link, async () =>
            {
                if (disposed) return;
                try
                {
                    var current = await Task.Run(() => resolve(value, CancellationToken.None));
                    if (!disposed && current.Contains(link)) await open(link);
                }
                catch (Exception) { }
            });
        }
        catch (Exception) { return null; }
        finally { if (contextPending == call) contextPending = null; }
    }

    internal async Task Activate(TerminalLink link)
    {
        if (disposed || text is not { } current || Resolve is null || Open is null) return;
        // Recheck before opening: a cached file can have disappeared or the shell can have changed directory.
        var result = await Task.Run(() => Resolve(current, CancellationToken.None));
        if (!disposed && text == current && result.Contains(link)) await Open(link);
    }

    public void Dispose()
    {
        disposed = true; contextPending?.Cancel(); contextPending = null; pending?.Cancel(); pending = null; cache.Clear(); links = [];
    }
}