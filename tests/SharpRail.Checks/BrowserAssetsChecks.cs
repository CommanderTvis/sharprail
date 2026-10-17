using System.IO.Compression;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class BrowserAssetsChecks
{
    internal static async Task Run(string root)
    {
        var bundle = Path.Combine(root, "browser-assets");
        Directory.CreateDirectory(bundle);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("browser asset λ\n", 2048)));
        foreach (var name in new[] { "index.html", "runtime.wasm", "assembly.dll", "main.js" })
        {
            await File.WriteAllBytesAsync(Path.Combine(bundle, name), bytes);
            foreach (var suffix in new[] { ".br", ".gz" })
            {
                await using var file = File.Create(Path.Combine(bundle, name + suffix));
                await using Stream compressed = suffix == ".br" ? new BrotliStream(file, CompressionLevel.SmallestSize) : new GZipStream(file, CompressionLevel.SmallestSize);
                await compressed.WriteAsync(bytes);
            }
        }
        await File.WriteAllBytesAsync(Path.Combine(bundle, "plain.wasm"), bytes);
        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "assets-check", browserRoot: bundle);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
            async Task<HttpResponseMessage> Request(string path, string? encoding, HttpMethod? method = null, string? etag = null)
            {
                using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
                if (encoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", encoding);
                if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                return await client.SendAsync(request);
            }
            string? brEtag = null;
            foreach (var (accept, expected) in new (string?, string?)[] { (null, null), ("br, gzip", "br"), ("gzip", "gzip"), ("br;q=0, gzip;q=0.5", "gzip"), ("br;q=0.2, gzip;q=0.9", "gzip"), ("BR", "br"), ("*;q=0.5", "br"), ("br;q=0, *;q=0.5", "gzip"), ("identity;q=1, br;q=0.5", null), ("deflate", null) })
            {
                using var response = await Request("/runtime.wasm", accept);
                Require(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentEncoding.SingleOrDefault() == expected, $"Wrong asset negotiation for {accept}.");
                Require(response.Headers.Vary.Contains("Accept-Encoding"), "All asset variants must vary by encoding.");
                Require(response.Content.Headers.ContentType?.MediaType == "application/wasm", "Compressed WASM must retain its MIME type.");
                Require(response.Headers.GetValues("Cross-Origin-Embedder-Policy").Single() == "require-corp", "Compression must preserve isolation.");
                var received = await response.Content.ReadAsByteArrayAsync();
                if (expected is not null)
                {
                    Require(received.Length < bytes.Length, "The compressed transfer must be smaller.");
                    using var input = new MemoryStream(received);
                    using Stream decoded = expected == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    await decoded.CopyToAsync(output);
                    received = output.ToArray();
                }
                Require(received.SequenceEqual(bytes), "Asset decoding must preserve exact original bytes.");
                if (expected == "br") brEtag = response.Headers.ETag!.ToString();
            }
            using (var cached = await Request("/runtime.wasm", "br", etag: brEtag))
                Require(cached.StatusCode == HttpStatusCode.NotModified && cached.Headers.Vary.Contains("Accept-Encoding"), "Compressed assets must support conditional caching.");
            using (var different = await Request("/runtime.wasm", null, etag: brEtag))
                Require(different.StatusCode == HttpStatusCode.OK, "ETags must distinguish encoding variants.");
            using (var head = await Request("/runtime.wasm", "br", HttpMethod.Head))
                Require(head.Content.Headers.ContentEncoding.Single() == "br" && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD must negotiate without sending a body.");
            foreach (var (url, type) in new[] { ("/", "text/html"), ("/assembly.dll", "application/octet-stream"), ("/main.js", "text/javascript") })
            {
                using var response = await Request(url, "br");
                Require(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == type, $"Wrong compressed MIME type for {url}.");
            }
            using (var plain = await Request("/plain.wasm", "br"))
                Require(plain.Content.Headers.ContentEncoding.Count == 0 && (await plain.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes), "Bundles without sidecars must remain usable.");
            foreach (var accept in new[] { "identity;q=0, br;q=0, gzip;q=0", "*;q=0" })
            {
                using var response = await Request("/runtime.wasm", accept);
                Require(response.StatusCode == HttpStatusCode.NotAcceptable, "Explicit encoding rejection must be respected.");
            }
            File.SetLastWriteTimeUtc(Path.Combine(bundle, "runtime.wasm"), DateTime.UtcNow.AddMinutes(1));
            using (var stale = await Request("/runtime.wasm", "br, gzip"))
                Require(stale.Content.Headers.ContentEncoding.Count == 0, "Stale sidecars must never be served.");
            foreach (var url in new[] { "/missing.wasm", "/runtime.wasm.br", "/runtime.wasm.gz" })
            {
                using var response = await Request(url, "br");
                Require(response.StatusCode == HttpStatusCode.NotFound, "Missing assets and direct sidecar URLs must return 404.");
            }
            Console.WriteLine("PASS browser compressed assets, negotiation, exact bytes, MIME types, isolation, HEAD, caching and fallback");
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(bundle, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}