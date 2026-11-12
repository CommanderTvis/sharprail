using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tereditor.Checks;

/// <summary>
/// Fails when a project reference or a source mention crosses the documented dependency direction.
/// A project is named by its root namespace, so one scan covers using directives, aliases, static and
/// global usings, qualified names and XAML namespaces alike.
/// </summary>
internal static class BoundaryChecks
{
    private const string Abstractions = "Tereditor.Host.Abstractions", Protocol = "Tereditor.Host.Protocol", Core = "Tereditor.Host.Core",
        Client = "Tereditor.Host.Client", Headless = "Tereditor.Host.Headless", Remote = "Tereditor.Host.Remote", Scintilla = "Tereditor.Scintilla", Ghostty = "Ghostty.Avalonia",
        UI = "Tereditor.UI", Checks = "Tereditor.Checks", Api = "Tereditor.Plugins.Api",
        Chat = "Tereditor.Plugins.Chat", ChatUi = "Tereditor.Plugins.Chat.UI", ApiHost = "Tereditor.Plugins.Api.Host", ApiUi = "Tereditor.Plugins.Api.UI", Kit = "Tereditor.Plugins.UI.Kit", Android = "Tereditor.Android", Browser = "Tereditor.Browser",
        Wire = "Tereditor.Wire", Isolation = "Tereditor.Plugins.Isolation", IsolationUi = "Tereditor.Plugins.Isolation.UI",
        IsolationGenerator = "Tereditor.Plugins.Isolation.Generator", Runner = "Tereditor.Plugins.Runner";

