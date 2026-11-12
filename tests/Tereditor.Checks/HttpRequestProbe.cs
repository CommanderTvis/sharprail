using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpRail.Checks;

internal sealed class HttpRequestProbe : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly ConcurrentBag<IDisposable> subscriptions = [];
    private readonly IDisposable listeners;
    private int requests;

    internal HttpRequestProbe() => listeners = DiagnosticListener.AllListeners.Subscribe(this);
    internal int Requests => Volatile.Read(ref requests);

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "HttpHandlerDiagnosticListener") subscriptions.Add(listener.Subscribe(this));
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == "System.Net.Http.HttpRequestOut.Start") Interlocked.Increment(ref requests);
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        listeners.Dispose();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}