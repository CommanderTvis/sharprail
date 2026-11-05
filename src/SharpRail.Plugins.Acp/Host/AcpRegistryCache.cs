using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Acp.Host;

internal sealed record AcpFetchResult(int Status, string? Body, string? ETag, string? LastModified);

/// <summary>Fetches a URL with conditional headers. A real one is <see cref="HttpFetch"/>; the checks pass a fixture.</summary>
internal delegate Task<AcpFetchResult> AcpFetch(string url, string? etag, string? lastModified, CancellationToken cancellationToken);

internal sealed record AcpRegistryMeta(string? ETag, string? LastModified, string FetchedAt);

/// <summary>
/// The registry on disk: a cached copy with its ETag and Last-Modified, revalidated with If-None-Match / If-Modified-Since. When the registry
/// cannot be reached the cached copy is used (and the view says so); only with no copy at all does the built-in seed stand in.
/// </summary>
internal sealed class AcpRegistryCache(string directory, AcpFetch fetch, Func<DateTimeOffset>? clock = null, string url = AcpRegistry.Url)
{
    /// <summary>The registry's own cache lifetime (<c>max-age=300</c>): a copy younger than this is not revalidated unless asked.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);
    private string BodyPath => Path.Combine(directory, "registry.json");
    private string MetaPath => Path.Combine(directory, "registry-meta.json");

    public static async Task<AcpFetchResult> HttpFetch(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SharpRail-ACP/0.1");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (lastModified is not null) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return new((int)response.StatusCode, null, etag, lastModified);
        var body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
        return new((int)response.StatusCode, body, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"));
    }

    private (string Body, AcpRegistryMeta Meta)? ReadCache()
    {
        try
        {
            if (!File.Exists(BodyPath) || !File.Exists(MetaPath)) return null;
            var meta = JsonSerializer.Deserialize<AcpRegistryMeta>(File.ReadAllText(MetaPath));
            return meta is null ? null : (File.ReadAllText(BodyPath), meta);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void WriteCache(string body, AcpRegistryMeta meta)
    {
        Directory.CreateDirectory(directory);
        // Written beside and moved into place, so a crash never leaves half a registry.
        var bodyTemp = BodyPath + ".tmp";
        var metaTemp = MetaPath + ".tmp";
        File.WriteAllText(bodyTemp, body);
        File.WriteAllText(metaTemp, JsonSerializer.Serialize(meta));
        File.Move(bodyTemp, BodyPath, overwrite: true);
        File.Move(metaTemp, MetaPath, overwrite: true);
    }

    /// <summary>The copy on disk without touching the network; null when there is none.</summary>
    public async Task<JsonObject?> CachedAsync(CancellationToken cancellationToken) =>
        await Task.Run(ReadCache, cancellationToken) is { } cached ? AcpRegistry.Parse(cached.Body) : null;

    public async Task<(JsonObject Root, AcpRegistryView View)> LoadAsync(bool refresh, CancellationToken cancellationToken)
    {
        var platform = AcpRegistry.Platform();
        var cached = await Task.Run(ReadCache, cancellationToken);
        var cachedRoot = cached is { } c ? AcpRegistry.Parse(c.Body) : null;
        var now = clock();
        if (cachedRoot is not null && !refresh && DateTimeOffset.TryParse(cached!.Value.Meta.FetchedAt, out var at) && now - at < Fresh)
            return (cachedRoot, AcpRegistry.View(cachedRoot, "cache", cached.Value.Meta.FetchedAt, null, platform));
        string reason;
        try
        {
            var result = await fetch(url, cachedRoot is null ? null : cached!.Value.Meta.ETag, cachedRoot is null ? null : cached!.Value.Meta.LastModified, cancellationToken);
            if (result.Status == 304 && cachedRoot is not null)
            {
                var meta = cached!.Value.Meta with { FetchedAt = now.ToString("o") };
                await Task.Run(() => WriteCache(cached.Value.Body, meta), cancellationToken);
                return (cachedRoot, AcpRegistry.View(cachedRoot, "network", meta.FetchedAt, null, platform));
            }
            if (result.Status == 200 && result.Body is not null && AcpRegistry.Parse(result.Body) is { } fresh)
            {
                var meta = new AcpRegistryMeta(result.ETag, result.LastModified, now.ToString("o"));
                await Task.Run(() => WriteCache(result.Body, meta), cancellationToken);
                return (fresh, AcpRegistry.View(fresh, "network", meta.FetchedAt, null, platform));
            }
            reason = result.Status is 200 ? "The registry answered with something that is not a registry." : $"The registry answered {result.Status}.";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            reason = "The registry could not be reached (" + error.Message + ").";
        }
        if (cachedRoot is not null)
            return (cachedRoot, AcpRegistry.View(cachedRoot, "cache", cached!.Value.Meta.FetchedAt, reason + " Showing the copy fetched earlier.", platform));
        var seed = AcpRegistry.Parse(AcpRegistry.Seed)!;
        return (seed, AcpRegistry.View(seed, "seed", null, reason + " Showing a built-in minimal list until the registry can be reached.", platform));
    }
}