using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// Which language servers headless SharpRail serves. The manifests decide: a server declares <c>Headless = Yes</c> only when its installation and launch
/// need nothing the machine must already have (every runtime is a pinned managed download, or the server is a self-contained archive); one that needs
/// a toolchain without a pinned download (a JDK, Go, Rust, Ruby, R, PowerShell, Swift, Dart, Coursier) declares <c>No</c> until that runtime is pinned.
/// <see cref="Refusal"/> re-derives that from the pins, so a check can fail when a manifest and the pins disagree, and it is what leaves a declared server
/// out on one platform (clangd has no linux/arm64 archive).
/// </summary>
internal static class HeadlessLanguages
{
    /// <summary>Toolchains a server needs beyond the dependencies its spec declares.</summary>
    private static readonly Dictionary<string, string> Undeclared = new(StringComparer.Ordinal)
    {
        ["rust"] = "rust-analyzer needs cargo and rustc to load a project, and no Rust toolchain is pinned"
    };

    /// <summary>Why a server is not served headless on a platform, or null when it is.</summary>
    internal static string? Refusal(LanguageServerSpec spec, string platform)
    {
        // A server with terms is installed by its provider from archives the spec pins; the terms text is pinned too, so consent can come before the download.
        if (spec.TermsAsync is not null)
            return spec.TermsSha256.Length != 64 ? "its terms text has no pinned hash"
                : spec.Artifacts.Any(artifact => artifact.Platform == platform && artifact.Sha256.Length == 64 && artifact.Url.StartsWith("https://", StringComparison.Ordinal)) ? null : "it has no pinned archive for " + platform;
        if (spec.Pinning == LanguageServerPinning.None) return "it has no managed installation and runs the machine's own toolchain";
        if (Undeclared.TryGetValue(spec.Id, out var reason)) return reason;
        foreach (var dependency in spec.Dependencies)
            if (ManagedRuntimes.KindOf(dependency.Executable) is not { } kind || ManagedRuntimes.For(kind.Id, platform).Count == 0)
                return $"it needs {dependency.Name}, which has no pinned download" + (ManagedRuntimes.KindOf(dependency.Executable) is null ? "" : " for " + platform);
        var server = new LanguageServer(spec);
        if (spec.Pinning == LanguageServerPinning.Archive && ManagedArtifacts.Find(server, platform) is null) return "it has no pinned archive for " + platform;
        if (spec.Pinning == LanguageServerPinning.NuGet && ManagedArtifacts.NuGetFor(server, platform) is null) return "it has no pinned package for " + platform;
        return null;
    }

    /// <summary>The declared headless roster without the servers that have no pinned download for this platform.</summary>
    internal static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> Roster(IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> declared, string platform) =>
        [.. declared.Where(plugin => plugin.Host is not LanguageServerPlugin language || Refusal(language.Spec, platform) is null)];

    /// <summary>The servers of a roster, in roster order.</summary>
    internal static IReadOnlyList<LanguageServerSpec> Servers(IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> roster) =>
        [.. roster.Select(plugin => plugin.Host).OfType<LanguageServerPlugin>().Select(plugin => plugin.Spec)];

    /// <summary>The servers declared headless that this platform leaves out, each with the reason (also reports a manifest that claims more than the pins deliver).</summary>
    internal static IReadOnlyList<(LanguageServerSpec Spec, string Reason)> Left(IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> declared, string platform) =>
        [.. declared.Select(plugin => plugin.Host).OfType<LanguageServerPlugin>().Select(plugin => (plugin.Spec, Reason: Refusal(plugin.Spec, platform))).Where(item => item.Reason is not null).Select(item => (item.Spec, item.Reason!))];

    /// <summary>One line naming the served languages, for the initialize text and the setup script.</summary>
    internal static string Summary(IReadOnlyList<LanguageServerSpec> servers) =>
        string.Join(", ", servers.Select(spec => spec.Name.Length > 0 ? spec.Name : spec.Id));
}