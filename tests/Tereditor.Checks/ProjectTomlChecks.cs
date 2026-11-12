using System.Text.Json.Nodes;

using Tereditor.Host.Core;
using Tereditor.Plugins.Codex.Host;

using Tomlyn;
using Tomlyn.Model;

namespace Tereditor.Checks;

/// <summary>
/// TOML read through generated Tomlyn metadata means what the reflection reader made of it: representative project files
/// (<c>workspace.toml</c>, <c>language.toml</c>, <c>instructions.toml</c>), a Codex <c>config.toml</c> and a document with every TOML
/// value kind give the same model tree, the project and Codex readers turn them into the same JSON as before, and broken
/// input fails the same way.
/// </summary>
internal static class ProjectTomlChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly (string Name, string Text)[] Documents =
    [
        ("workspace.toml", """
            # Ignored paths cloned from the Default workspace into new workspaces.
            version = 1

            [clone]
            include = ["**/obj", "**/bin", "node_modules"]
            exclude = ["**/obj/native"]
            """),
        ("language.toml", """
            version = 1

            [servers.typescript]
            enabled = true
            autoStart = false
            arguments = ["--stdio", "--log-level", "4"]
            initializationOptions = { preferences = { quotePreference = "single", includeInlayParameterNameHints = "all" } }

            [servers.python.settings.python.analysis]
            typeCheckingMode = "strict"
            extraPaths = ["src", "tests"]
            diagnosticSeverityOverrides = { reportMissingImports = "none" }

            [servers."c-sharp"]
            memoryLimitMb = 2048
            future = { nested = [1, 2.5, "three", true] }
            """),
        ("instructions.toml", """
            version = 1

            [[sections]]
            id = "style"
            title = "Code style"
            text = '''
            Keep changes small.
            Match the existing style: "quotes" and \backslashes\ stay.
            '''

            [[sections]]
            id = "unicode"
            title = "Ünïcødé ✓ 日本語"
            text = "Tabs\tand\nnew lines and \u00e9 escapes"
            """),
        ("config.toml", """
            model = "gpt-5-codex"
            model_reasoning_effort = "high"
            approval_policy = "on-request"

            [projects."/Users/someone/project"]
            trust_level = "trusted"

            [mcp_servers.tereditor]
            command = "tereditor"
            args = ["mcp", "--workspace", "/Users/someone/project"]
            env = { TEREDITOR_TOKEN = "secret" }
            startup_timeout_sec = 20.5

            [profiles.fast]
            model = "gpt-5-mini"
            """),
        ("every-kind.toml", """"
            string = "text"
            literal = 'C:\path'
            multiline = """
            one
            two"""
            integer = -42
            hex = 0xDEAD_BEEF
            octal = 0o755
            binary = 0b1010
            float = 6.25e-3
            infinity = inf
            negative-infinity = -inf
            not-a-number = nan
            yes = true
            no = false
            offset-date-time = 1979-05-27T07:32:00-08:00
            utc-date-time = 1979-05-27T07:32:00.999999Z
            local-date-time = 1979-05-27T07:32:00
            local-date = 1979-05-27
            local-time = 07:32:00.5
            empty-array = []
            mixed-array = [1, "two", [3, 4], { five = 5 }]
            dotted.key.path = "deep"
            "quoted key" = "quoted"
            "" = "empty key"

            [table]
            inline = { a = 1, b = { c = [true, false] } }

            [[tables]]
            name = "first"

            [[tables]]
            name = "second"
            [tables.child]
            value = 2
            """"),
    ];

    internal static void Run()
    {
        foreach (var (name, text) in Documents)
        {
            var reflected = TomlSerializer.Deserialize<TomlTable>(text)!;
            var project = TomlSerializer.Deserialize(text, ProjectTomlContext.Default.TomlTable)!;
            var codex = TomlSerializer.Deserialize(text, CodexTomlContext.Default.TomlTable)!;
            Require(Same(reflected, project, name), $"{name}: the project reader's generated model differs from the reflection model.");
            Require(Same(reflected, codex, name), $"{name}: the Codex reader's generated model differs from the reflection model.");
            Require(JsonNode.DeepEquals(ProjectToml.Parse(text), Expected(reflected, finiteOnly: false)), $"{name}: the project reader's JSON changed.");
            Require(JsonNode.DeepEquals(CodexConfig.Parse(text), Expected(reflected, finiteOnly: true)), $"{name}: the Codex reader's JSON changed.");
        }
        Require(ProjectToml.Parse("").Count == 0 && CodexConfig.Parse("").Count == 0, "An empty file reads as an empty table.");

        foreach (var broken in new[] { "key = ", "[table\nkey = 1", "a = [1, 2", "a = \"unterminated" })
        {
            var reflected = Failure(() => TomlSerializer.Deserialize<TomlTable>(broken));
            var project = Failure(() => ProjectToml.Parse(broken));
            var codex = Failure(() => CodexConfig.Parse(broken));
            Require(reflected is TomlException && project?.GetType() == reflected.GetType() && codex?.GetType() == reflected.GetType(),
                $"Broken TOML {broken.ReplaceLineEndings("\\n")} fails as before ({reflected?.GetType().Name}, project {project?.GetType().Name}, Codex {codex?.GetType().Name}).");
        }
        Console.WriteLine("PASS project TOML checks");
    }

    private static Exception? Failure(Action read)
    {
        try { read(); return null; }
        catch (Exception error) { return error; }
    }

    private static bool Same(object? left, object? right, string path) => (left, right) switch
    {
        (TomlTable a, TomlTable b) => a.Count == b.Count && a.Keys.SequenceEqual(b.Keys) && a.All(pair => Same(pair.Value, b[pair.Key], path + "." + pair.Key)),
        (TomlTableArray a, TomlTableArray b) => a.Count == b.Count && a.Zip(b).All(pair => Same(pair.First, pair.Second, path + "[]")),
        (TomlArray a, TomlArray b) => a.Count == b.Count && a.Zip(b).All(pair => Same(pair.First, pair.Second, path + "[]")),
        (null, null) => true,
        _ => left!.GetType() == right?.GetType() && (left is double number ? number.Equals((double)right) : left.Equals(right))
    };

    // The conversion both readers applied before generated metadata: tables become objects, arrays arrays, primitives JSON
    // values, anything else (dates and times) its text; the Codex reader keeps only finite floats.
    private static JsonNode? Expected(object? value, bool finiteOnly) => value switch
    {
        TomlTable table => new JsonObject(table.Select(pair => KeyValuePair.Create(pair.Key, Expected(pair.Value, finiteOnly)))),
        TomlTableArray tables => new JsonArray([.. tables.Select(table => Expected(table, finiteOnly))]),
        TomlArray array => new JsonArray([.. array.Select(item => Expected(item, finiteOnly))]),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long integer => JsonValue.Create(integer),
        double number when !finiteOnly || double.IsFinite(number) => JsonValue.Create(number),
        null => null,
        _ => JsonValue.Create(value.ToString())
    };
}