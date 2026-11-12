using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace SharpRail.Host.Client;

/// <summary>
/// HTTP/1.1 over a dialed stream, one request per connection (<c>Connection: close</c>). It serves a userspace
/// network that offers TCP streams but no sockets, such as a browser's own Tailscale node. Redirects are never
/// followed and the response head is bounded.
/// </summary>
public sealed class StreamHttpHandler(Func<Uri, CancellationToken, ValueTask<Stream>> dial) : HttpMessageHandler
{
    private const int MaxHead = 16 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("The request has no address.");
        if (uri.Scheme != "http") throw new HttpRequestException("Only plain HTTP is carried over the private network; it encrypts the transport itself.");
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var stream = await dial(uri, cancellationToken);
        try
        {
            var head = new StringBuilder();
            head.Append(request.Method.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(uri.Authority).Append("\r\nConnection: close\r\n");
            foreach (var header in request.Headers) head.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            if (request.Content is not null)
                foreach (var header in request.Content.Headers.Where(header => header.Key != "Content-Length"))
                    head.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            if (request.Content is not null || request.Method != HttpMethod.Get) head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken);
            if (body.Length > 0) await stream.WriteAsync(body, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var reader = new HeadReader(stream);
            var (status, headers) = await reader.ReadHeadAsync(cancellationToken);
            var response = new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Version = HttpVersion.Version11 };
            var content = new List<(string, string)>();
            foreach (var (name, value) in headers)
                if (!response.Headers.TryAddWithoutValidation(name, value)) content.Add((name, value));
            byte[] payload;
            if (headers.Any(header => header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && header.Value.Contains("chunked", StringComparison.OrdinalIgnoreCase)))
                payload = await reader.ReadChunkedAsync(cancellationToken);
            else if (headers.FirstOrDefault(header => header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value is { } length)
                payload = await reader.ReadExactAsync(long.Parse(length, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            else payload = request.Method == HttpMethod.Head || status is 204 or 304 ? [] : await reader.ReadToEndAsync(cancellationToken);
            response.Content = new ByteArrayContent(payload);
            foreach (var (name, value) in content)
                if (!name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) response.Content.Headers.TryAddWithoutValidation(name, value);
            return response;
        }
        catch (Exception error) when (error is EndOfStreamException or FormatException or OverflowException)
        { throw new HttpRequestException("The host sent a malformed HTTP response.", error); }
        finally { await stream.DisposeAsync(); }
    }

    /// <summary>
    /// Runs the client side of the WebSocket upgrade over an open stream and wraps the stream as a WebSocket.
    /// The stream is owned by the returned socket on success and disposed on failure.
    /// </summary>
    public static async Task<WebSocket> ConnectWebSocketAsync(Stream stream, Uri uri, string subprotocol, CancellationToken cancellationToken)
    {
        try
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var request = "GET " + uri.PathAndQuery + " HTTP/1.1\r\nHost: " + uri.Authority + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + key + "\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: " + subprotocol + "\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var (status, headers) = await new HeadReader(stream, exact: true).ReadHeadAsync(cancellationToken);
            string? Header(string name) => headers.FirstOrDefault(header => header.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            if (status != 101) throw new WebSocketException(WebSocketError.NotAWebSocket, "The host refused the WebSocket upgrade (" + status + ").");
            if (Header("Sec-WebSocket-Accept") != accept) throw new WebSocketException(WebSocketError.HeaderError, "The host sent an invalid WebSocket accept key.");
            if (Header("Sec-WebSocket-Protocol") != subprotocol) throw new WebSocketException(WebSocketError.UnsupportedProtocol, "The host did not accept the RPC subprotocol.");
            return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, SubProtocol = subprotocol });
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // Reads a response head byte by byte when the rest of the stream belongs to someone else (a WebSocket);
    // otherwise buffers.
    private sealed class HeadReader(Stream stream, bool exact = false)
    {
        private readonly byte[] buffer = new byte[exact ? 1 : 8192];
        private int offset, count;

        private async ValueTask<int> ReadByteAsync(CancellationToken token)
        {
            if (offset == count)
            {
                count = await stream.ReadAsync(buffer, token);
                offset = 0;
                if (count == 0) return -1;
            }
            return buffer[offset++];
        }

        private async ValueTask<string> ReadLineAsync(CancellationToken token, int limit = MaxHead)
        {
            var line = new StringBuilder();
            while (true)
            {
                var next = await ReadByteAsync(token);
                if (next < 0) throw new EndOfStreamException();
                if (next == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= limit) throw new FormatException("The response head is too long.");
                line.Append((char)next);
            }
        }

        internal async Task<(int Status, List<(string Name, string Value)> Headers)> ReadHeadAsync(CancellationToken token)
        {
            var statusLine = await ReadLineAsync(token);
            var parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new FormatException("Not an HTTP/1.x response.");
            var status = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var headers = new List<(string, string)>();
            var total = statusLine.Length;
            while (true)
            {
                var line = await ReadLineAsync(token);
                total += line.Length;
                if (total > MaxHead) throw new FormatException("The response head is too long.");
                if (line.Length == 0) return (status, headers);
                var colon = line.IndexOf(':');
                if (colon <= 0) throw new FormatException("Malformed response header.");
                headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }

        internal async Task<byte[]> ReadExactAsync(long length, CancellationToken token)
        {
            if (length < 0 || length > int.MaxValue) throw new FormatException("Invalid content length.");
            var result = new byte[length];
            var filled = 0;
            while (filled < length)
            {
                if (offset < count)
                {
                    var take = (int)Math.Min(count - offset, length - filled);
                    Array.Copy(buffer, offset, result, filled, take);
                    offset += take; filled += take;
                    continue;
                }
                var read = await stream.ReadAsync(result.AsMemory(filled), token);
                if (read == 0) throw new EndOfStreamException();
                filled += read;
            }
            return result;
        }

        internal async Task<byte[]> ReadChunkedAsync(CancellationToken token)
        {
            using var result = new MemoryStream();
            while (true)
            {
                var size = Convert.ToInt64((await ReadLineAsync(token)).Split(';')[0].Trim(), 16);
                if (size == 0)
                {
                    while ((await ReadLineAsync(token)).Length > 0) { }
                    return result.ToArray();
                }
                result.Write(await ReadExactAsync(size, token));
                await ReadLineAsync(token);
            }
        }

        internal async Task<byte[]> ReadToEndAsync(CancellationToken token)
        {
            using var result = new MemoryStream();
            result.Write(buffer, offset, count - offset);
            offset = count;
            await stream.CopyToAsync(result, token);
            return result.ToArray();
        }
    }
}