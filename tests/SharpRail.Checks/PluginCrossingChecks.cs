using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.Isolation.Generator;

using HostMembers = SharpRail.Plugins.Isolation.PluginHostContextMembers;
using UIMembers = SharpRail.Plugins.Isolation.UI.PluginUIContextMembers;

namespace SharpRail.Checks;

/// <summary>
/// <c>--plugin-crossing</c>: the generated member tables and the crossing rules (Security.SPEC.md, Calls across the
/// boundary). The tables' hashes are pinned, so a change to what crosses is a deliberate edit here; each rule SRI001 to
/// SRI006 fails an in-memory snippet that breaks it, and a clean snippet generates code that compiles.
/// </summary>
internal static class PluginCrossingChecks
{
    // The member tables' hashes. A change to the plugin API's crossing surface (a member, its attributes, the pending
    // list) changes them; update the pin in the same change, after checking the change is meant.
    private const string HostTable = "7f57865a979bfa34";
    private const string UITable = "2e8bb00fcd489b30";

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        Tables();
        Console.WriteLine("PASS plugin crossing tables: pinned hashes, ids are FNV-1a of the documentation-comment ids, the UI-only surface is generated and the rest pending");
        Negatives();
        Console.WriteLine("PASS plugin crossing rules: SRI001 to SRI006 each fail a snippet that breaks them, including a stale pending entry; a clean snippet generates code that compiles");
    }

    private static void Tables()
    {
        Require(HostMembers.Hash == HostTable && UIMembers.Hash == UITable,
            $"The isolation member tables changed: host {HostMembers.Hash} (pinned {HostTable}), UI {UIMembers.Hash} (pinned {UITable}). Update the pins in PluginCrossingChecks if the change is meant.");
        foreach (var table in new[] { HostMembers.Table, UIMembers.Table })
        {
            Require(table.Select(member => member.Id).Distinct().Count() == table.Count, "Member ids must be unique within a table.");
            foreach (var member in table)
                Require(member.Id == IsoMember.Id(member.DocId), $"{member.DocId}: the generated id must be FNV-1a 32 of the documentation-comment id.");
        }
        Require(HostMembers.Table.Count > 0 && HostMembers.Table.All(member => member.Pending),
            "Every host context member is pending until the host half runs isolated.");
        string[] generated =
        [
            "P:SharpRail.Plugins.Api.UI.IPluginUIContext.Id", "P:SharpRail.Plugins.Api.UI.IPluginUIContext.Log",
            "M:SharpRail.Plugins.Api.UI.IPluginUIContext.Settings``1", "M:SharpRail.Plugins.Api.UI.IPluginUIContext.UpdateSettingsAsync``1(``0)",
            "M:SharpRail.Plugins.Api.UI.IPluginUIContext.SideTool(SharpRail.Plugins.Api.UI.SideToolRegistration)",
            "M:SharpRail.Plugins.Api.UI.IPluginUIContext.FileViewer(SharpRail.Plugins.Api.UI.FileViewerRegistration)",
            "M:SharpRail.Plugins.Api.UI.IPluginUIContext.Notify(SharpRail.Plugins.Api.UI.PluginNotificationKind,System.String,System.String)",
            "M:SharpRail.Plugins.Api.UI.IPluginPreference.Set(System.String)", "P:SharpRail.Plugins.Api.UI.FileViewerRegistration.Matches",
        ];
        foreach (var docId in generated)
            Require(UIMembers.Table.SingleOrDefault(member => member.DocId == docId) is { Pending: false }, docId + " must be generated, not pending.");
        var copy = UIMembers.Table.Single(member => member.DocId == "M:SharpRail.Plugins.Api.UI.IPluginUIContext.CopyText(System.String,System.String)");
        Require(copy is { Pending: true, Permission: "clipboard.write" }, "CopyText is pending and needs clipboard.write.");
        Require(UIMembers.Table.Single(member => member.DocId == "P:SharpRail.Plugins.Api.UI.FileViewerRegistration.Matches").Crossing == "Resolver",
            "A viewer's Matches is a resolver.");
    }

    // A minimal API: both roots, so the pending list is checked against both, and members that pass every rule.
    private const string Clean = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using SharpRail.Plugins.Api;
        namespace SharpRail.Plugins.Api.Host
        {
            public interface IPluginHostContext
            {
                [PluginAmbient] void Ping();
                [PluginAmbient] void Later();
            }
        }
        namespace SharpRail.Plugins.Api.UI
        {
            public sealed record Choice(string Label)
            {
                [PluginCrossing(PluginCrossing.Resolver)] public Func<string, bool>? Matches { get; init; }
            }
            public interface IPluginUIContext
            {
                [PluginAmbient, PluginCrossing(PluginCrossing.LocalInRunner)] string Id { get; }
                [PluginAmbient] void Notify(string text, int count);
                [PluginAmbient] void Offer(Choice choice);
                [PluginAmbient] ValueTask<string> AskAsync(string question, CancellationToken cancellationToken = default);
                [PluginPermission("clipboard.write")] void CopyText(string text);
                /*DEFECT*/
            }
        }
        """;

    private const string Pending = "M:SharpRail.Plugins.Api.Host.IPluginHostContext.Later\n";

    private static void Negatives()
    {
        var clean = Generate(Clean, Pending);
        Require(clean.Diagnostics.IsEmpty, "The clean snippet must pass every rule: " + string.Join("; ", clean.Diagnostics));
        var errors = clean.Output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Require(errors.Length == 0, "The clean snippet's generated code must compile: " + string.Join("; ", errors.Take(5)));

        Fails("SRI001", "void Unclassified();");
        Fails("SRI001", "[PluginAmbient] int Count();");
        Fails("SRI001", "[PluginAmbient, PluginCrossing(PluginCrossing.Live)] int Count();");
        Fails("SRI001", "", Pending + "M:SharpRail.Plugins.Api.UI.IPluginUIContext.Gone\n");
        Fails("SRI001", "[PluginAmbient] void Pick(Func<string, bool> filter);");
        Fails("SRI002", "[PluginAmbient] void Load(Type type);");
        Fails("SRI002", "[PluginAmbient] void Swap(ref int value);");
        Fails("SRI003", "[PluginAmbient] void Show(Uri place);");
        Fails("SRI004", "[PluginAmbient] void M8bdca(); [PluginAmbient] void Md5579();");
        Fails("SRI005", "[PluginAmbient, PluginCrossing(PluginCrossing.Mirrored)] string? Title() => null; } sealed class Proxy : IPluginUIContext { public string Id => \"\"; public void Notify(string text, int count) { } public void Offer(Choice choice) { } public ValueTask<string> AskAsync(string question, CancellationToken cancellationToken) => default; public void CopyText(string text) { }");
        Fails("SRI006", "[PluginAmbient] ValueTask<(int, string Name)> PairAsync();");
    }

    private static void Fails(string rule, string defect, string pending = Pending)
    {
        var result = Generate(Clean.Replace("/*DEFECT*/", defect), pending);
        Require(result.Diagnostics.Any(diagnostic => diagnostic.Id == rule),
            $"{rule} must fail `{defect}`; the generator reported: {string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Id + " " + diagnostic.GetMessage()))}");
    }

    private static (ImmutableArray<Diagnostic> Diagnostics, Compilation Output) Generate(string source, string pending)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll")
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SharpRail.Plugins.Api.PluginJson).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(IsolationMember).Assembly.Location));
        var compilation = CSharpCompilation.Create("CrossingSnippet", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create([new IsolationGenerator().AsSourceGenerator()], [new Text(IsolationGenerator.PendingFile, pending)],
            new CSharpParseOptions(LanguageVersion.Preview), new Options("runner"));
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (diagnostics, output);
    }

    private sealed class Text(string path, string content) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }

    private sealed class Options(string mode) : AnalyzerConfigOptionsProvider
    {
        private readonly Values global = new(new Dictionary<string, string>
        {
            ["build_property.SharpRailIsolation"] = mode,
            ["build_property.RootNamespace"] = "CrossingSnippet",
        });

        public override AnalyzerConfigOptions GlobalOptions => global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Values.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Values.Empty;
    }

    private sealed class Values(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static readonly Values Empty = new([]);
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}