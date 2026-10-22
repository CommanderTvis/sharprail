using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Protocol;
using SharpRail.Plugins.Lsp.IntelliJ;

namespace SharpRail.Checks;

internal static class IntelliJTermsChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static string Installation(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "bin", "intellij-server"), "Synthetic fixture binary, never executed.");
        File.WriteAllText(Path.Combine(root, "product-info.json"), """{"productCode":"ILS","buildNumber":"fixture-1"}""");
        File.WriteAllBytes(Path.Combine(root, "EULA.txt"), Encoding.UTF8.GetBytes("Synthetic fixture agreement – not JetBrains terms.\r\nExplicit fixture acknowledgement only."));
        return root;
    }

    internal static async Task Run(IProjectServices host, string workspace, bool remote)
    {
        var install = Installation(Path.Combine(workspace, ".tools", "terms-installation"));
        await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: JsonSerializer.Serialize(new { installationPath = install })));
        var request = new LanguageTermsRequest(workspace, "intellij");
        var review = await host.LanguageTermsAsync(request);
        Require(review.State == "requiresAcceptance" && !review.Accepted && review.Text.Contains("\r\n", StringComparison.Ordinal) &&
            review.EulaSha256 == IntelliJDistribution.EulaHash(await File.ReadAllBytesAsync(Path.Combine(install, "EULA.txt"))), "Review changed exact terms or preaccepted them.");
        try { await host.LanguageTermsAsync(request with { Accept = true }); throw new InvalidOperationException("Acceptance did not require reviewed identities."); }
        catch (ArgumentException) { }
        catch (RpcException) when (remote) { }
        var accepted = await host.LanguageTermsAsync(request with { Accept = true, ArtifactId = review.ArtifactId, EulaSha256 = review.EulaSha256 });
        Require(accepted.Accepted && accepted.State == "accepted" && accepted.AcceptedAtUtc.Length > 0, "Explicit synthetic agreement acceptance was not recorded.");
        var reopened = new HostStateStore(Path.Combine(workspace, ".tools", "state"));
        try
        {
            var other = new LocalProjectAdapter(new ProjectServices(workspace, reopened));
            var persisted = await other.LanguageTermsAsync(request);
            Require(persisted.Accepted && persisted.AcceptedAtUtc == accepted.AcceptedAtUtc, "Acceptance did not survive reopening the host.");
        }
        finally { await reopened.StopLanguagesAsync(); }
        var recordFile = Path.Combine(workspace, ".tools", "state", "language", "agreements", "intellij", accepted.ArtifactId + ".json");
        await File.WriteAllTextAsync(recordFile, "invalid record");
        Require(!(await host.LanguageTermsAsync(request)).Accepted, "A corrupted record counted as acceptance.");
        accepted = await host.LanguageTermsAsync(request with { Accept = true, ArtifactId = review.ArtifactId, EulaSha256 = review.EulaSha256 });
        Require(accepted.Accepted, "Explicit acceptance did not recover a corrupted record.");
        Require((await host.LanguageTermsAsync(request)).Accepted, "The accepted installation was not ready for an explicit user start.");
        await File.AppendAllTextAsync(Path.Combine(install, "EULA.txt"), "\nChanged fixture terms.");
        var changed = await host.LanguageTermsAsync(request with { Accept = true, ArtifactId = review.ArtifactId, EulaSha256 = review.EulaSha256 });
        Require(changed.State == "changed" && !changed.Accepted && changed.ArtifactId != review.ArtifactId && changed.EulaSha256 != review.EulaSha256,
            "Changed terms reused an earlier acknowledgement.");
        accepted = await host.LanguageTermsAsync(request with { Accept = true, ArtifactId = changed.ArtifactId, EulaSha256 = changed.EulaSha256 });
        Require(accepted.Accepted, "Changed synthetic terms could not be explicitly acknowledged.");
        await File.AppendAllTextAsync(Path.Combine(install, "bin", "intellij-server"), "changed binary");
        var artifact = await host.LanguageTermsAsync(request);
        Require(!artifact.Accepted && artifact.ArtifactId != accepted.ArtifactId && artifact.EulaSha256 == accepted.EulaSha256, "A changed artifact inherited EULA acceptance.");
        await File.WriteAllTextAsync(Path.Combine(install, "product-info.json"), """{"productCode":"ILS","buildNumber":"fixture-2"}""");
        var version = await host.LanguageTermsAsync(request);
        Require(version.Version == "fixture-2" && !version.Accepted && version.ArtifactId != artifact.ArtifactId, "A new build inherited acceptance.");
        var records = Path.Combine(workspace, ".tools", "state", "language", "agreements", "intellij");
        var blockedRecord = Path.Combine(records, version.ArtifactId + ".json");
        Directory.CreateDirectory(blockedRecord);
        var failed = await host.LanguageTermsAsync(request with { Accept = true, ArtifactId = version.ArtifactId, EulaSha256 = version.EulaSha256 });
        Require(failed.State == "unavailable" && !failed.Accepted && !Directory.EnumerateFiles(records, "*.partial-*").Any(), "Failed atomic acceptance left a usable record or temporary file.");
        Directory.Delete(blockedRecord);
        try { await host.LanguageAsync(new(workspace, Operation: "acceptEula", Server: "intellij")); throw new InvalidOperationException("Agent language operation accepted terms."); }
        catch (ArgumentException) { }
        catch (RpcException) when (remote) { }
        await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: "{}"));
        Directory.Delete(install, true);
        Console.WriteLine("PASS IntelliJ terms direct/WebSocket exact bytes, explicit acknowledgement, persistent artifact/version/hash identity, changed terms, atomic failure and MCP exclusion");
    }
}