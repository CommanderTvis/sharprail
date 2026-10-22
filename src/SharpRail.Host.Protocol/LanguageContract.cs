using ProtoBuf;

namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class LanguageRequestDto
{
    [ProtoMember(1)] public string Workspace { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
    [ProtoMember(3)] public string Operation { get; set; } = "inspect";
    [ProtoMember(4)] public int Line { get; set; }
    [ProtoMember(5)] public int Column { get; set; }
    [ProtoMember(6)] public string? Text { get; set; }
    [ProtoMember(7)] public string DocumentId { get; set; } = "";
    [ProtoMember(8)] public long Version { get; set; }
    [ProtoMember(9)] public string Query { get; set; } = "";
    [ProtoMember(10)] public bool Dirty { get; set; }
    [ProtoMember(11)] public string Server { get; set; } = "";
    [ProtoMember(12)] public string Scope { get; set; } = "";
    [ProtoMember(13)] public List<string> Paths { get; set; } = [];
    [ProtoMember(14)] public int Offset { get; set; }
    [ProtoMember(16)] public string? Configuration { get; set; }
    [ProtoMember(15), System.ComponentModel.DefaultValue(200)] public int Limit { get; set; } = 200;
}

[ProtoContract]
public sealed class LanguageItemDto
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public int Line { get; set; }
    [ProtoMember(3)] public int Column { get; set; }
    [ProtoMember(4)] public int EndLine { get; set; }
    [ProtoMember(5)] public int EndColumn { get; set; }
    [ProtoMember(6)] public string Text { get; set; } = "";
    [ProtoMember(7)] public string Kind { get; set; } = "";
    [ProtoMember(8)] public string Source { get; set; } = "";
    [ProtoMember(9)] public string Preview { get; set; } = "";
    [ProtoMember(10)] public string Body { get; set; } = "";
    [ProtoMember(11)] public bool Truncated { get; set; }
    [ProtoMember(12)] public string Modifiers { get; set; } = "";
}

[ProtoContract]
public sealed class LanguageServerDto
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
    [ProtoMember(3)] public string State { get; set; } = "";
    [ProtoMember(4)] public string Detail { get; set; } = "";
    [ProtoMember(5)] public string Version { get; set; } = "";
    [ProtoMember(6)] public string Configuration { get; set; } = "";
    [ProtoMember(7)] public string ActiveConfiguration { get; set; } = "";
    [ProtoMember(8)] public bool Configured { get; set; }
    [ProtoMember(9)] public LanguageExitDto? Exit { get; set; }
    [ProtoMember(10)] public LanguageServerPresentationDto? Presentation { get; set; }
    [ProtoMember(11)] public List<LanguageDependencyDto> Dependencies { get; set; } = [];
    [ProtoMember(12)] public bool DependenciesProbed { get; set; }
}

[ProtoContract]
public sealed class LanguageDependencyDto
{
    [ProtoMember(1)] public string Executable { get; set; } = "";
    [ProtoMember(2)] public string Name { get; set; } = "";
    [ProtoMember(3)] public string State { get; set; } = "";
    [ProtoMember(4)] public string Path { get; set; } = "";
    [ProtoMember(5)] public string Source { get; set; } = "";
    [ProtoMember(6)] public string InstallHint { get; set; } = "";
    [ProtoMember(7)] public string Problem { get; set; } = "";
    [ProtoMember(8)] public bool Override { get; set; }
    [ProtoMember(9)] public bool Required { get; set; }
}

[ProtoContract]
public sealed class LanguageServerPresentationDto
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public byte[]? Icon { get; set; }
    [ProtoMember(3)] public string IconFormat { get; set; } = "";
    [ProtoMember(4)] public List<string> OptionGroups { get; set; } = [];
    [ProtoMember(5)] public List<string> Replaces { get; set; } = [];
    [ProtoMember(6)] public bool HasTerms { get; set; }
    [ProtoMember(7)] public string TermsNote { get; set; } = "";
    [ProtoMember(8)] public string ReplacesLabel { get; set; } = "";
}

