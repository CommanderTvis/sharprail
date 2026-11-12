using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

/// <summary>
/// Files another app shared into this one (Android's share sheet), waiting for an agent terminal to take them.
/// The queue outlives workbenches, so an item shared before connecting is attached once one is ready.
/// </summary>
internal static class SharedAttachments
{
    private static readonly Lock gate = new();
    private static List<FileTransferEntry> pending = [];

    /// <summary>Raised on the UI thread after items were added.</summary>
    internal static event Action? Changed;

    internal static bool HasPending { get { lock (gate) return pending.Count > 0; } }

    /// <summary>Queues entries from any thread.</summary>
    internal static void Add(IReadOnlyList<FileTransferEntry> entries)
    {
        if (entries.Count == 0) return;
        lock (gate) pending.AddRange(entries);
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    internal static IReadOnlyList<FileTransferEntry> Take()
    {
        lock (gate) { var taken = pending; pending = []; return taken; }
    }
}