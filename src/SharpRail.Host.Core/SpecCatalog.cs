using System.Collections.Concurrent;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>The spec indexes of the workspaces this host has read, one per root, dropped when a workspace is removed.</summary>
internal static class SpecCatalog
{
    private static readonly ConcurrentDictionary<string, SpecIndex> Indexes = new(StringComparer.Ordinal);

    internal static SpecIndex For(string root) => Indexes.GetOrAdd(root, path => new SpecIndex(path));

    internal static void Evict(string root) => Indexes.TryRemove(root, out _);

    internal static bool IsIndexed(string root) => Indexes.ContainsKey(root);

    internal static Task<SpecGraph> GraphAsync(string root, CancellationToken cancellationToken) =>
        Task.Run(() => For(root).Graph(cancellationToken), cancellationToken);
}