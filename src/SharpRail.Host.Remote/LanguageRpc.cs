using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<IntelliJTermsReply> IntelliJTermsAsync(IntelliJTermsRequestDto request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).IntelliJTermsAsync(new(request.Workspace, request.Accept, request.ArtifactId, request.EulaSha256), context.CancellationToken);
        return new IntelliJTermsReply { State = result.State, Detail = result.Detail, Version = result.Version, ArtifactId = result.ArtifactId, EulaSha256 = result.EulaSha256, Text = result.Text, Accepted = result.Accepted, AcceptedAtUtc = result.AcceptedAtUtc };
    });

    public ValueTask<LanguageReply> LanguageAsync(LanguageRequestDto request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).LanguageAsync(new(request.Workspace, request.Path, request.Operation, request.Line,
            request.Column, request.Text, request.DocumentId, request.Version, request.Query, request.Dirty, request.Server, request.Scope, request.Paths, request.Offset, request.Limit, request.Configuration), context.CancellationToken);
        return new LanguageReply
        {
            Projects = result.Projects.Select(item => new LanguageProjectDto { Root = item.Root, Manifest = item.Manifest, Server = item.Server, Kind = item.Kind, State = item.State, Detail = item.Detail }).ToList(),
            SemanticColoring = result.SemanticColoring,
            Configuration = result.Configuration,
            ActiveConfiguration = result.ActiveConfiguration,
            Total = result.Total,
            Truncated = result.Truncated,
            State = result.State,
            Detail = result.Detail,
            Server = result.Server,
            Version = result.Version,
            Files = result.Files.Select(item => new LanguageFileDto { Path = item.Path, State = item.State, Freshness = item.Freshness, Detail = item.Detail }).ToList(),
            Servers = result.Servers.Select(item => new LanguageServerDto { Id = item.Id, Path = item.Path, State = item.State, Detail = item.Detail, Version = item.Version, Configuration = item.Configuration, ActiveConfiguration = item.ActiveConfiguration, Configured = item.Configured }).ToList(),
            Locations = result.Locations.Select(MapLanguage).ToList(),
            Diagnostics = result.Diagnostics.Select(MapLanguage).ToList(),
            Tokens = result.Tokens.Select(MapLanguage).ToList(),
            Freshness = result.Freshness,
            Coverage = result.Coverage
        };
    });

    private static LanguageItemDto MapLanguage(LanguageItem item) => new()
    {
        Path = item.Path,
        Line = item.Line,
        Column = item.Column,
        EndLine = item.EndLine,
        EndColumn = item.EndColumn,
        Text = item.Text,
        Kind = item.Kind,
        Source = item.Source,
        Preview = item.Preview,
        Body = item.Body,
        Modifiers = item.Modifiers,
        Truncated = item.Truncated
    };
}