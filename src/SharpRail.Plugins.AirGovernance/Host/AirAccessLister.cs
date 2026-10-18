using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.AirGovernance.Host;

/// <summary>The two endpoints of one Central environment. Production values come only from the fixed table below.</summary>
internal sealed record AirAccessEndpoints(Uri Token, Uri Options)
{
    internal static AirAccessEndpoints? For(string environment) => environment switch
    {
        "prod" => new(new("https://oauth.account.jetbrains.com/oauth2/token"), new("https://api.jetbrains.cloud/user-management/api/ai-access-options")),
        "staging" => new(new("https://public.aip.oauth.intservices.aws.intellij.net/oauth2/token"), new("https://api.stgn.jetbrains.cloud/user-management/api/ai-access-options")),
        _ => null
    };
}

/// <summary>A listing: the sources, or the one specific reason there are none to show. Never carries a credential.</summary>
internal sealed record AirAccessListing(IReadOnlyList<AirAccessSource>? Sources, string? Failure);

/// <summary>
/// Lists the AI access sources the signed-in Central session may select, after JetBrains/thinkrail#708: the
/// session comes from <c>central login export</c>, its refresh token is exchanged in memory at the environment's
/// fixed OAuth endpoint and the enabled workspaces and licences are read from JetBrains Cloud. The envelope and
/// the endpoint are internal interfaces of Central and JetBrains Cloud, so every deviation fails closed: a malformed
/// envelope or response, an environment outside the table, a redirect, an oversized body, any error status and a
/// rotated refresh token. Credentials live only in locals and buffers cleared after the request; nothing is logged,
/// stored or put in a message.
/// </summary>
internal static partial class AirAccessLister
{
    internal const int MaxResponse = 64 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    [GeneratedRegex("^central-[A-Za-z0-9_-]+$")]
    private static partial Regex Envelope();

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex Segment();

