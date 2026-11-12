namespace SharpRail.Host.Protocol;

public enum StatusCode { OK, Cancelled, Unknown, InvalidArgument, DeadlineExceeded, NotFound, AlreadyExists, PermissionDenied, ResourceExhausted, FailedPrecondition, Aborted, OutOfRange, Unimplemented, Internal, Unavailable, DataLoss, Unauthenticated }
public readonly record struct Status(StatusCode StatusCode, string Detail)
{
    public static Status DefaultSuccess => new(StatusCode.OK, "");
}
public sealed class RpcException(Status status, Metadata? trailers = null) : Exception(status.Detail)
{
    public Status Status { get; } = status;
    public StatusCode StatusCode => Status.StatusCode;
    public Metadata Trailers { get; } = trailers ?? [];
}
public sealed class Metadata : List<Metadata.Entry>
{
    public sealed record Entry(string Key, string Value)
    {
        public bool IsBinary => Key.EndsWith("-bin", StringComparison.Ordinal);
        public byte[] ValueBytes => Convert.FromBase64String(Value);
    }
    public void Add(string key, string value) => Add(new Entry(key, value));
    public void Add(string key, byte[] value) => Add(key, Convert.ToBase64String(value));
    public string? GetValue(string key) => this.LastOrDefault(entry => entry.Key == key)?.Value;
    public byte[]? GetValueBytes(string key) => this.LastOrDefault(entry => entry.Key == key)?.ValueBytes;
}
public readonly record struct CallOptions
{
    public CallOptions(Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
    { Headers = headers; Deadline = deadline; CancellationToken = cancellationToken; }
    public Metadata? Headers { get; init; }
    public DateTime? Deadline { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public CallOptions WithHeaders(Metadata headers) => this with { Headers = headers };
    public CallOptions WithDeadline(DateTime deadline) => this with { Deadline = deadline };
}
public readonly struct CallContext(CallOptions options)
{
    public CallOptions Options { get; } = options;
    public CancellationToken CancellationToken => Options.CancellationToken;
    public Metadata? RequestHeaders => Options.Headers;
    public string Method { get; init; } = "";
    public static implicit operator CallContext(CancellationToken token) => new(new(cancellationToken: token));
}
public interface IRpcClient
{
    ValueTask<TReply> Unary<TRequest, TReply>(string method, TRequest request, CallContext context);
    IAsyncEnumerable<TReply> Stream<TRequest, TReply>(string method, TRequest request, CallContext context);
    IAsyncEnumerable<TReply> Duplex<TRequest, TReply>(string method, IAsyncEnumerable<TRequest> requests, CallContext context);
}