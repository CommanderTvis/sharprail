using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using SharpRail.Plugins.AirContext;

namespace SharpRail.Plugins.AirContext.Host;

internal sealed class ContextConsent(string storage, HttpClient http, ContextBundle bundle)
{
    private readonly SemaphoreSlim verificationGate = new(1);
    private (long Length, DateTime Modified, bool Valid)? verified;
    private sealed record Receipt(string ArtifactId, string TermsSha256, string AcceptedAtUtc);
    internal static readonly string[] LegalUrls = [ContextDistribution.TermsUrl,
        "https://www.jetbrains.com/legal/docs/terms/jetbrains-ai-service/",
        "https://www.jetbrains.com/legal/docs/terms/jetbrains-ai-platform/"];
    internal static string Identity(ContextBundle artifact) => Hash(JsonSerializer.Serialize(artifact));
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string Record => Path.Combine(storage, "agreements", "context", Identity(bundle) + ".json");

    internal async Task<ContextConsentResult> ExecuteAsync(ContextConsentRequest request, CancellationToken token)
    {
        if (request.Operation == "status") return await StatusAsync(token);
        if (request.Operation is not ("review" or "acceptAndDownload" or "download")) throw new ArgumentException("Unknown Context consent operation.");
        var review = await ReviewAsync(token);
        if (request.Operation == "review") return review;
        if (request.ArtifactId != review.ArtifactId || request.TermsSha256 != review.TermsSha256)
            return review with { State = "changed", Detail = "The artifact or terms changed. Review them again before downloading.", AcceptedAtUtc = "" };
        if (request.Operation == "acceptAndDownload" && review.AcceptedAtUtc.Length == 0)
        {
            var receipt = new Receipt(review.ArtifactId, review.TermsSha256, DateTimeOffset.UtcNow.ToString("O"));
            Directory.CreateDirectory(Path.GetDirectoryName(Record)!);
            var temporary = Record + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, Record, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        try
        {
            var installation = await ContextDistribution.InstallAsync(storage, bundle, http, token);
            var current = await ReviewAsync(token);
            return current.AcceptedAtUtc.Length == 0
                ? current with { State = "changed", Detail = "Terms changed after download. Review them before any further use.", Installation = installation }
                : current with { State = "downloaded", Detail = "Downloaded and verified. Downloading does not start the CLI or enable workspace indexing.", Installation = installation };
        }
        catch (ContextConsentRequiredException)
        {
            return await ReviewAsync(token);
        }
    }

    internal async Task<ContextConsentResult> ReviewAsync(CancellationToken token)
    {
        var documents = new List<string>();
        foreach (var url in LegalUrls)
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https", Host: "www.jetbrains.com" })
                throw new IOException("Context agreements must be read from JetBrains over HTTPS.");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (bytes.Length + read > 2 * 1024 * 1024) throw new IOException("Context terms exceed the review size limit.");
                await bytes.WriteAsync(buffer.AsMemory(0, read), token);
            }
            var html = Encoding.UTF8.GetString(bytes.ToArray());
            var content = Regex.Match(html, "<div[^>]*class=[\"']licenseContent[\"'][^>]*>(.*?)</div>", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            if (!content.Success) throw new IOException("Unable to read the current JetBrains agreement. Open the original terms and retry later.");
            var linked = Regex.Replace(content.Groups[1].Value, "<a\\b[^>]*href=[\"']([^\"']*)[\"'][^>]*>(.*?)</a>",
                match => match.Groups[2].Value + " (" + WebUtility.HtmlDecode(match.Groups[1].Value) + ")",
                RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var text = Regex.Replace(linked, "</(?:p|h[1-6]|li)>|<br\\s*/?>", "\n\n", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
            text = string.Join('\n', text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
            if (text.Length < 100) throw new IOException("JetBrains returned an incomplete agreement.");
            documents.Add(url + "\n\n" + text);
        }
        var combined = string.Join("\n\n────────\n\n", documents);
        var result = new ContextConsentResult("requiresAcceptance", bundle.Platform + " · " + bundle.Url + "\nSHA-256: " + bundle.Sha256,
            bundle.Version, Identity(bundle), Hash(combined), combined);
        if (File.Exists(Record))
        {
            Receipt? receipt;
            try { receipt = JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(Record, token)); }
            catch (JsonException) { receipt = null; }
            if (receipt is not null && receipt.ArtifactId == result.ArtifactId && receipt.TermsSha256 == result.TermsSha256 && DateTimeOffset.TryParse(receipt.AcceptedAtUtc, out _))
                result = result with { State = "accepted", AcceptedAtUtc = receipt.AcceptedAtUtc };
        }
        if (result.AcceptedAtUtc.Length > 0 && await VerifiedInstallationAsync(token) is { } installation)
            result = result with { State = "downloaded", Installation = installation };
        return result;
    }

    private async Task<string?> VerifiedInstallationAsync(CancellationToken token)
    {
        var path = ContextDistribution.Installation(storage, bundle);
        await verificationGate.WaitAsync(token);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null) { verified = null; return null; }
            var identity = (file.Length, file.LastWriteTimeUtc);
            if (verified is not { } previous || previous.Length != identity.Length || previous.Modified != identity.LastWriteTimeUtc)
            {
                var valid = await ContextDistribution.MatchesAsync(path, bundle.Sha256, token);
                verified = (identity.Length, identity.LastWriteTimeUtc, valid);
            }
            return verified is { Valid: true } ? path : null;
        }
        finally { verificationGate.Release(); }
    }

    private async Task<ContextConsentResult> StatusAsync(CancellationToken token)
    {
        var installation = await VerifiedInstallationAsync(token);
        var exists = File.Exists(ContextDistribution.Installation(storage, bundle));
        return new(installation is not null ? "downloaded" : exists ? "repair" : "notInstalled",
            installation is not null ? "CLI downloaded and verified. Workspace indexing is controlled separately in the Air Context pane." : exists ? "The CLI failed integrity verification. Download again to repair it." : "CLI not downloaded.",
            bundle.Version, Identity(bundle), Installation: installation ?? "");
    }

    internal async Task AuthorizeExecutionAsync(CancellationToken token)
    {
        await RequireAsync(token);
        var executable = ContextDistribution.Installation(storage, bundle);
        if (!await ContextDistribution.MatchesAsync(executable, bundle.Sha256, token))
            throw new IOException("Context CLI integrity verification failed. Repair the installation in the Air Context pane before running it.");
    }

    internal async Task RequireAsync(CancellationToken token)
    {
        if (!File.Exists(Record)) throw new ContextConsentRequiredException();
        if ((await ReviewAsync(token)).AcceptedAtUtc.Length == 0) throw new ContextConsentRequiredException();
    }
}

internal sealed class ContextConsentRequiredException() : InvalidOperationException("Review and accept JetBrains Context terms in the user interface before provisioning.");