using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>The host's record of terms a user accepted, per server and installation fingerprint (H21); the provider only reads the terms.</summary>
internal static class LanguageTerms
{
    private sealed record Acceptance(string ArtifactId, string Version, string EulaSha256, string AcceptedAtUtc);

    private static string Record(string storage, string server, string artifact) => Path.Combine(storage, "agreements", server, artifact + ".json");

    internal static bool Accepted(string storage, string server, LanguageServerTerms terms) => AcceptedAt(storage, server, terms) is not null;

    private static string? AcceptedAt(string storage, string server, LanguageServerTerms terms)
    {
        var record = Record(storage, server, terms.ArtifactId);
        if (!File.Exists(record)) return null;
        Acceptance? accepted;
        try { accepted = JsonSerializer.Deserialize<Acceptance>(File.ReadAllText(record)); }
        catch (JsonException) { return null; }
        return accepted is not null && accepted.ArtifactId == terms.ArtifactId && accepted.Version == terms.Version && accepted.EulaSha256 == terms.TextSha256 &&
            DateTimeOffset.TryParse(accepted.AcceptedAtUtc, out _) ? accepted.AcceptedAtUtc : null;
    }

    internal static async Task<LanguageTermsResult> ReviewAsync(string storage, LanguageServer server, ILanguageServerInstallation installation, LanguageTermsRequest request, CancellationToken token)
    {
        if (request.Accept && (request.ArtifactId is not { Length: 64 } || request.EulaSha256 is not { Length: 64 }))
            throw new ArgumentException("Acceptance requires the exact artifact and full terms hash shown to the user.");
        try
        {
            // Review may prepare the installation, but never executes it or infers consent.
            var terms = await server.Spec.TermsAsync!(installation, !request.Accept, token);
            var result = new LanguageTermsResult("requiresAcceptance", terms.Installation, terms.Version, terms.ArtifactId, terms.TextSha256, terms.Text);
            if (AcceptedAt(storage, server.Id, terms) is { } at) result = result with { State = "accepted", Accepted = true, AcceptedAtUtc = at };
            if (!request.Accept) return result;
            if (request.ArtifactId != terms.ArtifactId || request.EulaSha256 != terms.TextSha256)
                return result with { State = "changed", Detail = "The installation or terms changed. Read the updated text and accept again.", Accepted = false, AcceptedAtUtc = "" };
            if (result.Accepted) return result;
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            var record = Record(storage, server.Id, terms.ArtifactId);
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            var temporary = record + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Acceptance(terms.ArtifactId, terms.Version, terms.TextSha256, timestamp)), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, record, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result with { State = "accepted", Accepted = true, AcceptedAtUtc = timestamp };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or DecoderFallbackException or PlatformNotSupportedException)
        {
            return new("unavailable", error.Message);
        }
    }
}

/// <summary>A server with terms whose current terms were not accepted.</summary>
internal sealed class LanguageTermsRequiredException(LanguageServer server)
    : IOException($"Review and accept the {(server.Spec.Name.Length > 0 ? server.Spec.Name : server.Id)} terms in workspace settings before starting it.");

public sealed partial class LanguageService
{
    internal async ValueTask<LanguageTermsResult> TermsAsync(LanguageTermsRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace)) throw new ArgumentException("Terms review requires an existing absolute workspace directory.");
        var server = servers.Servers.FirstOrDefault(item => item.Id == request.Server && item.Spec.TermsAsync is not null)
            ?? throw new ArgumentException("Select a server whose installation has terms.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        var options = WorkspaceLanguageConfiguration.Parse(configuration(workspace, server.Id), server.Id);
        return await LanguageTerms.ReviewAsync(servers.Storage, server, servers.InstallationFor(server, workspace, options.InstallationPath), request, token);
    }
}