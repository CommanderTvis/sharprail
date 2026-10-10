using System.Collections;
using System.Text;

using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Parser;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace SharpRail.Plugins.UI.Kit.Editor;

/// <summary>How a displayed diff row contributes to the old and new source streams. The prefix counts UTF-16 units and is not source.</summary>
public sealed record SyntaxLine(int PrefixLength, bool Old, bool New, bool Reset = false);

internal sealed class TextMateHighlighter
{
    internal static readonly string[] Roles = ["foreground", "comment", "commentDoc", "keyword", "string", "number", "regexp",
        "annotation", "tag", "attributeName", "attributeValue", "property", "function", "type", "variable", "constant", "operator", "punctuation"];
    internal const int MaximumCharacters = 1_048_576;
    private static readonly Lazy<Options> Catalog = new(() => new());
    private CustomGrammar[] custom = CustomHighlighting.Current;
    private Registry? registry;
    private string? scope;
    private IGrammar? grammar;
    private List<CachedLine> cache = [];
    private sealed record CachedLine(string Text, IStateStack? Before, IStateStack? After, byte[] Styles);

    internal static string? Detect(string path) => Detect(path, CustomHighlighting.Current);

    private static string? Detect(string path, CustomGrammar[] custom)
    {
        var name = Path.GetFileName(path);
        if (CustomHighlighting.Detect(path, custom) is { } customScope) return customScope;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is ".kt" or ".kts") return "source.kotlin";
        if (extension is ".scala" or ".sc" or ".sbt") return "source.scala";
        if (name is ".gitignore" or ".gitignore_global" or ".dockerignore" or ".containerignore" or ".ignore" or ".npmignore" or ".prettierignore" or ".eslintignore" or ".stylelintignore" or ".vscodeignore" or ".helmignore" or ".vercelignore" or ".gcloudignore" or ".rgignore" or ".fdignore") return "source.ignore";
        if (name == ".gitattributes") return "source.gitattributes";
        if (name is ".gitconfig" or ".gitmodules" or "gitconfig") return "source.ini";
        if (name is ".bashrc" or ".bash_aliases" or ".bash_profile" or ".bash_login" or ".bash_logout" or ".profile" or ".zshrc" or ".zshenv" or ".zprofile" or ".zlogin" or ".zlogout" or ".envrc" or ".cshrc" or ".tcshrc" or ".yashrc" or ".yash_profile" or ".xprofile" or ".xsession" or ".xsessionrc") return "source.shell";
        if (extension == ".toml") return "source.toml";
        if (extension == ".fish") return "source.fish";
        if (extension == ".slnx") return "text.xml";
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase)) return "source.dockerfile";
        if (name is "Makefile" or "GNUmakefile") return "source.makefile";
        var language = Catalog.Value.Bundled.GetLanguageByExtension(extension);
        return language is null ? null : Catalog.Value.Bundled.GetScopeByLanguageId(language.Id);
    }

    internal byte[] Highlight(string text, string path, IReadOnlyList<SyntaxLine>? rows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = CustomHighlighting.Current;
        if (!ReferenceEquals(custom, current))
        {
            custom = current;
            registry = null;
            scope = null;
            grammar = null;
            cache = [];
        }
        registry ??= new(new Options(custom));
        var detected = Detect(path, custom);
        if (scope != detected)
        {
            scope = detected;
            grammar = detected is null ? null : registry.LoadGrammar(detected);
            cache = [];
        }
        var styles = new byte[Encoding.UTF8.GetByteCount(text)];
        if (grammar is null || text.Length > MaximumCharacters) return styles;
        var lines = SplitLines(text);
        var nextCache = new List<CachedLine>(lines.Count);
        IStateStack? oldState = null, newState = null;
        var offset = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var (line, ending) = lines[index];
            var row = rows is not null && index < rows.Count ? rows[index] : new SyntaxLine(0, false, true);
            if (row.Reset) { oldState = null; newState = null; }
            var prefix = Math.Min(row.PrefixLength, line.Length);
            var source = line[prefix..];
            CachedLine? highlighted = null;
            if (row.Old) { highlighted = Tokenize(source, oldState); oldState = highlighted.After; }
            if (row.New)
            {
                highlighted = rows is null && index < cache.Count && cache[index].Text == source && Equals(cache[index].Before, newState)
                    ? cache[index] : Tokenize(source, newState);
                newState = highlighted.After;
            }
            if (highlighted is not null)
                highlighted.Styles.CopyTo(styles, offset + Encoding.UTF8.GetByteCount(line.AsSpan(0, prefix)));
            if (rows is null) nextCache.Add(highlighted!);
            offset += Encoding.UTF8.GetByteCount(line) + ending.Length;
        }
        if (rows is null) cache = nextCache;
        return styles;
    }

    private CachedLine Tokenize(string line, IStateStack? state)
    {
        var styles = new byte[Encoding.UTF8.GetByteCount(line)];
        if (line.Length > 16_384) return new(line, state, null, styles);
        var result = grammar!.TokenizeLine(line, state!, TimeSpan.FromMilliseconds(50));
        foreach (var token in result.Tokens)
        {
            var start = Math.Clamp(token.StartIndex, 0, line.Length);
            var end = Math.Clamp(token.EndIndex, start, line.Length);
            var first = Encoding.UTF8.GetByteCount(line.AsSpan(0, start));
            var count = Encoding.UTF8.GetByteCount(line.AsSpan(start, end - start));
            Array.Fill(styles, Role(token.Scopes), first, count);
        }
        return new(line, state, result.RuleStack, styles);
    }

    private static byte Role(IEnumerable<string> scopes)
    {
        foreach (var scope in scopes.Reverse())
        {
            var role = scope switch
            {
                _ when scope.StartsWith("markup.inserted", StringComparison.Ordinal) => "string",
                _ when scope.StartsWith("markup.deleted", StringComparison.Ordinal) => "annotation",
                _ when scope.StartsWith("markup.changed", StringComparison.Ordinal) || scope.StartsWith("meta.diff.header", StringComparison.Ordinal) => "keyword",
                _ when scope.StartsWith("markup.heading", StringComparison.Ordinal) => "keyword",
                _ when scope.StartsWith("markup.bold", StringComparison.Ordinal) => "type",
                _ when scope.StartsWith("markup.italic", StringComparison.Ordinal) || scope.StartsWith("markup.inline.raw", StringComparison.Ordinal) => "string",
                _ when scope.StartsWith("comment.block.documentation", StringComparison.Ordinal) => "commentDoc",
                _ when scope.StartsWith("comment", StringComparison.Ordinal) => "comment",
                _ when scope.StartsWith("string.regexp", StringComparison.Ordinal) => "regexp",
                _ when scope.StartsWith("string", StringComparison.Ordinal) => "string",
                _ when scope.StartsWith("constant.numeric", StringComparison.Ordinal) => "number",
                _ when scope.StartsWith("keyword.operator", StringComparison.Ordinal) => "operator",
                _ when scope.StartsWith("keyword", StringComparison.Ordinal) || scope.StartsWith("storage", StringComparison.Ordinal) => "keyword",
                _ when scope.StartsWith("entity.name.function", StringComparison.Ordinal) || scope.StartsWith("support.function", StringComparison.Ordinal) => "function",
                _ when scope.StartsWith("entity.name.type", StringComparison.Ordinal) || scope.StartsWith("support.type", StringComparison.Ordinal) || scope.StartsWith("entity.name.class", StringComparison.Ordinal) => "type",
                _ when scope.StartsWith("entity.name.tag", StringComparison.Ordinal) => "tag",
                _ when scope.StartsWith("entity.other.attribute-name", StringComparison.Ordinal) => "attributeName",
                _ when scope.Contains("annotation", StringComparison.Ordinal) || scope.Contains("decorator", StringComparison.Ordinal) => "annotation",
                _ when scope.StartsWith("variable.other.property", StringComparison.Ordinal) || scope.StartsWith("support.type.property-name", StringComparison.Ordinal) => "property",
                _ when scope.StartsWith("variable", StringComparison.Ordinal) => "variable",
                _ when scope.StartsWith("constant", StringComparison.Ordinal) => "constant",
                _ when scope.StartsWith("punctuation", StringComparison.Ordinal) => "punctuation",
                _ => null
            };
            if (role is not null) return (byte)Array.IndexOf(Roles, role);
        }
        return 0;
    }

    private static List<(string Text, string Ending)> SplitLines(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            var length = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            lines.Add((text[start..index], text.Substring(index, length)));
            index += length - 1;
            start = index + 1;
        }
        lines.Add((text[start..], ""));
        return lines;
    }

    private static void FlattenRepositories(Raw grammar)
    {
        var flattened = new Raw();
        var sequence = 0;
        void Visit(object? value, Dictionary<string, string> inherited)
        {
            if (value is Raw rule)
            {
                var scope = inherited;
                Raw? repository = null;
                if (rule.TryGetValue("repository", out var local) && local is Raw definitions)
                {
                    repository = definitions;
                    scope = new(inherited);
                    foreach (var pair in definitions)
                    {
                        var name = "sharprail-rule-" + sequence++;
                        scope[pair.Key] = name;
                        flattened[name] = pair.Value;
                    }
                    rule.Remove("repository");
                }
                if (rule.TryGetValue("include", out var include) && include is string reference && reference.StartsWith('#') &&
                    scope.TryGetValue(reference[1..], out var resolved)) rule["include"] = "#" + resolved;
                foreach (var child in rule.Values) Visit(child, scope);
                if (repository is not null) foreach (var child in repository.Values) Visit(child, scope);
            }
            else if (value is IList list) foreach (var child in list) Visit(child, inherited);
        }
        Visit(grammar, []);
        grammar["repository"] = flattened;
    }

    internal sealed class Options(CustomGrammar[]? custom = null) : IRegistryOptions
    {
        internal readonly RegistryOptions Bundled = new(ThemeName.DarkPlus);
        public IRawGrammar GetGrammar(string scopeName)
        {
            if (custom?.LastOrDefault(definition => definition.Scope == scopeName) is { } definition)
            {
                using var customStream = new MemoryStream(Encoding.UTF8.GetBytes(definition.Grammar));
                using var source = new StreamReader(customStream);
                return GrammarReader.ReadGrammarSync(source);
            }
            var file = scopeName switch { "source.kotlin" => "kotlin", "source.scala" => "scala", "source.toml" => "toml", "source.fish" => "fish", "source.gitattributes" => "gitattributes", _ => null };
            if (file is null)
            {
                var bundled = Bundled.GetGrammar(scopeName);
                if (scopeName == "source.swift" && bundled is Raw raw) FlattenRepositories(raw);
                return bundled;
            }
            using var stream = typeof(TextMateHighlighter).Assembly.GetManifestResourceStream($"SharpRail.Plugins.UI.Kit.Editor.Grammars.{file}.json")!;
            using var reader = new StreamReader(stream);
            return GrammarReader.ReadGrammarSync(reader);
        }
        public ICollection<string> GetInjections(string scopeName) => Bundled.GetInjections(scopeName);
        public IRawTheme GetTheme(string scopeName) => Bundled.GetTheme(scopeName);
        public IRawTheme GetDefaultTheme() => Bundled.GetDefaultTheme();
    }
}