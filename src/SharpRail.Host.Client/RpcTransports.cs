namespace SharpRail.Host.Client;

/// <summary>Endpoint-scoped transports for private userspace networks.</summary>
public static class RpcTransports
{
    private static readonly Dictionary<string, Registration> registrations = [];

    /// <summary>
    /// Registers a handler factory for channels subsequently created for this endpoint.
    /// Each connection attempt owns and disposes a fresh handler. Disposing the registration stops
    /// new channels using it; existing channels retain their route until they are disposed.
    /// HTTP and WebSocket forms of the same endpoint identify the same registration.
    /// </summary>
    public static IDisposable RegisterTransport(Uri endpoint, Func<HttpMessageHandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("Browser sockets use the browser's network transport.");
        var key = Key(endpoint);
        lock (registrations)
        {
            if (registrations.ContainsKey(key)) throw new InvalidOperationException("A transport is already registered for this endpoint.");
            var registration = new Registration(key, factory);
            registrations.Add(key, registration);
            return registration;
        }
    }

    internal static Registration? Find(Uri endpoint)
    {
        if (endpoint.Scheme == "unix") return null;
        var key = Key(endpoint);
        lock (registrations) return registrations.GetValueOrDefault(key);
    }

    private static string Key(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https" or "ws" or "wss"))
            throw new ArgumentException("A transport endpoint must be an absolute HTTP or WebSocket URI.", nameof(endpoint));
        return new UriBuilder(endpoint) { Scheme = endpoint.Scheme is "https" or "wss" ? "https" : "http", Port = endpoint.Port }.Uri.AbsoluteUri;
    }

    internal sealed class Registration(string key, Func<HttpMessageHandler> factory) : IDisposable
    {
        internal HttpMessageHandler CreateHandler() => factory();
        public void Dispose()
        {
            lock (registrations)
                if (registrations.GetValueOrDefault(key) == this) registrations.Remove(key);
        }
    }
}