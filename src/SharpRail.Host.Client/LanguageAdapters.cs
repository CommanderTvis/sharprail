using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

public sealed partial class LocalProjectAdapter
{
    public ValueTask<LanguageTermsResult> LanguageTermsAsync(LanguageTermsRequest request, CancellationToken cancellationToken = default)
        => OffDispatcher.Run(() => host.LanguageTermsAsync(request, cancellationToken));

    public ValueTask<LanguageResult> LanguageAsync(LanguageRequest request, CancellationToken cancellationToken = default)
        => OffDispatcher.Run(() => host.LanguageAsync(request, cancellationToken));
}

public sealed partial class RemoteProjectAdapter
{
    public async ValueTask<LanguageTermsResult> LanguageTermsAsync(LanguageTermsRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await service.LanguageTermsAsync(new() { Workspace = request.Workspace, Server = request.Server, Accept = request.Accept, ArtifactId = request.ArtifactId, EulaSha256 = request.EulaSha256 }, Context(cancellationToken, deadline: TimeSpan.FromMinutes(15)));
            return new(reply.State, reply.Detail, reply.Version, reply.ArtifactId, reply.EulaSha256, reply.Text, reply.Accepted, reply.AcceptedAtUtc);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { return new("unavailable", "This host does not support terms review."); }
        catch (RpcException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    public async ValueTask<LanguageResult> LanguageAsync(LanguageRequest request, CancellationToken cancellationToken = default)
    {
        LanguageReply reply;
        try
        {
            reply = await service.LanguageAsync(new()
            {
                Workspace = request.Workspace,
                Path = request.Path,
                Operation = request.Operation,
                Line = request.Line,
                Column = request.Column,
                Text = request.Text,
                DocumentId = request.DocumentId,
                Version = request.Version,
                Query = request.Query,
                Dirty = request.Dirty,
                Server = request.Server,
                Scope = request.Scope,
                Paths = request.Paths?.ToList() ?? [],
                Offset = request.Offset,
                Configuration = request.Configuration,
                Limit = request.Limit
            }, Context(cancellationToken, deadline: TimeSpan.FromMinutes(5)));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented) { return new("unavailable", "This host does not provide language services."); }
        catch (RpcException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        return new(reply.State, reply.Detail, reply.Server, reply.Version)
        {
            Projects = reply.Projects.Select(item => new LanguageProject(item.Root, item.Manifest, item.Server, item.Kind, item.State, item.Detail)).ToArray(),
            SemanticColoring = reply.SemanticColoring,
            Configuration = reply.Configuration,
            ActiveConfiguration = reply.ActiveConfiguration,
            Total = reply.Total,
            Truncated = reply.Truncated,
            Files = reply.Files.Select(item => new LanguageFileResult(item.Path, item.State, item.Freshness, item.Detail)).ToArray(),
            Servers = reply.Servers.Select(item => new LanguageServerState(item.Id, item.Path, item.State, item.Detail, item.Version, item.Configuration, item.ActiveConfiguration, item.Configured) { Workspace = item.Workspace, MemoryBytes = item.MemoryBytes, IdleSeconds = item.IdleSeconds, Presentation = item.Presentation is { } shown ? new LanguageServerPresentation(shown.Name, shown.Icon, shown.IconFormat, shown.OptionGroups, shown.Replaces, shown.HasTerms, shown.TermsNote, shown.ReplacesLabel, shown.TermsDeadline) : null, Exit = item.Exit is { } exit ? new LanguageExit(exit.Time, exit.Reason, exit.Unexpected, exit.Initiator, exit.HasExitCode ? exit.ExitCode : null, exit.Signal, exit.UptimeSeconds, exit.MemoryBytes, exit.Details) : null, Dependencies = item.DependenciesProbed ? item.Dependencies.Select(need => new LanguageDependencyState(need.Executable, need.Name, need.State, need.Path, need.Source, need.InstallHint, need.Problem, need.Override, need.Required)).ToArray() : null }).ToArray(),
            Locations = reply.Locations.Select(MapLanguage).ToArray(),
            Diagnostics = reply.Diagnostics.Select(MapLanguage).ToArray(),
            Tokens = reply.Tokens.Select(MapLanguage).ToArray(),
            Actions = reply.Actions.Select(MapLanguage).ToArray(),
            Edits = reply.Edits.Select(MapLanguage).ToArray(),
            Freshness = reply.Freshness,
            Coverage = reply.Coverage
        };
    }

    private static LanguageItem MapLanguage(LanguageItemDto item)
        => new(item.Path, item.Line, item.Column, item.EndLine, item.EndColumn, item.Text, item.Kind, item.Source, item.Preview, item.Body, item.Truncated) { Modifiers = item.Modifiers, Container = item.Container };
}