    public static async Task<AirAccessListing> ListAsync(AirCentralCli cli, string central, CancellationToken cancellationToken)
    {
        var export = await cli.RunAsync(central, ["login", "export"], AirCentralCli.StatusTimeout, true, cancellationToken);
        if (export.Outcome != "exited" || export.ExitCode != 0)
            return Fail("Central has no signed-in session to read. Sign in to Central, then list the sources again.");
        byte[]? decoded = null, form = null;
        try
        {
            var text = export.Stdout.Trim();
            if (!Envelope().IsMatch(text)) return Fail("Central's session export is not in the expected format.");
            try { decoded = Base64Url.DecodeFromChars(text.AsSpan(8)); }
            catch (FormatException) { return Fail("Central's session export is not in the expected format."); }
            using var session = Parse(decoded);
            if (session is null || session.RootElement.ValueKind != JsonValueKind.Object ||
                !session.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                Text(session.RootElement, "refresh_token") is not { Length: > 0 } refresh ||
                Text(session.RootElement, "environment") is not { } environment)
                return Fail("Central's session export is not in the expected format.");
            var known = AirAccessEndpoints.For(environment);
            if (known is null) return Fail("Central's session belongs to an unknown environment, so it was not sent anywhere.");
            var endpoints = cli.Environment.TestEndpoints ?? known;

            form = Encoding.UTF8.GetBytes($"grant_type=refresh_token&client_id=central-cli&refresh_token={Uri.EscapeDataString(refresh)}");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(RequestTimeout);
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, ConnectTimeout = RequestTimeout }, true) { Timeout = RequestTimeout };
            string accessToken;
            try
            {
                using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, endpoints.Token) { Content = new ByteArrayContent(form) };
                tokenRequest.Content.Headers.ContentType = new("application/x-www-form-urlencoded");
                var (tokenStatus, tokenBody) = await SendAsync(client, tokenRequest, limit.Token);
                if (tokenStatus is not null) return Fail(tokenStatus);
                using var token = Parse(tokenBody!);
                if (token is null || token.RootElement.ValueKind != JsonValueKind.Object || Text(token.RootElement, "access_token") is not { Length: > 0 } access)
                    return Fail("JetBrains returned an unexpected token response.");
                if (token.RootElement.TryGetProperty("refresh_token", out var rotated) && !(rotated.ValueKind == JsonValueKind.String && rotated.GetString() == refresh))
                    return Fail("The sign-in server rotated Central's refresh token. Listing refuses to continue; if Central reports being signed out, sign in again.");
                accessToken = access;
                CryptographicOperations.ZeroMemory(tokenBody!);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested) { return Fail("The JetBrains sign-in server could not be reached."); }

            try
            {
                using var optionsRequest = new HttpRequestMessage(HttpMethod.Get, endpoints.Options);
                optionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                optionsRequest.Headers.Accept.Add(new("application/json"));
                var (status, body) = await SendAsync(client, optionsRequest, limit.Token);
                accessToken = "";
                if (status is not null) return Fail(status);
                using var options = Parse(body!);
                if (options is null || options.RootElement.ValueKind != JsonValueKind.Object ||
                    !options.RootElement.TryGetProperty("aiAccessOptions", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    return Fail("JetBrains returned an unexpected list of AI access sources.");
                return Collect(entries, CurrentId(session.RootElement));
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested) { return Fail("The AI access endpoint could not be reached."); }
        }
        finally
        {
            if (decoded is not null) CryptographicOperations.ZeroMemory(decoded);
            if (form is not null) CryptographicOperations.ZeroMemory(form);
        }
    }

    private static AirAccessListing Fail(string message) => new(null, message);

    private static JsonDocument? Parse(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>One bounded request: the body on a 2xx, else a fixed failure message. Redirects are refused, never followed.</summary>
    private static async Task<(string? Failure, byte[]? Body)> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } uri || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            return ("The AI access endpoint is not secure, so it was not contacted.", null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400) return ("The AI access endpoint redirected, which is refused.", null);
        if (code is 400 or 401 or 403) return ("JetBrains rejected Central's stored session. Sign in to Central again.", null);
        if (!response.IsSuccessStatusCode) return ($"The AI access endpoint answered with an error (HTTP {code}).", null);
        if (response.Content.Headers.ContentLength > MaxResponse) return ("The AI access endpoint's response was too large and was ignored.", null);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxResponse + 1];
        var length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken)) > 0)
        {
            length += read;
            if (length > MaxResponse) { CryptographicOperations.ZeroMemory(buffer); return ("The AI access endpoint's response was too large and was ignored.", null); }
        }
        var body = buffer.AsSpan(0, length).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return (null, body);
    }

    private static string? Id(JsonElement element, string name) => Text(element, name) is { } value && Segment().IsMatch(value) ? value : null;

    private static string? SelectionId(JsonElement element, string type, string org, string workspace, string license)
    {
        if (Text(element, "type") != type) return null;
        if (type == "workspace") return Id(element, org) is { } o && Id(element, workspace) is { } w ? $"workspace:{o}:{w}" : null;
        return Id(element, license) is { } l ? $"license:{l}" : null;
    }

    private static string? CurrentId(JsonElement session)
    {
        if (!session.TryGetProperty("ai_access", out var current) || current.ValueKind != JsonValueKind.Object) return null;
        return SelectionId(current, "workspace", "org_id", "workspace_id", "license_id") ?? SelectionId(current, "license", "org_id", "workspace_id", "license_id");
    }

    private static AirAccessListing Collect(JsonElement entries, string? currentId)
    {
        var sources = new List<AirAccessSource>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) return Fail("JetBrains returned an unexpected list of AI access sources.");
            if (!entry.TryGetProperty("aiAccessEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) continue;
            var type = Text(entry, "type");
            var id = type is "workspace" or "license" ? SelectionId(entry, type, "orgId", "workspaceId", "licenseId") : null;
            var name = type == "workspace" ? Text(entry, "workspaceName") : type == "license" ? Text(entry, "licenseName") : null;
            if (id is null || string.IsNullOrWhiteSpace(name)) return Fail("JetBrains returned an unexpected list of AI access sources.");
            name = new string([.. name.Where(character => !char.IsControl(character))]).Trim();
            if (name.Length > 80) name = name[..80];
            if (name.Length == 0) name = id;
            if (sources.Any(source => source.Id == id)) continue;
            sources.Add(new(id, name, id == currentId));
        }
        return new(sources, null);
    }
}