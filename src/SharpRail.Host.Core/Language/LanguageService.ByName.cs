using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    /// <summary>Most files tried when a qualified name has several same-named declarations.</summary>
    private const int ByNameFiles = 20;

    /// <summary>Runs a positional operation named only by <see cref="LanguageRequest.Query"/>: finds the declaration through workspace symbols and runs there, or lists the candidates.</summary>
    private async Task<LanguageResult> ByNameAsync(LanguageRequest request, CancellationToken token)
    {
        var search = await ExecuteAsync(request with { Operation = "workspaceSymbols", Query = LanguageUsage.SimpleName(request.Query), Offset = 0, Limit = 200 }, token);
        var candidates = search.Locations.Where(item => LanguageUsage.NamesSymbol(item.Text, request.Query))
            .DistinctBy(item => (item.Path, item.Line, item.Column)).ToArray();
        if (candidates.Length == 0)
            return search with
            {
                State = search.State is "initializing" or "requiresAcceptance" or "expired" ? search.State : "notFound",
                Detail = LanguageUsage.ByNameNotFound(request) + " " + search.Detail,
                Locations = [],
                Total = 0,
                Truncated = false
            };
        // A type's partial declarations and its own constructors are one symbol to navigate from, not a choice for the caller.
        if (candidates.Length > 1 && await OneTypeAsync(request, candidates, token) is { } type) candidates = [type];
        if (candidates.Length > 1 && LanguageUsage.IsQualified(request.Query))
        {
            // A qualified name selects among same-named declarations through each file's outline.
            var resolved = new List<LanguageResult>();
            foreach (var path in candidates.Select(item => item.Path).Where(path => !LanguageSession.IsVirtual(path) && !Path.IsPathFullyQualified(path)).Distinct().Take(ByNameFiles))
            {
                token.ThrowIfCancellationRequested();
                var attempt = await ExecuteAsync(request with { Path = path }, token);
                if (attempt.State == "ready") resolved.Add(attempt);
            }
            if (resolved.Count == 1) return resolved[0] with { Detail = $"Resolved \"{request.Query}\" through workspace symbols. " + resolved[0].Detail };
        }
        if (candidates.Length == 1)
        {
            var target = candidates[0];
            var result = await ExecuteAsync(request with { Path = target.Path, Line = target.Line, Column = target.Column, Query = "" }, token);
            return result with { Detail = $"Resolved \"{request.Query}\" to {target.Path} line {target.Line}, column {target.Column} (zero-based) through workspace symbols. " + result.Detail };
        }
        return new("ambiguous", LanguageUsage.Ambiguous(request, candidates[0], candidates.Length), Version: request.Version)
        {
            Locations = candidates.Skip(request.Offset).Take(request.Limit).ToArray(),
            Total = candidates.Length,
            Truncated = request.Offset + request.Limit < candidates.Length,
            Freshness = "partial",
            Coverage = search.Coverage
        };
    }

    /// <summary>
    /// The type a name means when every candidate is a declaration of that one type (partial parts, which the server's definition of the first
    /// lists together) or one of its constructors in the same project; null when the name could mean different symbols.
    /// </summary>
    private async Task<LanguageItem?> OneTypeAsync(LanguageRequest request, LanguageItem[] candidates, CancellationToken token)
    {
        var name = LanguageUsage.SimpleName(request.Query);
        var types = candidates.Where(item => item.Kind is "5" or "10" or "11" or "23").ToArray();
        if (types.Length == 0 || ProjectOf(types[0]) is not { } project || types.Any(item => ProjectOf(item) != project)) return null;
        var constructors = candidates.Where(item => item.Kind is "6" or "9" && item.Container.StartsWith("in " + name + " (", StringComparison.Ordinal) && ProjectOf(item) == project).ToArray();
        if (types.Length + constructors.Length != candidates.Length) return null;
        if (types.Length == 1) return types[0];
        var first = types[0];
        var parts = await ExecuteAsync(request with { Operation = "definition", Path = first.Path, Line = first.Line, Column = first.Column, Query = "", Offset = 0, Limit = 200 }, token);
        return parts.State == "ready" && types.All(item => parts.Locations.Any(part => part.Path == item.Path && part.Line == item.Line)) ? first : null;
    }

    /// <summary>The project a Roslyn-style container names: "File.cs - Project (tfm)" for a type, "in Type (project Project (tfm))" for a member.</summary>
    private static string? ProjectOf(LanguageItem item)
    {
        var container = item.Container;
        var member = container.IndexOf("(project ", StringComparison.Ordinal);
        if (member >= 0 && container.EndsWith(')')) return container[(member + "(project ".Length)..^1];
        var file = container.LastIndexOf(" - ", StringComparison.Ordinal);
        return file >= 0 ? container[(file + 3)..] : null;
    }
}