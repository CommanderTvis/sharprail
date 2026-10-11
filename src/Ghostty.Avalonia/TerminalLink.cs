namespace Ghostty.Avalonia;

/// <summary>A host-resolved file reference, with UTF-16 offsets and one-based source coordinates.</summary>
public sealed record TerminalLink(int Start, int Length, string Path, int Line, int Column, bool IsDirectory);

internal sealed class TerminalLinkHover(Action changed) : IDisposable
{
    internal Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? Resolve;
    internal Func<TerminalLink, Task>? Open;
    private CancellationTokenSource? pending;
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

    internal async Task Activate(TerminalLink link)
    {
        if (disposed || text is not { } current || Resolve is null || Open is null) return;
        // Recheck before opening: a cached file can have disappeared or the shell can have changed directory.
        var result = await Task.Run(() => Resolve(current, CancellationToken.None));
        if (!disposed && text == current && result.Contains(link)) await Open(link);
    }

    public void Dispose()
    {
        disposed = true; pending?.Cancel(); pending = null; cache.Clear(); links = [];
    }
}