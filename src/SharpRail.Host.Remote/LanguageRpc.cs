using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

public sealed partial class ProjectRpc
{
    public ValueTask<LanguageTermsReply> LanguageTermsAsync(LanguageTermsRequestDto request, CallContext context = default) => Execute(async () =>
    {
        var result = await Host(context).LanguageTermsAsync(new(request.Workspace, request.Server, request.Accept, request.ArtifactId, request.EulaSha256), context.CancellationToken);
        return new LanguageTermsReply { State = result.State, Detail = result.Detail, Version = result.Version, ArtifactId = result.ArtifactId, EulaSha256 = result.EulaSha256, Text = result.Text, Accepted = result.Accepted, AcceptedAtUtc = result.AcceptedAtUtc };
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
            Servers = result.Servers.Select(item => new LanguageServerDto { Id = item.Id, Path = item.Path, State = item.State, Detail = item.Detail, Version = item.Version, Configuration = item.Configuration, ActiveConfiguration = item.ActiveConfiguration, Configured = item.Configured, Presentation = item.Presentation is { } shown ? new LanguageServerPresentationDto { Name = shown.Name, Icon = shown.Icon, IconFormat = shown.IconFormat, OptionGroups = [.. shown.OptionGroups ?? []], Replaces = [.. shown.Replaces ?? []], HasTerms = shown.HasTerms, TermsNote = shown.TermsNote, ReplacesLabel = shown.ReplacesLabel } : null, Exit = item.Exit is { } exit ? new LanguageExitDto { Time = exit.Time, Reason = exit.Reason, Unexpected = exit.Unexpected, Initiator = exit.Initiator, HasExitCode = exit.ExitCode is not null, ExitCode = exit.ExitCode ?? 0, Signal = exit.Signal, UptimeSeconds = exit.UptimeSeconds, MemoryBytes = exit.MemoryBytes, Details = exit.Details } : null, DependenciesProbed = item.Dependencies is not null, Dependencies = [.. (item.Dependencies ?? []).Select(need => new LanguageDependencyDto { Executable = need.Executable, Name = need.Name, State = need.State, Path = need.Path, Source = need.Source, InstallHint = need.InstallHint, Problem = need.Problem, Override = need.Override, Required = need.Required })] }).ToList(),
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
        Container = item.Container,
        Truncated = item.Truncated
    };
}