[ProtoContract]
public sealed class LanguageExitDto
{
    [ProtoMember(1)] public string Time { get; set; } = "";
    [ProtoMember(2)] public string Reason { get; set; } = "";
    [ProtoMember(3)] public bool Unexpected { get; set; }
    [ProtoMember(4)] public string Initiator { get; set; } = "";
    [ProtoMember(5)] public bool HasExitCode { get; set; }
    [ProtoMember(6)] public int ExitCode { get; set; }
    [ProtoMember(7)] public string Signal { get; set; } = "";
    [ProtoMember(8)] public int UptimeSeconds { get; set; }
    [ProtoMember(9)] public long MemoryBytes { get; set; }
    [ProtoMember(10)] public string Details { get; set; } = "";
}

[ProtoContract]
public sealed class LanguageFileDto
{
    [ProtoMember(1)] public string Path { get; set; } = "";
    [ProtoMember(2)] public string State { get; set; } = "";
    [ProtoMember(3)] public string Freshness { get; set; } = "";
    [ProtoMember(4)] public string Detail { get; set; } = "";
}

[ProtoContract]
public sealed class LanguageProjectDto
{
    [ProtoMember(1)] public string Root { get; set; } = "";
    [ProtoMember(2)] public string Manifest { get; set; } = "";
    [ProtoMember(3)] public string Server { get; set; } = "";
    [ProtoMember(4)] public string Kind { get; set; } = "";
    [ProtoMember(5)] public string State { get; set; } = "";
    [ProtoMember(6)] public string Detail { get; set; } = "";
}

[ProtoContract]
public sealed class LanguageReply
{
    [ProtoMember(17)] public bool SemanticColoring { get; set; }
    [ProtoMember(16)] public List<LanguageProjectDto> Projects { get; set; } = [];
    [ProtoMember(1)] public string State { get; set; } = "";
    [ProtoMember(2)] public string Detail { get; set; } = "";
    [ProtoMember(3)] public string Server { get; set; } = "";
    [ProtoMember(4)] public long Version { get; set; }
    [ProtoMember(5)] public List<LanguageItemDto> Locations { get; set; } = [];
    [ProtoMember(6)] public List<LanguageItemDto> Diagnostics { get; set; } = [];
    [ProtoMember(7)] public List<LanguageItemDto> Tokens { get; set; } = [];
    [ProtoMember(8)] public string Freshness { get; set; } = "pending";
    [ProtoMember(11)] public List<LanguageFileDto> Files { get; set; } = [];
    [ProtoMember(10)] public List<LanguageServerDto> Servers { get; set; } = [];
    [ProtoMember(12)] public int Total { get; set; }
    [ProtoMember(14)] public string Configuration { get; set; } = "";
    [ProtoMember(15)] public string ActiveConfiguration { get; set; } = "";
    [ProtoMember(13)] public bool Truncated { get; set; }
    [ProtoMember(9)] public string Coverage { get; set; } = "open documents";
}

public partial interface IProjectRpc
{
    ValueTask<LanguageReply> LanguageAsync(LanguageRequestDto request, CallContext context = default);
}

[ProtoContract]
public sealed class LanguageTermsRequestDto
{
    [ProtoMember(1)] public string Workspace { get; set; } = "";
    [ProtoMember(2)] public bool Accept { get; set; }
    [ProtoMember(3)] public string ArtifactId { get; set; } = "";
    [ProtoMember(4)] public string EulaSha256 { get; set; } = "";
    [ProtoMember(5)] public string Server { get; set; } = "";
}

[ProtoContract]
public sealed class LanguageTermsReply
{
    [ProtoMember(1)] public string State { get; set; } = "";
    [ProtoMember(2)] public string Detail { get; set; } = "";
    [ProtoMember(3)] public string Version { get; set; } = "";
    [ProtoMember(4)] public string ArtifactId { get; set; } = "";
    [ProtoMember(5)] public string EulaSha256 { get; set; } = "";
    [ProtoMember(6)] public string Text { get; set; } = "";
    [ProtoMember(7)] public bool Accepted { get; set; }
    [ProtoMember(8)] public string AcceptedAtUtc { get; set; } = "";
}

public partial interface IProjectRpc
{
    ValueTask<LanguageTermsReply> LanguageTermsAsync(LanguageTermsRequestDto request, CallContext context = default);
}