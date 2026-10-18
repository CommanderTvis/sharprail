using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Context;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private static readonly HttpClient contextHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
    private ContextConsent? contextConsent;
    internal ContextConsent? ContextConsentOverride { get; set; }
    internal ContextConsent ContextConsent
    {
        get
        {
            lock (gate)
            {
                if (ContextConsentOverride is { } overridden) return overridden;
                if (contextConsent is not null) return contextConsent;
                if (DirectoryPath is null) throw new IOException("This host has no persistent Context storage.");
                var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unsupported";
                var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    _ => "unsupported"
                };
                return contextConsent = new(Path.Combine(DirectoryPath, "context"), contextHttp, ContextDistribution.Bundle(os, arch));
            }
        }
    }
}

public sealed partial class ProjectServices
{
    public async ValueTask<ContextConsentResult> ContextConsentAsync(ContextConsentRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return state is null ? new("unavailable", "This host has no persistent Context storage.") : await state.ContextConsent.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new("error", error.Message);
        }
    }
}

public sealed partial class HostStateStore
{
    private ContextService? contexts;
    internal Func<string, Action<string>, CancellationToken, Task<IContextRuntime>>? ContextRuntimeOverride { get; set; }
    internal ContextService Contexts
    {
        get
        {
            lock (gate)
            {
                if (contexts is not null) return contexts;
                contexts = new(workspace => Current.ContextWorkspaces.Contains(workspace), async (workspace, log, token) =>
                {
                    await ContextConsent.RequireAsync(token);
                    if (ContextRuntimeOverride is { } fixture) return await fixture(workspace, log, token);
                    var terms = await ContextConsent.ReviewAsync(token);
                    var installed = await ContextConsent.ExecuteAsync(new("download", terms.ArtifactId, terms.TermsSha256), token);
                    if (installed.State != "downloaded") throw new IOException(installed.Detail);
                    return new ContextRuntime(installed.Installation, workspace, log, ContextConsent.AuthorizeExecutionAsync);
                });
                WorkspaceRemoved += workspace => _ = contexts.ForgetWorkspaceAsync(workspace);
                return contexts;
            }
        }
    }

    internal async ValueTask SetContextEnabledAsync(string workspace, bool enabled, CancellationToken token)
    {
        if (enabled) await ContextConsent.RequireAsync(token);
        lock (gate)
        {
            var values = state.ContextWorkspaces.Where(path => path != workspace).ToList();
            if (enabled) values.Add(workspace);
            Publish(state with { ContextWorkspaces = values.Order(StringComparer.Ordinal).ToArray() }, persist: true);
        }
        if (!enabled && contexts is { } service) await service.CloseWorkspaceAsync(workspace);
    }
}

public sealed partial class ProjectServices
{
    public async ValueTask<ContextResult> ContextAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        if (state is null) return new("unavailable", "This host has no persistent Context storage.");
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace)) throw new ArgumentException("Context requires an existing absolute workspace.");
        request = request with { Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace)) };
        try
        {
            if (request.Operation is "enable" or "disable")
            {
                await state.SetContextEnabledAsync(request.Workspace, request.Operation == "enable", cancellationToken);
                return await state.Contexts.ExecuteAsync(request with { Operation = "status" }, cancellationToken);
            }
            return await state.Contexts.ExecuteAsync(request, cancellationToken);
        }
        catch (ContextConsentRequiredException) { return new("requiresAcceptance", "Review and accept the current Context agreements in Code Intelligence first."); }
        catch (Exception error) when (error is IOException or HttpRequestException or PlatformNotSupportedException or UnauthorizedAccessException)
        { return new("error", ContextOutput.Sanitize(error.Message), state.Current.ContextWorkspaces.Contains(request.Workspace)); }
    }
}