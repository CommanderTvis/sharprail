using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>
/// What an icon from the registry must be before it is shown: a small, plain SVG. The registry is remote and its files are untrusted, so anything
/// that could run, load or draw something from elsewhere is refused outright instead of being filtered.
/// </summary>
internal static partial class AcpIconRules
{
    /// <summary>The most an icon may be, in bytes (the registry's own are well under 5 KB).</summary>
    public const int MaxBytes = 64 * 1024;

    private static readonly HashSet<string> ForbiddenElements = new(StringComparer.OrdinalIgnoreCase) { "script", "foreignObject", "image", "iframe", "object", "embed", "audio", "video", "animate", "set", "animateTransform", "animateMotion" };

    [GeneratedRegex(@"url\((?>\s*)(?>['""]?)(?>\s*)(?!#)", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalUrl();

    [GeneratedRegex(@"@import|expression\(|javascript:", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveStyle();

    /// <summary>The ids that become file names: letters, digits, dot, dash and underscore only.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafeId();

    public static bool IsSafeId(string id) => SafeId().IsMatch(id) && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>An icon url is only fetched when it is https, on the registry's own host and port, and carries no credentials.</summary>
    public static bool IsAllowedUrl(string url, string registryUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out var icon) && Uri.TryCreate(registryUrl, UriKind.Absolute, out var registry)
        && icon.Scheme == Uri.UriSchemeHttps && icon.IsDefaultPort && icon.UserInfo.Length == 0 && icon.Fragment.Length == 0
        && string.Equals(icon.Host, registry.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the document is acceptable, otherwise why it is not.</summary>
    public static string? Reject(string svg)
    {
        if (Encoding.UTF8.GetByteCount(svg) > MaxBytes) return "the icon is larger than " + MaxBytes / 1024 + " KB";
        if (svg.Contains("<script", StringComparison.OrdinalIgnoreCase) || svg.Contains("<foreignObject", StringComparison.OrdinalIgnoreCase) || svg.Contains("<image", StringComparison.OrdinalIgnoreCase)) return "the icon contains active or external content";
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 });
            var first = true;
            var inStyle = false;
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (first && !string.Equals(reader.LocalName, "svg", StringComparison.OrdinalIgnoreCase)) return "the document is not an SVG";
                        first = false;
                        if (ForbiddenElements.Contains(reader.LocalName)) return "the icon uses <" + reader.LocalName + ">";
                        inStyle = string.Equals(reader.LocalName, "style", StringComparison.OrdinalIgnoreCase);
                        var empty = reader.IsEmptyElement;
                        if (reader.HasAttributes)
                            while (reader.MoveToNextAttribute())
                            {
                                var name = reader.LocalName;
                                var value = reader.Value;
                                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return "the icon has an event handler";
                                if (string.Equals(name, "href", StringComparison.OrdinalIgnoreCase) && !value.StartsWith('#')) return "the icon links outside itself";
                                if (ExternalUrl().IsMatch(value) || ActiveStyle().IsMatch(value)) return "the icon refers outside itself";
                            }
                        reader.MoveToElement();
                        if (empty) inStyle = false;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA when inStyle:
                        if (ExternalUrl().IsMatch(reader.Value) || ActiveStyle().IsMatch(reader.Value)) return "the icon's style refers outside itself";
                        break;
                    case XmlNodeType.EndElement:
                        inStyle = false;
                        break;
                    case XmlNodeType.DocumentType:
                        return "the icon declares a document type";
                }
            }
            return first ? "the document is empty" : null;
        }
        catch (XmlException) { return "the icon is not well-formed XML"; }
    }
}

/// <summary>The cache beside a copy of one icon: the validators of its last fetch.</summary>
internal sealed record AcpIconMeta(string Url, string? ETag, string? LastModified, string FetchedAt);

/// <summary>
/// The registry's icons on disk, one SVG per agent id next to the registry copy, revalidated with the ETag the way the registry is. Each is fetched
/// only from the registry's own https origin, capped in size, never followed across a redirect and checked by <see cref="AcpIconRules"/> before it is
/// kept or returned. An icon that cannot be fetched keeps the copy from before, so a browser that has seen the registry once shows icons offline.
/// </summary>
internal sealed class AcpIconCache(string directory, AcpFetch fetch, Func<DateTimeOffset>? clock = null, string registryUrl = AcpRegistry.Url)
{
    /// <summary>An icon younger than this is not revalidated unless asked; icons change rarely.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(12);

