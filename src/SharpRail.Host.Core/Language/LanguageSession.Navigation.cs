using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private async Task<LanguageResult> NavigationResultAsync(LanguageResult result, JsonNode? response, LanguageRequest request, CancellationToken token)
    {
        var symbols = request.Operation is "symbols" or "symbolBody";
        var navigation = request.Operation == "hover"
            ? Hover(response, request) : symbols ? Symbols(response, request.Path) : Locations(response, request.Path);
        var scope = request.Scope.Length == 0 ? "workspace" : request.Scope;
        var scoped = request.Operation is "references" or "implementation" or "workspaceSymbols" || request.Scope.Length > 0;
        if (scoped && scope == "module")
            return result with { State = "unsupported", Detail = "This provider does not expose reliable module ownership. Choose workspace, file or dependencies scope.", Coverage = projectModel.Description, Freshness = "partial" };
        string Absolute(string path) => Path.GetFullPath(Path.Combine(workspace, path));
        if (scoped && scope is not ("all" or "dependencies")) navigation = navigation.Where(item => scope switch
        {
            "file" => item.Path == request.Path || !IsVirtual(request.Path) && !IsVirtual(item.Path) && Path.GetRelativePath(Absolute(request.Path), Absolute(item.Path)) == ".",
            _ => !IsVirtual(item.Path) && LanguageProjectModel.Contains(workspace, Absolute(item.Path))
        });
        result = result with { Coverage = projectModel.Description + (scoped ? " Search scope: " + scope + ". Results are reported by the server's loaded project graph; no full-index completeness guarantee." : "") };
        var items = navigation
            .Distinct().OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line).ThenBy(item => item.Column).ToArray();
        if (request.Operation == "symbolBody")
        {
            items = request.Query.Length > 0
                ? items.Where(item => item.Text == request.Query).ToArray()
                : items.Where(item => (request.Line > item.Line || request.Line == item.Line && request.Column >= item.Column) &&
                    (request.Line < item.EndLine || request.Line == item.EndLine && request.Column < item.EndColumn))
                    .OrderBy(item => item.EndLine - item.Line).ThenBy(item => item.EndColumn - item.Column).Take(1).ToArray();
            if (items.Length == 0) return result with { State = "notFound", Detail = "No matching symbol. Request symbols for qualified names and ranges.", Freshness = "current" };
            if (items.Length > 1) result = result with { State = "ambiguous", Detail = "Several symbols match. Omit query and use a position inside the required symbol to select its body." };
        }
        else if (symbols && request.Query.Length > 0)
            items = items.Where(item => item.Text.Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();

        var page = items.Skip(request.Offset).Take(request.Limit).ToArray();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < page.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = page[i];
            try
            {
                var isVirtual = IsVirtual(item.Path);
                if (isVirtual) virtualLocations.TryAdd(item.Path, 0);
                var path = isVirtual ? item.Path : Path.IsPathFullyQualified(item.Path) ? item.Path : ProjectServices.Contain(workspace, item.Path);
                var draft = documents.GetValueOrDefault(isVirtual ? path : UriOf(path));
                if (!sources.TryGetValue(path, out var source))
                    sources[path] = source = isVirtual ? (await ReadVirtualAsync(path, token)).Text : draft is { Dirty: true } || item.Path == request.Path ? draft?.Text ?? await ReadAsync(path, token) : await ReadAsync(path, token);
                var lines = source.Split('\n');
                if (item.Line < 0 || item.Line >= lines.Length) continue;
                var first = Math.Max(0, item.Line - 1);
                var preview = string.Join('\n', lines.Skip(first).Take(3).Select((line, index) => $"{first + index + 1}: {line.TrimEnd('\r')}"));
                if (preview.Length > 600) preview = preview[..SafeLength(preview, 0, 600)] + "…";
                if (draft is { Dirty: true }) preview = "Unsaved editor buffer\n" + preview;
                item = item with { Preview = preview };
                if (request.Operation == "symbolBody" && result.State == "ready")
                {
                    if (item.Kind == "location")
                        return result with { State = "unsupported", Detail = "This server provides symbol locations without reliable body ranges. Use the outline and read the source range instead." };
                    var start = Offset(lines, item.Line, item.Column);
                    var end = Offset(lines, item.EndLine, item.EndColumn);
                    if (end < start) throw new IOException("The server returned an invalid symbol range.");
                    var length = SafeLength(source, start, Math.Min(end - start, 12_000));
                    item = item with { Body = source.Substring(start, length), Truncated = end - start > length };
                    if (item.Truncated) result = result with { Detail = "Symbol body exceeds 12,000 characters; read the returned source range for the remainder." };
                }
                page[i] = item;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                page[i] = item with { Preview = "Source unavailable: " + error.Message };
                if (request.Operation == "symbolBody") result = result with { State = "unavailable", Detail = "Cannot read the symbol body: " + error.Message };
            }
        }
        return result with { Locations = page, Total = items.Length, Truncated = request.Offset + page.Length < items.Length, Freshness = result.State == "unavailable" ? "unavailable" : NeedsConfiguration || IsIndexing || projectModel.Truncated ? "partial" : "current" };
    }

    private IEnumerable<LanguageItem> Hover(JsonNode? response, LanguageRequest request)
    {
        if (response?["contents"] is not { } contents) yield break;
        var text = contents is JsonArray values
            ? string.Join("\n\n", values.Select(value => value is JsonObject ? value["value"]?.ToString() ?? "" : value?.ToString() ?? ""))
            : contents is JsonObject ? contents["value"]?.ToString() ?? "" : contents.ToString();
        yield return response["range"] is { } range
            ? Item(request.Path, range, text, "hover", server.Id)
            : new(request.Path, request.Line, request.Column, request.Line, request.Column, text, "hover", server.Id);
    }

    private IEnumerable<LanguageItem> Symbols(JsonNode? node, string path, string parent = "")
    {
        foreach (var value in node as JsonArray ?? [])
        {
            if (value is null) continue;
            var name = value["name"]?.ToString() ?? "";
            var qualified = parent.Length == 0 ? name : parent + "/" + name;
            if (value["range"] is { } range)
                yield return Item(path, range, qualified, value["kind"]?.ToString() ?? "", server.Id);
            else
                foreach (var item in Locations(value, path)) yield return item with { Kind = "location" };
            foreach (var child in Symbols(value["children"], path, qualified)) yield return child;
        }
    }

    private static int SafeLength(string text, int start, int length)
    {
        if (length > 0 && start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]) && char.IsLowSurrogate(text[start + length])) length--;
        return length;
    }

    private static int Offset(string[] lines, int line, int column)
    {
        if (line < 0 || line >= lines.Length || column < 0 || column > lines[line].TrimEnd('\r').Length)
            throw new IOException("The server returned a range outside the current source text.");
        var offset = column;
        for (var i = 0; i < line; i++) offset += lines[i].Length + 1;
        return offset;
    }
}