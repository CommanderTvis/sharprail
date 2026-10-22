using System.Collections.Concurrent;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>Where one host tool was found: <c>found</c>, <c>missing</c> or <c>rejected</c>, from <c>override</c>, <c>home</c> or <c>path</c>.</summary>
internal sealed record ToolProbe(string State, string Path, string Source, string Problem);

internal sealed partial class LanguageServers
{
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<(string Workspace, string Executable, string Home, string Override), (DateTime Time, ToolProbe Probe)> probes = new();

    /// <summary>The user's host-wide tool paths from host state, as a JSON object of executable name to absolute path.</summary>
    internal Func<string> ToolPaths { get; set; } = () => "";

    internal static Dictionary<string, string> ParseTools(string json) => string.IsNullOrWhiteSpace(json) ? new(StringComparer.Ordinal)
        : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);

    private string ToolOverride(string executable) => ParseTools(ToolPaths()).GetValueOrDefault(executable) ?? "";

    /// <summary>
    /// Finds one tool without running it: the user's path for it first, then <paramref name="homeVariable"/>'s <c>bin</c> and root, then the
    /// hardened PATH. Never returns an executable inside the workspace. Recent answers are reused unless <paramref name="fresh"/>.
    /// </summary>
    internal ToolProbe ProbeTool(string executable, string workspace, string homeVariable = "", bool fresh = false)
    {
        var chosen = ToolOverride(executable);
        var key = (workspace, executable, homeVariable, chosen);
        if (!fresh && probes.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < ProbeLifetime) return cached.Probe;
        var probe = Probe(executable, workspace, homeVariable, chosen);
        probes[key] = (DateTime.UtcNow, probe);
        return probe;
    }

    private static ToolProbe Probe(string executable, string workspace, string homeVariable, string chosen)
    {
        if (chosen.Length > 0)
            return LanguageExecutables.Reject(chosen, workspace) is { } problem ? new("rejected", chosen, "override", problem) : new("found", chosen, "override", "");
        var file = executable + (OperatingSystem.IsWindows() ? ".exe" : "");
        if (homeVariable.Length > 0 && Environment.GetEnvironmentVariable(homeVariable) is { Length: > 0 } home && Path.IsPathFullyQualified(home))
            foreach (var candidate in new[] { Path.Combine(home, "bin", file), Path.Combine(home, file) })
                if (File.Exists(candidate) && LanguageExecutables.Reject(candidate, workspace) is null) return new("found", candidate, "home", "");
        var rejected = new List<string>();
        return LanguageExecutables.FindOnPath(executable, workspace, rejected) is { } found ? new("found", found, "path", "") : new("missing", "", "", rejected.FirstOrDefault() ?? "");
    }

    /// <summary>The dependency a server declares for a tool, or a generic one for a tool it does not declare.</summary>
    private static LanguageServerDependency Dependency(LanguageServer server, string executable) =>
        server.Spec.Dependencies.FirstOrDefault(item => item.Executable == executable) ?? new(executable, executable, $"Install {executable} on the host (outside the workspace).");

    /// <summary>A tool the server's installation or launch needs, or an <see cref="IOException"/> naming it and how to provide it.</summary>
    internal string RequireTool(LanguageServer server, string executable, string workspace)
    {
        var dependency = Dependency(server, executable);
        var probe = ProbeTool(executable, workspace, dependency.HomeVariable, fresh: true);
        return probe.State == "found" ? probe.Path : throw new IOException(Needs(dependency, probe));
    }

    internal string? FindTool(LanguageServer server, string executable, string workspace) =>
        ProbeTool(executable, workspace, Dependency(server, executable).HomeVariable, fresh: true) is { State: "found" } probe ? probe.Path : null;

    private static string Needs(LanguageServerDependency dependency, ToolProbe probe) => probe.State == "rejected"
        ? $"Needs {dependency.Executable}: your path for it cannot run. {probe.Problem} Choose another with Use path… in the language server's row, or clear it, then Retry."
        : $"Needs {dependency.Executable} ({dependency.Name}). {dependency.InstallHint} Or choose an existing {dependency.Executable} with Use path… in the language server's row, then Retry.";

    /// <summary>
    /// Probes every tool the server declares, without starting or installing anything. A tool is required only for the managed launch:
    /// an executable override or PATH launch needs none, and an install-only tool is needed only until the managed installation exists.
    /// </summary>
    internal IReadOnlyList<LanguageDependencyState> ProbeDependencies(LanguageServer server, string workspace, bool fresh = false)
    {
        var spec = server.Spec;
        if (spec.Dependencies.Count == 0) return [];
        var custom = CustomOptions(server.Id);
        var managed = !(spec.AllowsOverride && !string.IsNullOrWhiteSpace(custom?.Command)) && !(custom?.AllowPath == true && spec.PathCapable) && spec.Pinning != LanguageServerPinning.None;
        var root = Path.Combine(directory, "servers", server.Id, ManagedArtifacts.Directory(server, ManagedArtifacts.Platform()));
        var installed = File.Exists(Path.Combine(root, ".ready")) && spec.Installed(root) || File.Exists(Path.Combine(root, ".sharprail-artifact.json"));
        var tools = ParseTools(ToolPaths());
        return [.. spec.Dependencies.Select(dependency =>
        {
            var probe = ProbeTool(dependency.Executable, workspace, dependency.HomeVariable, fresh);
            return new LanguageDependencyState(dependency.Executable, dependency.Name, probe.State, probe.Path, probe.Source, dependency.InstallHint, probe.Problem,
                tools.ContainsKey(dependency.Executable), managed && (!dependency.InstallOnly || !installed));
        })];
    }

    /// <summary>Refuses a managed start while a tool it needs is missing, naming the tool instead of failing inside the launch.</summary>
    private void RequireDependencies(LanguageServer server, string workspace)
    {
        if (ProbeDependencies(server, workspace, fresh: true).FirstOrDefault(item => item.Required && item.State != "found") is { } missing)
            throw new IOException(Needs(Dependency(server, missing.Executable), new(missing.State, missing.Path, missing.Source, missing.Problem)));
    }
}