    private readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The real fetch: no redirects, a short timeout, and the body read only up to the size cap (a larger answer is a 413, nothing kept).</summary>
    public static async Task<AcpFetchResult> HttpFetch(string url, string? etag, string? lastModified, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SharpRail-ACP/0.1");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (lastModified is not null) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return new((int)response.StatusCode, null, etag, lastModified);
        if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, null, null, null);
        if (response.Content.Headers.ContentLength > AcpIconRules.MaxBytes) return new(413, null, null, null);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > AcpIconRules.MaxBytes) return new(413, null, null, null);
        }
        return new((int)response.StatusCode, Encoding.UTF8.GetString(buffer.ToArray()), response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"));
    }

    private string SvgPath(string id) => Path.Combine(directory, id + ".svg");
    private string MetaPath(string id) => Path.Combine(directory, id + ".json");

    private (string Svg, AcpIconMeta Meta)? Read(string id)
    {
        try
        {
            if (!File.Exists(SvgPath(id)) || !File.Exists(MetaPath(id))) return null;
            var svg = File.ReadAllText(SvgPath(id));
            var meta = JsonSerializer.Deserialize<AcpIconMeta>(File.ReadAllText(MetaPath(id)));
            // A copy that no longer passes the rules (they may have grown) is not shown.
            return meta is null || AcpIconRules.Reject(svg) is not null ? null : (svg, meta);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void Write(string id, string svg, AcpIconMeta meta)
    {
        Directory.CreateDirectory(directory);
        var svgTemp = SvgPath(id) + ".tmp";
        var metaTemp = MetaPath(id) + ".tmp";
        File.WriteAllText(svgTemp, svg);
        File.WriteAllText(metaTemp, JsonSerializer.Serialize(meta));
        File.Move(svgTemp, SvgPath(id), overwrite: true);
        File.Move(metaTemp, MetaPath(id), overwrite: true);
    }

    private void Forget(string id)
    {
        try { File.Delete(SvgPath(id)); File.Delete(MetaPath(id)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One icon: the cached copy, revalidated or fetched when it is stale and the network is wanted. Null when there is none to show.</summary>
    private async Task<string?> OneAsync(string id, string url, bool refresh, bool cacheOnly, CancellationToken cancellationToken)
    {
        if (!AcpIconRules.IsSafeId(id) || !AcpIconRules.IsAllowedUrl(url, registryUrl)) return null;
        var cached = await Task.Run(() => Read(id), cancellationToken);
        if (cached is { } copy && copy.Meta.Url != url) cached = null;
        var now = clock();
        if (cached is { } have && (cacheOnly || !refresh && DateTimeOffset.TryParse(have.Meta.FetchedAt, out var at) && now - at < Fresh)) return have.Svg;
        if (cacheOnly) return null;
        try
        {
            var result = await fetch(url, cached?.Meta.ETag, cached?.Meta.LastModified, cancellationToken);
            if (result.Status == 304 && cached is { } same)
            {
                await Task.Run(() => Write(id, same.Svg, same.Meta with { FetchedAt = now.ToString("o") }), cancellationToken);
                return same.Svg;
            }
            if (result.Status == 200 && result.Body is { } body)
            {
                if (AcpIconRules.Reject(body) is not null)
                {
                    // The registry now serves something unsafe for this agent: the old copy goes too, and the generic glyph stands in.
                    await Task.Run(() => Forget(id), cancellationToken);
                    return null;
                }
                await Task.Run(() => Write(id, body, new AcpIconMeta(url, result.ETag, result.LastModified, now.ToString("o"))), cancellationToken);
                return body;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
        }
        return cached?.Svg;
    }

    /// <summary>The icons of the given agents (id and icon url) that can be shown; the others are simply absent.</summary>
    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(IEnumerable<(string Id, string Url)> wanted, bool refresh, bool cacheOnly, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = wanted.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return (item.Id, Svg: await OneAsync(item.Id, item.Url, refresh, cacheOnly, cancellationToken)); }
            finally { gate.Release(); }
        }).ToList();
        var all = await Task.WhenAll(tasks);
        return all.Where(item => item.Svg is not null).ToDictionary(item => item.Id, item => item.Svg!);
    }
}
