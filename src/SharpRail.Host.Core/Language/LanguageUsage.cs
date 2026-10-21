using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>Working call shapes for lsp_navigate, so a rejected call names the one that works.</summary>
internal static class LanguageUsage
{
    private static readonly string[] Positional = ["hover", "definition", "typeDefinition", "implementation", "references"];

    /// <summary>A symbol query without a path searches the workspace instead of failing.</summary>
    internal static bool MapsToWorkspaceSearch(LanguageRequest request)
        => request.Path.Length == 0 && request.Query.Length > 0 && (Positional.Contains(request.Operation) || request.Operation is "symbols" or "symbolBody");

    /// <summary>A positional operation named by a symbol instead of a file: resolved through workspace symbols, then run at the declaration.</summary>
    internal static bool ResolvesByName(LanguageRequest request)
        => request.Path.Length == 0 && request.Query.Length > 0 && Positional.Contains(request.Operation);

    /// <summary>Whether the query names more than a bare identifier, such as <c>Foo/Bar</c> or <c>Ns.Foo.Bar</c>.</summary>
    internal static bool IsQualified(string query) => Segments(query, query).Length > 1;

    /// <summary>The query's last segment, which is what servers' workspace-symbol search understands.</summary>
    internal static string SimpleName(string query) => Segments(query, "").LastOrDefault() ?? query;

    /// <summary>Whether a workspace-symbol name has the query's last segment as its simple name.</summary>
    internal static bool NamesSymbol(string name, string query)
    {
        var symbol = Segments(name, query);
        var wanted = Segments(query, query);
        return symbol.Length > 0 && wanted.Length > 0 && symbol[^1] == wanted[^1];
    }

    /// <summary>Whether a qualified outline name ends with the query's segments: <c>Run</c>, <c>Suite/Run</c> and <c>Ns.Suite.Run</c> all name <c>Ns/Suite/Run() : void</c>.</summary>
    internal static bool EndsWithName(string qualified, string query)
    {
        var symbol = Segments(qualified, query);
        var wanted = Segments(query, query);
        return wanted.Length > 0 && wanted.Length <= symbol.Length && symbol[^wanted.Length..].SequenceEqual(wanted, StringComparer.Ordinal);
    }

    /// <summary>Name segments split on <c>/</c> and <c>.</c>, without the <c> : type</c> suffix, and without parameter lists unless the query spells one.</summary>
    private static string[] Segments(string name, string query)
    {
        var typed = name.IndexOf(" : ", StringComparison.Ordinal);
        if (typed >= 0) name = name[..typed];
        var parameters = name.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0 && !query.Contains('(', StringComparison.Ordinal)) name = name[..parameters];
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is '(' or '<') depth++;
            else if (name[i] is ')' or '>') depth--;
            else if (depth == 0 && name[i] is '/' or '.')
            {
                segments.Add(name[start..i]);
                start = i + 1;
            }
        }
        segments.Add(name[start..]);
        return segments.Select(segment => segment.Trim()).Where(segment => segment.Length > 0).ToArray();
    }

    internal static string ByNameNotFound(LanguageRequest request)
        => $"No workspace symbol is named \"{request.Query}\". Find the spelling with "
            + $$"""{"operation":"workspaceSymbols","query":"<part of the name>","workspace":"{{request.Workspace}}"}, then repeat {{request.Operation}} with the exact name as query.""";

    internal static string Ambiguous(LanguageRequest request, LanguageItem first, int count)
        => $"{count} symbols are named \"{request.Query}\"; locations lists the candidates. Pick one and repeat with its path and position (zero-based), for example "
            + $$"""{"operation":"{{request.Operation}}","path":"{{first.Path}}","line":{{first.Line}},"column":{{first.Column}},"workspace":"{{request.Workspace}}"}""" + ", or narrow query with its containing type, such as \"Type/" + Segments(request.Query, request.Query)[^1] + "\".";

    internal static string OutlineNotFound(LanguageRequest request)
        => $"No symbol in {request.Path} is named \"{request.Query}\". List its qualified names with "
            + $$"""{"operation":"symbols","path":"{{request.Path}}","workspace":"{{request.Workspace}}"}, or omit path to look the name up across the workspace.""";

    internal static string OutlineAmbiguous(LanguageRequest request, int count)
        => $"{count} symbols in {request.Path} match \"{request.Query}\"; locations lists them with qualified names. Repeat with one name as query, or with its line and column (zero-based).";

    /// <summary>
    /// The state of a path-free call answered by every server for the workspace's languages. A symbol search answered by
    /// some servers succeeds: servers missing for other languages (pwsh, go) are listed in the detail, not reported as a failed call.
    /// </summary>
    internal static string CombinedState(string operation, IReadOnlyList<string> states)
    {
        var state = states.Contains("expired") ? "expired" : states.Contains("requiresAcceptance") ? "requiresAcceptance"
            : states.Any(item => item is "installing" or "initializing") ? "initializing" : states.Contains("error") ? "error"
            : states.Contains("unavailable") ? "unavailable" : "ready";
        return state is "error" or "unavailable" && operation == "workspaceSymbols" && states.Contains("ready") ? "ready" : state;
    }

    internal static string PathRequired(string operation, string workspace)
    {
        var example = operation switch
        {
            "symbols" => $$"""{"operation":"symbols","path":"src/Foo.cs","workspace":"{{workspace}}"}""",
            "symbolBody" => $$"""{"operation":"symbolBody","path":"src/Foo.cs","query":"Foo/Bar","workspace":"{{workspace}}"}""",
            "source" => $$"""{"operation":"source","path":"<dependency URI from a navigation result>","server":"<its source server>","workspace":"{{workspace}}"}""",
            _ when Positional.Contains(operation) => $$"""{"operation":"{{operation}}","query":"Foo","workspace":"{{workspace}}"} (or a path with a zero-based line and column)""",
            _ => $$"""{"operation":"{{operation}}","path":"src/Foo.cs","workspace":"{{workspace}}"}"""
        };
        return $"The {operation} operation needs {(Positional.Contains(operation) ? "a symbol name as query or" : "")} a workspace-relative path. Working call: {example}. To find a symbol without knowing its file, pass only a query: "
            + $$"""{"operation":"workspaceSymbols","query":"Foo","workspace":"{{workspace}}"}.""";
    }
}