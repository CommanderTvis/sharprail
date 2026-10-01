using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpRail.Checks;

/// <summary>
/// Fails when a project reference or a source mention crosses the documented dependency direction.
/// A project is named by its root namespace, so one scan covers using directives, aliases, static and
/// global usings, qualified names and XAML namespaces alike.
/// </summary>
internal static class BoundaryChecks
{
    private const string Abstractions = "SharpRail.Host.Abstractions", Protocol = "SharpRail.Host.Protocol", Core = "SharpRail.Host.Core",
        Client = "SharpRail.Host.Client", Remote = "SharpRail.Host.Remote", Scintilla = "SharpRail.Scintilla", Ghostty = "Ghostty.Avalonia",
        UI = "SharpRail.UI", Checks = "SharpRail.Checks", Api = "SharpRail.Plugins.Api",
        ApiHost = "SharpRail.Plugins.Api.Host", ApiUi = "SharpRail.Plugins.Api.UI", Kit = "SharpRail.Plugins.UI.Kit";

    /// <summary>Every project and what it may reach, directly or through a reference. A new project needs its own entry.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [Abstractions] = [Api],
        [Protocol] = [],
        [Core] = [Abstractions, Api, ApiHost, "SharpRail.Plugins.ClaudeCode", "SharpRail.Plugins.Codex"],
        [Client] = [Abstractions, Protocol, Api],
        [Remote] = [Abstractions, Protocol, Core, Api, ApiHost],
        [Scintilla] = [],
        [Ghostty] = [],
        [UI] = [Abstractions, Core, Client, Remote, Scintilla, Ghostty, Api, ApiUi, Kit, "SharpRail.Plugins.ClaudeCode", "SharpRail.Plugins.Codex"],
        [Checks] = [Abstractions, Protocol, Core, Client, Remote, Scintilla, Ghostty, UI, Api, ApiHost, ApiUi, Kit,
            "SharpRail.Plugins.ClaudeCode", "SharpRail.Plugins.Codex", "SharpRail.PluginFixture.Host", "SharpRail.PluginFixture.UI"],
        [Api] = [],
        [ApiHost] = [Api],
        [ApiUi] = [Api],
        [Kit] = [Scintilla],
        ["SharpRail.Plugins.Agent.UI"] = [Kit],
        ["SharpRail.PluginFixture.Host"] = [Api, ApiHost],
        ["SharpRail.PluginFixture.UI"] = [Api, ApiUi, Kit]
    };

    private static readonly string[] Generated = ["bin", "obj", "artifacts", "node_modules"];

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static string? Checkout()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SharpRail.slnx"))) return directory.FullName;
        return null;
    }

    internal static void Run(string root)
    {
        Fixtures(Path.Combine(root, "boundaries"));
        if (Checkout() is not { } checkout) { Console.WriteLine("SKIP repository boundary scan: no source checkout"); return; }
        var violations = Scan(checkout);
        Require(violations.Count == 0, "Dependency boundaries crossed:\n" + string.Join('\n', violations));
        Console.WriteLine("PASS dependency boundaries: project references and source mentions follow the documented direction");
    }

    /// <summary>The repository gate and the fixtures both enter here.</summary>
    internal static List<string> Scan(string root)
    {
        var projects = new[] { "src", "tests" }.Select(area => Path.Combine(root, area)).Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories).SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).Where(project => !Path.GetRelativePath(directory, project).Split(Path.DirectorySeparatorChar).Any(Generated.Contains))).Order(StringComparer.Ordinal).ToArray();
        var rules = Allowed.ToDictionary();
        foreach (var plugin in new[] { "SpecDialect", "Blueprint", "ClaudeCode", "Discord", "PdfPreview", "BranchGraph", "Visualize", "FileIcons", "Codex" })
        {
            var contract = "SharpRail.Plugins." + plugin;
            rules[contract] = [Api];
            rules[contract + ".Host"] = [Api, ApiHost, contract, "SharpRail.Plugins.SpecDialect"];
            rules[contract + ".UI"] = [Api, ApiUi, Kit, Scintilla, contract, "SharpRail.Plugins.SpecDialect", "SharpRail.Plugins.Agent.UI"];
            rules[Core] = [.. rules[Core], contract, contract + ".Host"];
            rules[UI] = [.. rules[UI], contract, contract + ".UI"];
            rules[Checks] = [.. rules[Checks], contract, contract + ".Host", contract + ".UI"];
        }
        var names = projects.Select(project => Path.GetFileNameWithoutExtension(project)).ToArray();
        var mention = new Regex(@"(?<![\w.])(" + string.Join('|', names.OrderByDescending(name => name.Length).Select(Regex.Escape)) + @")(?!\w)", RegexOptions.CultureInvariant);
        var shared = Directory.EnumerateFiles(root, "Directory.Build.*").Where(file => Path.GetExtension(file) is ".props" or ".targets").ToArray();
        var violations = new List<string>();
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            if (!rules.TryGetValue(name, out var allowed))
            {
                violations.Add($"{name}: no boundary rule; add the project and what it may depend on");
                continue;
            }
            void Report(string file, string text, string kind)
            {
                foreach (var target in mention.Matches(text).Select(match => match.Value).Distinct().Where(target => target != name && !allowed.Contains(target)))
                    violations.Add($"{name} -> {target}: {kind} in {Path.GetRelativePath(root, file)}");
            }
            foreach (var manifest in shared.Append(project))
                foreach (var item in XDocument.Load(manifest).Descendants().Where(item => item.Name.LocalName != "InternalsVisibleTo"))
                    Report(manifest, string.Join(' ', new[] { item.Attribute("Include")?.Value, item.Name.LocalName == "HintPath" ? item.Value : null }), "project item");
            foreach (var source in Sources(Path.GetDirectoryName(project)!))
                Report(source, source.EndsWith(".cs", StringComparison.Ordinal) ? Code(File.ReadAllText(source)) : File.ReadAllText(source), "source mention");
        }
        return violations;
    }

    private static IEnumerable<string> Sources(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
            if (Path.GetExtension(file) is ".cs" or ".axaml") yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
            if (!Generated.Contains(Path.GetFileName(child)) && !Path.GetFileName(child).StartsWith('.') && !Directory.EnumerateFiles(child, "*.csproj").Any())
                foreach (var file in Sources(child)) yield return file;
    }

    /// <summary>C# with comments and string and character literals blanked, so only code can name a project.</summary>
    internal static string Code(string text)
    {
        var code = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length;)
        {
            var end = index;
            if (text.AsSpan(index).StartsWith("//")) end = text.IndexOf('\n', index) is var line and >= 0 ? line : text.Length;
            else if (text.AsSpan(index).StartsWith("/*")) end = text.IndexOf("*/", index + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : text.Length;
            else if (text[index] == '"')
            {
                var quotes = 1;
                while (index + quotes < text.Length && text[index + quotes] == '"') quotes++;
                if (quotes >= 3) end = text.IndexOf(new string('"', quotes), index + quotes, StringComparison.Ordinal) is var raw and >= 0 ? raw + quotes : text.Length;
                else if (quotes == 2) end = index + 2;
                else
                {
                    var verbatim = index > 0 && (text[index - 1] == '@' || (index > 1 && text[index - 1] == '$' && text[index - 2] == '@'));
                    for (end = index + 1; end < text.Length && text[end] != '"'; end++)
                        if (!verbatim && text[end] == '\\') end++;
                        else if (verbatim && text[end] == '"' && end + 1 < text.Length && text[end + 1] == '"') end++;
                    end = Math.Min(text.Length, end + 1);
                }
            }
            else if (text[index] == '\'')
            {
                for (end = index + 1; end < text.Length && text[end] != '\''; end++)
                    if (text[end] == '\\') end++;
                end = Math.Min(text.Length, end + 1);
            }
            if (end == index) code.Append(text[index++]);
            else { code.Append(' '); index = end; }
        }
        return code.ToString();
    }

    private static void Fixtures(string directory)
    {
        var count = 0;
        string Tree(params (string Path, string Text)[] files)
        {
            var root = Path.Combine(directory, "case-" + ++count);
            foreach (var (path, text) in files.Append(("src/SharpRail.Host.Abstractions/SharpRail.Host.Abstractions.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"))
                .Append(("src/SharpRail.Host.Core/SharpRail.Host.Core.csproj", Project("../SharpRail.Host.Abstractions/SharpRail.Host.Abstractions.csproj"))))
            {
                var file = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (!File.Exists(file)) File.WriteAllText(file, text);
            }
            return root;
        }
        static string Project(params string[] references)
            => "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" + string.Concat(references.Select(reference => $"<ProjectReference Include=\"{reference}\" />")) + "</ItemGroup></Project>";
        void Crosses(string edge, string file, params (string Path, string Text)[] files)
        {
            var violations = Scan(Tree(files));
            Require(violations.Count == 1 && violations[0].StartsWith(edge + ":", StringComparison.Ordinal) && violations[0].EndsWith(file, StringComparison.Ordinal),
                $"Expected only {edge} in {file}, found: {string.Join("; ", violations)}");
        }
        void Clean(params (string Path, string Text)[] files)
        {
            var violations = Scan(Tree(files));
            Require(violations.Count == 0, "A permitted tree was rejected: " + string.Join("; ", violations));
        }
        const string core = "src/SharpRail.Host.Core/", ui = "src/SharpRail.UI/SharpRail.UI.csproj", client = "src/SharpRail.Host.Client/SharpRail.Host.Client.csproj",
            remote = "src/SharpRail.Host.Remote/SharpRail.Host.Remote.csproj", bare = "<Project Sdk=\"Microsoft.NET.Sdk\" />";

        Clean((core + "Host.cs", "using SharpRail.Host.Abstractions;\nnamespace SharpRail.Host.Core;\ninternal sealed class Host : IWorkspaceHost;\n"));
        Crosses("SharpRail.Host.Core -> SharpRail.UI", "SharpRail.Host.Core.csproj", (ui, bare),
            (core + "SharpRail.Host.Core.csproj", Project("../SharpRail.Host.Abstractions/SharpRail.Host.Abstractions.csproj", "../SharpRail.UI/SharpRail.UI.csproj")));
        Crosses("SharpRail.Host.Abstractions -> SharpRail.UI", "SharpRail.Host.Abstractions.csproj", (ui, bare),
            ("src/SharpRail.Host.Abstractions/SharpRail.Host.Abstractions.csproj", Project("../SharpRail.UI/SharpRail.UI.csproj")));
        Crosses("SharpRail.Host.Core -> SharpRail.Host.Client", "SharpRail.Host.Core.csproj", (client, bare),
            (core + "SharpRail.Host.Core.csproj", Project("../SharpRail.Host.Client/SharpRail.Host.Client.csproj")));
        Crosses("SharpRail.Host.Core -> SharpRail.Host.Remote", "SharpRail.Host.Core.csproj", (remote, bare),
            (core + "SharpRail.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Reference Include=\"Remote\"><HintPath>../SharpRail.Host.Remote/bin/SharpRail.Host.Remote.dll</HintPath></Reference></ItemGroup></Project>"));
        Crosses("SharpRail.Host.Core -> SharpRail.UI", "SharpRail.Host.Core.csproj", (ui, bare),
            (core + "SharpRail.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Using Include=\"SharpRail.UI.State\" /></ItemGroup></Project>"));
        Crosses("SharpRail.Host.Core -> SharpRail.UI", "SharpRail.Host.Core.csproj", (ui, bare),
            (core + "SharpRail.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../SharpRail.UI/State/ProfileStore.cs\" Link=\"ProfileStore.cs\" /></ItemGroup></Project>"));
        Crosses("SharpRail.Host.Core -> SharpRail.UI", Path.Combine("SharpRail.Host.Core", "Plain.cs"), (ui, bare), (core + "Plain.cs", "using SharpRail.UI;\n"));
        Crosses("SharpRail.Host.Core -> SharpRail.Host.Remote", Path.Combine("SharpRail.Host.Core", "Alias.cs"), (remote, bare),
            (core + "Alias.cs", "using Server = SharpRail.Host.Remote.RemoteServer;\n"));
        Crosses("SharpRail.Host.Core -> SharpRail.Host.Client", Path.Combine("SharpRail.Host.Core", "Static.cs"), (client, bare),
            (core + "Static.cs", "using static SharpRail.Host.Client.LocalHostAdapter;\n"));
        Crosses("SharpRail.Host.Core -> SharpRail.UI", Path.Combine("SharpRail.Host.Core", "GlobalUsings.cs"), (ui, bare),
            (core + "GlobalUsings.cs", "global using SharpRail.UI.Docking;\n"));
        Crosses("SharpRail.Host.Core -> SharpRail.UI", Path.Combine("Nested", "Qualified.cs"), (ui, bare),
            (core + "Nested/Qualified.cs", "namespace SharpRail.Host.Core;\ninternal static class Qualified { static object Window() => new global::SharpRail.UI.WorkbenchWindow(); }\n"));
        Crosses("SharpRail.Host.Core -> SharpRail.Host.Client", Path.Combine("SharpRail.Host.Core", "Forward.cs"), (client, bare),
            (core + "Forward.cs", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(SharpRail.Host.Client.LocalHostAdapter))]\n"));
        Crosses("SharpRail.Host.Client -> SharpRail.Host.Core", Path.Combine("SharpRail.Host.Client", "View.axaml"), (client, bare),
            ("src/SharpRail.Host.Client/View.axaml", "<UserControl xmlns:core=\"clr-namespace:SharpRail.Host.Core;assembly=SharpRail.Host.Core\" />"));
        // A shared build file reaches every project, whatever conditions it carries.
        var shared = Scan(Tree((ui, bare), ("Directory.Build.props", "<Project><ItemGroup><ProjectReference Include=\"$(MSBuildThisFileDirectory)src/SharpRail.UI/SharpRail.UI.csproj\" /></ItemGroup></Project>")));
        Require(shared.Order(StringComparer.Ordinal).SequenceEqual(["SharpRail.Host.Abstractions -> SharpRail.UI: project item in Directory.Build.props",
            "SharpRail.Host.Core -> SharpRail.UI: project item in Directory.Build.props"]), "A shared build file's reference must count against every project: " + string.Join("; ", shared));
        var unruled = Scan(Tree(("src/SharpRail.Plugins/SharpRail.Plugins.csproj", bare)));
        Require(unruled.Count == 1 && unruled[0].StartsWith("SharpRail.Plugins: no boundary rule", StringComparison.Ordinal), "A project without a rule must be rejected.");

        // Build output, comments, literals and identifiers that merely contain a project's name are not dependencies.
        Clean((ui, bare), (core + "obj/Generated.cs", "using SharpRail.UI;\n"), (core + "bin/Release/Copy.cs", "using SharpRail.UI;\n"),
            (core + "Prose.cs", "// using SharpRail.UI;\n/* SharpRail.UI.State */\nnamespace SharpRail.Host.Core;\ninternal static class Prose\n{\n" +
                "    const string Plain = \"SharpRail.UI\", Escaped = \"\\\"SharpRail.UI\", Verbatim = @\"\"\"SharpRail.UI\\\";\n" +
                "    const string Raw = \"\"\"\n        \"SharpRail.UI\"\n        \"\"\";\n    const char Quote = '\"';\n    static int MySharpRail.UIx;\n}\n"),
            (core + "SharpRail.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><InternalsVisibleTo Include=\"SharpRail.Checks\" /></ItemGroup></Project>"),
            ("tests/SharpRail.Checks/SharpRail.Checks.csproj", Project("../../src/SharpRail.UI/SharpRail.UI.csproj")),
            ("tests/SharpRail.Checks/Probe.cs", "using SharpRail.UI;\nusing SharpRail.Host.Core;\n"));
        Console.WriteLine($"PASS boundary fixtures: {count} trees through the repository scan");
    }
}