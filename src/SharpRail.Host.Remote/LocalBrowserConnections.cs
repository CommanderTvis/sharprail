using System.Net;
using System.Security.Cryptography;

using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

/// <summary>In-memory browser approvals owned by one embedded listener generation.</summary>
public sealed class LocalBrowserConnections(Func<string, CancellationToken, Task<bool>> confirm) : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, string> grants = [];
    private readonly SemaphoreSlim pending = new(1, 1);
    private readonly CancellationTokenSource stopped = new();
    private long nextRequest;

    /// <summary>Approved browser origins; credentials are never exposed in this projection.</summary>
    public IReadOnlyList<string> Origins { get { lock (sync) return [.. grants.Values.Distinct().Order()]; } }
    /// <summary>Raised off the UI thread when approvals change.</summary>
    public event Action? Changed;

    /// <summary>Revokes every approved session from an origin, including active RPC calls.</summary>
    public void Revoke(string origin)
    {
        lock (sync)
            foreach (var key in grants.Where(pair => pair.Value == origin).Select(pair => pair.Key).ToArray()) grants.Remove(key);
        Changed?.Invoke();
    }

    internal bool Authenticate(string token, HttpContext context)
    {
        if (!IsLocalRequest(context)) return false;
        lock (sync) return !stopped.IsCancellationRequested && grants.TryGetValue(token, out var origin) && origin == context.Request.Headers.Origin.ToString();
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    private static bool IsLocalRequest(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            context.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local) &&
            IsLoopbackHost(context.Request.Host.Host) &&
            Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            IsLoopbackHost(uri.Host) && uri.UserInfo.Length == 0 && origin == uri.GetLeftPart(UriPartial.Authority);
    }

    internal void Map(WebApplication app)
    {
        app.Lifetime.ApplicationStopping.Register(() => { stopped.Cancel(); lock (sync) grants.Clear(); Changed?.Invoke(); });
        app.MapMethods("/browser/connect", ["POST", "OPTIONS"], async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Vary = "Origin";
            if (!IsLocalRequest(context)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            context.Response.Headers.AccessControlAllowOrigin = context.Request.Headers.Origin;
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.Headers.AccessControlAllowMethods = "POST";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            if (!await pending.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
            string? token = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopped.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                lock (sync)
                {
                    if (grants.Count >= 32 || Environment.TickCount64 < nextRequest)
                    { context.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
                }
                var origin = context.Request.Headers.Origin.ToString();
                var allowed = await confirm(origin, timeout.Token).WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (!allowed)
                {
                    lock (sync) nextRequest = Environment.TickCount64 + 2000;
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                lock (sync)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    grants.Add(token, origin);
                }
                Changed?.Invoke();
                await context.Response.WriteAsJsonAsync(new LocalBrowserApprovalReply(token), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (token is not null) { lock (sync) grants.Remove(token); Changed?.Invoke(); }
                if (!context.RequestAborted.IsCancellationRequested) context.Response.StatusCode = stopped.IsCancellationRequested ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status408RequestTimeout;
            }
            finally { pending.Release(); }
        });
    }

    public void Dispose() => stopped.Dispose();
}