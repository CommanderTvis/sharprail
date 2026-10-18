using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private readonly ConcurrentDictionary<string, byte> virtualLocations = new(StringComparer.Ordinal);
    private sealed record DependencySource(string Text, string Language);

    internal static bool IsVirtual(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile;
    internal bool OwnsVirtual(string uri) => !Exited && virtualLocations.ContainsKey(uri);

    private async Task<DependencySource> ReadVirtualAsync(string uri, CancellationToken token)
    {
        if (!OwnsVirtual(uri)) throw new IOException("Dependency source belongs to a different or ended language session.");
        if (server.Id != "intellij" || !(uri.StartsWith("jar:", StringComparison.Ordinal) || uri.StartsWith("jrt:", StringComparison.Ordinal)))
            throw new IOException("This provider or URI scheme has no supported source reader. The original location is preserved.");
        if (capabilities["executeCommandProvider"]?["commands"] is not JsonArray commands || !commands.Any(command => command?.ToString() == "decompile"))
            throw new IOException("The server does not advertise dependency source retrieval.");
        // Public IntelliJ client protocol: preserve the URI exactly, including JAR entry and JDK module.
        var response = await connection.RequestAsync("workspace/executeCommand", new { command = "decompile", arguments = new[] { uri } }, token);
        if (response?["code"] is not JsonValue code || !code.TryGetValue<string>(out var text)) throw new IOException("The server returned no dependency source.");
        if (text.Length > 1_048_576) throw new IOException("Dependency source exceeds the 1 Mi character limit.");
        return new(text, response["language"]?.ToString() ?? "java");
    }

    private LanguageResult SourceResult(LanguageRequest request, string text)
    {
        if (request.Offset > text.Length || request.Offset > 0 && request.Offset < text.Length && char.IsLowSurrogate(text[request.Offset]) && char.IsHighSurrogate(text[request.Offset - 1]))
            throw new ArgumentException("Source offset is outside the text or splits a UTF-16 character.");
        var length = SafeLength(text, request.Offset, Math.Min(12_000, text.Length - request.Offset));
        var prefix = text[..request.Offset];
        var body = text.Substring(request.Offset, length);
        var through = text[..(request.Offset + length)];
        return new("ready", "Read-only source; offsets count UTF-16 characters. Re-query navigation after a restart to obtain fresh dependency locations.", server.Id, request.Version)
        {
            Locations = [new(request.Path, prefix.Count(c => c == '\n'), prefix.Length - prefix.LastIndexOf('\n') - 1,
                through.Count(c => c == '\n'), through.Length - through.LastIndexOf('\n') - 1, Source: server.Id, Body: body, Truncated: request.Offset + length < text.Length)],
            Total = text.Length,
            Truncated = request.Offset + length < text.Length,
            Coverage = projectModel.Description,
            Freshness = NeedsConfiguration || IsIndexing || projectModel.Truncated ? "partial" : "current"
        };
    }

    private static IEnumerable<(string Name, JsonNode? Range)> SymbolSelections(JsonNode? node, string parent = "")
    {
        foreach (var value in node as JsonArray ?? [])
        {
            if (value is null) continue;
            var name = value["name"]?.ToString() ?? "";
            var qualified = parent.Length == 0 ? name : parent + "/" + name;
            yield return (qualified, value["selectionRange"]);
            foreach (var child in SymbolSelections(value["children"], qualified)) yield return child;
        }
    }
}