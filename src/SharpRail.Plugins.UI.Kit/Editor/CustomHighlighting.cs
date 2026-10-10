using System.IO.Enumeration;
using System.Text.Json;

using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Registry;

namespace SharpRail.Plugins.UI.Kit.Editor;

internal sealed record CustomGrammar(string Name, string Scope, string[] Patterns, string Grammar);

internal static class CustomHighlighting
{
    private static CustomGrammar[] current = [];
    internal static CustomGrammar[] Current => Volatile.Read(ref current);
    internal static event Action? Changed;

    internal static CustomGrammar[] Read(string json) => string.IsNullOrWhiteSpace(json)
        ? [] : JsonSerializer.Deserialize<CustomGrammar[]>(json) ?? [];

    internal static CustomGrammar Import(string json, string patterns)
    {
        if (json.Length > 512 * 1024) throw new ArgumentException("The grammar must be at most 512 Ki characters.");
        using var document = JsonDocument.Parse(json);
        var scope = document.RootElement.GetProperty("scopeName").GetString();
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("The grammar needs a scopeName.");
        var names = patterns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0 || names.Length > 32 || names.Any(name => name.Length > 200 || name.Contains('/') || name.Contains('\\')))
            throw new ArgumentException("Enter up to 32 filename patterns separated by commas, such as *.foo, Foofile.");
        var name = document.RootElement.TryGetProperty("name", out var label) ? label.GetString() : null;
        var definition = new CustomGrammar(name ?? scope, scope, names, json);
        var grammar = new Registry(new TextMateHighlighter.Options([definition])).LoadGrammar(scope)
            ?? throw new ArgumentException("The grammar could not be loaded.");
        grammar.TokenizeLine("", null!, TimeSpan.FromMilliseconds(50));
        return definition;
    }

    internal static void Apply(CustomGrammar[] definitions)
    {
        Volatile.Write(ref current, definitions);
        Changed?.Invoke();
    }

    internal static string? Detect(string path, CustomGrammar[] definitions) =>
        definitions.LastOrDefault(definition => definition.Patterns.Any(pattern =>
            FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(path), ignoreCase: true)))?.Scope;
}