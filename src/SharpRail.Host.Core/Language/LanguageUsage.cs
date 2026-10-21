using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>Working call shapes for lsp_navigate, so a rejected call names the one that works.</summary>
internal static class LanguageUsage
{
    private static readonly string[] Positional = ["hover", "definition", "typeDefinition", "implementation", "references"];

    /// <summary>A symbol query without a path searches the workspace instead of failing.</summary>
    internal static bool MapsToWorkspaceSearch(LanguageRequest request)
        => request.Path.Length == 0 && request.Query.Length > 0 && (Positional.Contains(request.Operation) || request.Operation is "symbols" or "symbolBody");

    internal static string PathRequired(string operation, string workspace)
    {
        var example = operation switch
        {
            "symbols" => $$"""{"operation":"symbols","path":"src/Foo.cs","workspace":"{{workspace}}"}""",
            "symbolBody" => $$"""{"operation":"symbolBody","path":"src/Foo.cs","query":"Foo/Bar","workspace":"{{workspace}}"}""",
            "source" => $$"""{"operation":"source","path":"<dependency URI from a navigation result>","server":"<its source server>","workspace":"{{workspace}}"}""",
            _ when Positional.Contains(operation) => $$"""{"operation":"{{operation}}","path":"src/Foo.cs","line":10,"column":5,"workspace":"{{workspace}}"}""",
            _ => $$"""{"operation":"{{operation}}","path":"src/Foo.cs","workspace":"{{workspace}}"}"""
        };
        return $"The {operation} operation needs a workspace-relative path. Working call: {example}. To find a symbol without knowing its file, pass only a query: "
            + $$"""{"operation":"workspaceSymbols","query":"Foo","workspace":"{{workspace}}"}.""";
    }
}