    /// <summary>Every project and what it may reach, directly or through a reference. A new project needs its own entry.</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [Abstractions] = [Api],
        [Wire] = [],
        [Protocol] = [Wire],
        // The host runs an external plugin's runner through the plugin channel (Security.SPEC.md).
        [Core] = [Abstractions, Api, ApiHost, Isolation, "Tereditor.Plugins.ClaudeCode", "Tereditor.Plugins.Codex"],
        // The remote adapter of an external plugin's UI session speaks the channel's surface protocol (Security.SPEC.md, UI halves).
        [Client] = [Abstractions, Protocol, Api, Isolation],
        // The headless host is a console program over the core: no web server, no UI.
        [Headless] = [Abstractions, Core, Api, ApiHost, "Tereditor.Plugins.ClaudeCode", "Tereditor.Plugins.Codex"],
        // The host serves an external plugin's UI session to remote clients over the surface call.
        [Remote] = [Abstractions, Protocol, Core, Api, ApiHost, Isolation, "Tereditor.Plugins.Tailscale"],
        [Scintilla] = [],
        [Ghostty] = [],
        // The UI draws an external plugin's surfaces (Security.SPEC.md, UI halves) from the channel's surface protocol.
        [UI] = [Abstractions, Core, Client, Remote, Scintilla, Ghostty, Api, ApiUi, Kit, Isolation, "Tereditor.Plugins.ClaudeCode", "Tereditor.Plugins.Codex"],
        // The Android client compiles the UI's sources and reaches what they do, except the serving half.
        [Android] = [Abstractions, Core, Client, Scintilla, Ghostty, UI, Api, ApiUi, Kit, Isolation, "Tereditor.Plugins.ClaudeCode", "Tereditor.Plugins.Codex"],
        [Browser] = [Abstractions, Core, Client, Scintilla, Ghostty, UI, Api, ApiUi, Kit, Isolation, "Tereditor.Plugins.ClaudeCode", "Tereditor.Plugins.Codex"],
        [Checks] = [Abstractions, Wire, Protocol, Core, Client, Headless, Remote, Scintilla, Ghostty, UI, Android, Api, ApiHost, ApiUi, Kit,
            "Tereditor.Plugins.ClaudeCode", "Tereditor.Plugins.Codex", "Tereditor.Plugins.Agent.UI", "Tereditor.PluginFixture.Host", "Tereditor.PluginFixture.UI", "Tereditor.PluginFixture.Outsider.UI", Isolation, IsolationUi, IsolationGenerator, Runner],
        [Api] = [],
        [ApiHost] = [Api],
        [ApiUi] = [Api],
        [Kit] = [Scintilla],
        // The chat contract and the chat view are shared by the plugins that offer a chat.
        [Chat] = [Api],
        [ChatUi] = [Api, ApiUi, Kit, Chat, "Tereditor.Plugins.Agent.UI"],
        ["Tereditor.Plugins.Agent.UI"] = [Api, Kit],
        ["Tereditor.PluginFixture.Host"] = [Api, ApiHost],
        ["Tereditor.PluginFixture.UI"] = [Api, ApiUi, Kit],
        // An external plugin with only a UI half, installed from disk by the checks, and its private dependency.
        ["Tereditor.PluginFixture.Outsider.UI"] = [Api, ApiUi, Kit, "Tereditor.PluginFixture.Outsider.Words"],
        ["Tereditor.PluginFixture.Outsider.Words"] = [],
        // The plugin channel and the process that runs an external plugin (src/Tereditor.Plugins.Api/Security.SPEC.md).
        [Isolation] = [Wire, Api, ApiHost],
        [IsolationUi] = [Isolation, Api, ApiUi],
        [IsolationGenerator] = [],
        // The runner shares the API, the kit and the chat contract with the plugins it loads.
        [Runner] = [Isolation, IsolationUi, Api, ApiHost, ApiUi, Kit, Chat, ChatUi]
    };

    private static readonly string[] Generated = ["bin", "obj", "artifacts", "node_modules"];

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static string? Checkout()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Tereditor.slnx"))) return directory.FullName;
        return null;
    }

    internal static void Run(string root)
    {
        Fixtures(Path.Combine(root, "boundaries"));
        if (Checkout() is not { } checkout) { Console.WriteLine("SKIP repository boundary scan: no source checkout"); return; }
        var violations = Scan(checkout);
        Require(violations.Count == 0, "Dependency boundaries crossed:\n" + string.Join('\n', violations));
        Console.WriteLine("PASS dependency boundaries: project references and source mentions follow the documented direction");
        var reflective = Directory.EnumerateFiles(Path.Combine(checkout, "src"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(checkout, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(file => File.ReadAllText(file) is var text && (text.Contains("x:CompileBindings=\"False\"", StringComparison.Ordinal) || text.Contains("ReflectionBinding", StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(checkout, file)).Order(StringComparer.Ordinal).ToArray();
        Require(reflective.Length == 0, "XAML binds by reflection, which NativeAOT cannot keep:\n" + string.Join('\n', reflective));
        Console.WriteLine("PASS XAML bindings: no reflection bindings in src");
    }

    /// <summary>The repository gate and the fixtures both enter here.</summary>
    internal static List<string> Scan(string root)
    {
        var projects = new[] { "src", "tests" }.Select(area => Path.Combine(root, area)).Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories).SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).Where(project => !Path.GetRelativePath(directory, project).Split(Path.DirectorySeparatorChar).Any(part => Generated.Contains(part) || part.StartsWith('.')))).Order(StringComparer.Ordinal).ToArray();
        var rules = Allowed.ToDictionary();
        foreach (var plugin in new[] { "Acp", "SpecDialect", "Blueprint", "ClaudeCode", "Discord", "BranchGraph", "Review", "Processes", "Visualize", "Codex", "Omp", "Pi", "Providers", "AirGateway", "AirContext", "Tailscale", "Epub", "CodeIntelligence", "EnvMise", "EnvDirenv", "EnvPython", "EnvJavaScript", "EnvJvm", "Env", "GitHub" })
        {
            var contract = "Tereditor.Plugins." + plugin;
            rules[contract] = [Api];
            rules[contract + ".Host"] = [Api, ApiHost, contract, "Tereditor.Plugins.SpecDialect"];
            rules[contract + ".UI"] = [Api, ApiUi, Kit, Scintilla, contract, "Tereditor.Plugins.SpecDialect", "Tereditor.Plugins.Agent.UI"];
            rules[Core] = [.. rules[Core], contract, contract + ".Host"];
            rules[UI] = [.. rules[UI], contract, contract + ".UI"];
            rules[Android] = [.. rules[Android], contract, contract + ".UI"];
            rules[Browser] = [.. rules[Browser], contract, contract + ".UI"];
            rules[Checks] = [.. rules[Checks], contract, contract + ".Host", contract + ".UI"];
            // A browser tab runs Pi's host in the page (TabPi), so the tab client names its host module.
            if (plugin is "Pi") rules[Browser] = [.. rules[Browser], contract + ".Host"];
            if (plugin is "ClaudeCode" or "Pi" or "Acp")
            {
                rules[contract] = [.. rules[contract], Chat];
                rules[contract + ".Host"] = [.. rules[contract + ".Host"], Chat];
                rules[contract + ".UI"] = [.. rules[contract + ".UI"], Chat, ChatUi];
                rules[Core] = [.. rules[Core], Chat];
                rules[UI] = [.. rules[UI], Chat, ChatUi];
                rules[Android] = [.. rules[Android], Chat, ChatUi];
                rules[Browser] = [.. rules[Browser], Chat, ChatUi];
                rules[Checks] = [.. rules[Checks], Chat, ChatUi];
            }
        }
        // Language server plugins (H21) are one host-only project each, referenced by the host core only.
        foreach (var plugin in projects.Select(project => Path.GetFileNameWithoutExtension(project)).Where(name => name.StartsWith("Tereditor.Plugins.Lsp.", StringComparison.Ordinal)))
        {
            rules[plugin] = [Api, ApiHost];
            rules[Core] = [.. rules[Core], plugin];
            rules[Checks] = [.. rules[Checks], plugin];
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
            foreach (var (path, text) in files.Append(("src/Tereditor.Host.Abstractions/Tereditor.Host.Abstractions.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"))
                .Append(("src/Tereditor.Host.Core/Tereditor.Host.Core.csproj", Project("../Tereditor.Host.Abstractions/Tereditor.Host.Abstractions.csproj"))))
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
        const string core = "src/Tereditor.Host.Core/", ui = "src/Tereditor.UI/Tereditor.UI.csproj", client = "src/Tereditor.Host.Client/Tereditor.Host.Client.csproj",
            remote = "src/Tereditor.Host.Remote/Tereditor.Host.Remote.csproj", bare = "<Project Sdk=\"Microsoft.NET.Sdk\" />";

        Clean((core + "Host.cs", "using Tereditor.Host.Abstractions;\nnamespace Tereditor.Host.Core;\ninternal sealed class Host : IWorkspaceHost;\n"));
        Crosses("Tereditor.Host.Core -> Tereditor.UI", "Tereditor.Host.Core.csproj", (ui, bare),
            (core + "Tereditor.Host.Core.csproj", Project("../Tereditor.Host.Abstractions/Tereditor.Host.Abstractions.csproj", "../Tereditor.UI/Tereditor.UI.csproj")));
        Crosses("Tereditor.Host.Abstractions -> Tereditor.UI", "Tereditor.Host.Abstractions.csproj", (ui, bare),
            ("src/Tereditor.Host.Abstractions/Tereditor.Host.Abstractions.csproj", Project("../Tereditor.UI/Tereditor.UI.csproj")));
        Crosses("Tereditor.Host.Core -> Tereditor.Host.Client", "Tereditor.Host.Core.csproj", (client, bare),
            (core + "Tereditor.Host.Core.csproj", Project("../Tereditor.Host.Client/Tereditor.Host.Client.csproj")));
        Crosses("Tereditor.Host.Core -> Tereditor.Host.Remote", "Tereditor.Host.Core.csproj", (remote, bare),
            (core + "Tereditor.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Reference Include=\"Remote\"><HintPath>../Tereditor.Host.Remote/bin/Tereditor.Host.Remote.dll</HintPath></Reference></ItemGroup></Project>"));
        Crosses("Tereditor.Host.Core -> Tereditor.UI", "Tereditor.Host.Core.csproj", (ui, bare),
            (core + "Tereditor.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Using Include=\"Tereditor.UI.State\" /></ItemGroup></Project>"));
        Crosses("Tereditor.Host.Core -> Tereditor.UI", "Tereditor.Host.Core.csproj", (ui, bare),
            (core + "Tereditor.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../Tereditor.UI/State/ProfileStore.cs\" Link=\"ProfileStore.cs\" /></ItemGroup></Project>"));
        Crosses("Tereditor.Host.Core -> Tereditor.UI", Path.Combine("Tereditor.Host.Core", "Plain.cs"), (ui, bare), (core + "Plain.cs", "using Tereditor.UI;\n"));
        Crosses("Tereditor.Host.Core -> Tereditor.Host.Remote", Path.Combine("Tereditor.Host.Core", "Alias.cs"), (remote, bare),
            (core + "Alias.cs", "using Server = Tereditor.Host.Remote.RemoteServer;\n"));
        Crosses("Tereditor.Host.Core -> Tereditor.Host.Client", Path.Combine("Tereditor.Host.Core", "Static.cs"), (client, bare),
            (core + "Static.cs", "using static Tereditor.Host.Client.LocalHostAdapter;\n"));
        Crosses("Tereditor.Host.Core -> Tereditor.UI", Path.Combine("Tereditor.Host.Core", "GlobalUsings.cs"), (ui, bare),
            (core + "GlobalUsings.cs", "global using Tereditor.UI.Docking;\n"));
        Crosses("Tereditor.Host.Core -> Tereditor.UI", Path.Combine("Nested", "Qualified.cs"), (ui, bare),
            (core + "Nested/Qualified.cs", "namespace Tereditor.Host.Core;\ninternal static class Qualified { static object Window() => new global::Tereditor.UI.WorkbenchWindow(); }\n"));
        Crosses("Tereditor.Host.Core -> Tereditor.Host.Client", Path.Combine("Tereditor.Host.Core", "Forward.cs"), (client, bare),
            (core + "Forward.cs", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Tereditor.Host.Client.LocalHostAdapter))]\n"));
        Crosses("Tereditor.Host.Client -> Tereditor.Host.Core", Path.Combine("Tereditor.Host.Client", "View.axaml"), (client, bare),
            ("src/Tereditor.Host.Client/View.axaml", "<UserControl xmlns:core=\"clr-namespace:Tereditor.Host.Core;assembly=Tereditor.Host.Core\" />"));
        // A shared build file reaches every project, whatever conditions it carries.
        var shared = Scan(Tree((ui, bare), ("Directory.Build.props", "<Project><ItemGroup><ProjectReference Include=\"$(MSBuildThisFileDirectory)src/Tereditor.UI/Tereditor.UI.csproj\" /></ItemGroup></Project>")));
        Require(shared.Order(StringComparer.Ordinal).SequenceEqual(["Tereditor.Host.Abstractions -> Tereditor.UI: project item in Directory.Build.props",
            "Tereditor.Host.Core -> Tereditor.UI: project item in Directory.Build.props"]), "A shared build file's reference must count against every project: " + string.Join("; ", shared));
        var unruled = Scan(Tree(("src/Tereditor.Plugins/Tereditor.Plugins.csproj", bare)));
        Require(unruled.Count == 1 && unruled[0].StartsWith("Tereditor.Plugins: no boundary rule", StringComparison.Ordinal), "A project without a rule must be rejected.");

        // Build output, comments, literals and identifiers that merely contain a project's name are not dependencies.
        Clean((ui, bare), (core + "obj/Generated.cs", "using Tereditor.UI;\n"), (core + "bin/Release/Copy.cs", "using Tereditor.UI;\n"),
            (core + "Prose.cs", "// using Tereditor.UI;\n/* Tereditor.UI.State */\nnamespace Tereditor.Host.Core;\ninternal static class Prose\n{\n" +
                "    const string Plain = \"Tereditor.UI\", Escaped = \"\\\"Tereditor.UI\", Verbatim = @\"\"\"Tereditor.UI\\\";\n" +
                "    const string Raw = \"\"\"\n        \"Tereditor.UI\"\n        \"\"\";\n    const char Quote = '\"';\n    static int MyTereditor.UIx;\n}\n"),
            (core + "Tereditor.Host.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><InternalsVisibleTo Include=\"Tereditor.Checks\" /></ItemGroup></Project>"),
            ("tests/Tereditor.Checks/Tereditor.Checks.csproj", Project("../../src/Tereditor.UI/Tereditor.UI.csproj")),
            ("tests/Tereditor.Checks/Probe.cs", "using Tereditor.UI;\nusing Tereditor.Host.Core;\n"));
        Console.WriteLine($"PASS boundary fixtures: {count} trees through the repository scan");
    }
}