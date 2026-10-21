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
}