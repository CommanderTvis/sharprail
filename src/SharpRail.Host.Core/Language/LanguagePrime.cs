using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>Binds a server's <see cref="LanguageServerSpec.PrimeWorkspaceAsync"/> to the checkouts and its per-workspace state directories.</summary>
internal sealed record LanguagePrime(string Source, string Target, string SourceState, string TargetState) : ILanguageServerPrime
{
    /// <summary>The primer <see cref="WorkspaceClone.StartConfigured"/> runs for <paramref name="server"/>, with state under <paramref name="storage"/>.</summary>
    internal static Func<string, string, CancellationToken, Task> For(LanguageServer server, string storage) => (source, target, cancellationToken) =>
        server.Spec.PrimeWorkspaceAsync!(new LanguagePrime(source, target, State(storage, server, source), State(storage, server, target)), cancellationToken);

    // The same directory ILanguageServerStart.WorkspaceState names for that workspace.
    private static string State(string storage, LanguageServer server, string workspace) => Path.Combine(storage, "workspaces", LanguageServers.Key(workspace), server.Id);
}