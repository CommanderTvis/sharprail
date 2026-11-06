using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Isolation;
using SharpRail.Plugins.Isolation.Generator;
using SharpRail.Plugins.Isolation.UI;

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

    internal static async Task Run()
    {
        Tables();
        Console.WriteLine("PASS plugin crossing tables: pinned hashes, ids are FNV-1a of the documentation-comment ids, the UI-only surface is generated and the rest pending");
        Negatives();
        Console.WriteLine("PASS plugin crossing rules: SRI001 to SRI006 each fail a snippet that breaks them, including a stale pending entry; a clean snippet's runner proxies and trusted dispatchers compile without warnings");
        if (PluginChannelChecks.Runner() is not { } runner)
        {
            Console.WriteLine("SKIP plugin crossing round trip: these checks run self-contained, and the framework-dependent runner needs a shared .NET runtime");
            return;
        }
        await RoundTrip(runner);
        Console.WriteLine("PASS plugin crossing round trip: a UI-only plugin in the real runner registers a side tool, settings section, file viewer and header item, notifies, writes a preference and settings and invalidates through the generated proxy; the dispatcher replays each onto the context its handle names; factories and a viewer's resolver called back run in the runner; mirrored reads stay local; pending members throw NotSupportedException");
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

    /// <summary>
    /// The real runner's <c>probe ui-context</c> acts as a UI-only plugin through the generated proxy, on a context handle
    /// the host lends; the generated dispatcher replays what arrives onto a recording context.
    /// </summary>
    private static async Task RoundTrip(PluginChannelChecks.RunnerLaunch runner)
    {
        PluginJson.Register(IsolationUiJsonContext.Default);
        var context = DispatchProxy.Create<IPluginUIContext, UIRecorder>();
        var recorder = (UIRecorder)(object)context;
        IsolationPeer? peer = null;
        var callbacks = new PeerCallbacks(() => peer!);
        var options = new IsolationPeerOptions
        {
            Calls = (call, token) => call.TargetObject is IPluginUIContext target
                ? Dispatch(call, target, callbacks, token)
                : throw new IsoCallException(IsoError.Unknown, "Not a UI context."),
            OneWays = call =>
            {
                if (call.TargetObject is IPluginUIContext target)
                    PluginUIContextDispatcher.DispatchAsync(call.Member, call.Payload, target, callbacks, default).AsTask().GetAwaiter().GetResult();
            },
        };
        await using var session = await RunnerSession.StartAsync(PluginChannelChecks.Start(runner, RunnerProbe.UIContextMode, new()), options,
            new RunnerInit("probe-ui"), PluginChannelChecks.Patience, apiTableHash: PluginChannelChecks.TableHash);
        peer = session.Peer;
        var root = peer.Handles.Mint(context);
        var seen = (await peer.CallAsync(RunnerProbe.RunUIPlugin, root.ToString())).Payload.Split('|');
        string[] expected =
        [
            "id:probe-ui", "mirror:wide", "pending:IPluginUIContext.CopyText is not available to isolated plugins yet.",
            "pending:SideToolRegistration.Retarget is not available to isolated plugins yet.",
        ];
        Require(expected.All(seen.Contains), "The plugin in the runner saw: " + string.Join(" | ", seen));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!recorder.Calls.Contains("Invalidate()"))
        {
            Require(clock.Elapsed < PluginChannelChecks.Patience, "The one-way messages did not arrive: " + string.Join(" | ", recorder.Calls));
            await Task.Delay(20);
        }
        string[] calls = ["Notify(Info, Hello, null)", "Preference(layout).Set(wide)", "UpdateSettingsAsync({\"columns\":3})", "Invalidate()"];
        Require(calls.All(recorder.Calls.Contains) && !recorder.Calls.Any(call => call.Contains("Get", StringComparison.Ordinal) || call.Contains("CopyText", StringComparison.Ordinal)),
            "The context must see exactly what the plugin sent, and no mirrored or pending member: " + string.Join(" | ", recorder.Calls));

        var tool = (SideToolRegistration)recorder.Arguments["SideTool"]!;
        Require(tool is { Tool: "board", Retarget: null, RailDefault: null }, "The side tool must arrive as registered.");
        tool.Create("ws-1");
        var viewer = (FileViewerRegistration)recorder.Arguments["FileViewer"]!;
        Require(viewer.Matches!("a.todo") && !viewer.Matches("a.txt") && viewer.Open is null, "A viewer's Matches must run in the runner.");
        viewer.Create(new("ws-1", "notes/a.todo", 1, null));
        var section = (SettingsSectionRegistration)recorder.Arguments["SettingsSection"]!;
        Require(section is { Label: "Probe", Icon: "settings-3-line", Id: null }, "The settings section must arrive as registered.");
        section.Create();
        ((HeaderItemRegistration)recorder.Arguments["HeaderItem"]!).Create();
        string[] surfaces = ["surface:board:ws-1", "surface:viewer:notes/a.todo", "surface:settings", "surface:header"];
        Require(surfaces.SequenceEqual(callbacks.Surfaces), "Each factory must run in the runner with the host's arguments: " + string.Join(" | ", callbacks.Surfaces));
    }

    private static async ValueTask<IsoResult> Dispatch(IsoCall call, IPluginUIContext target, IIsolatedCallbacks callbacks, CancellationToken token) =>
        IsoResult.Of(await PluginUIContextDispatcher.DispatchAsync(call.Member, call.Payload, target, callbacks, token));

    /// <summary>Records each call on a UI context (or a preference it handed out) and the first argument by member name.</summary>
    public class UIRecorder : DispatchProxy
    {
        internal ConcurrentQueue<string> Calls { get; set; } = new();
        internal ConcurrentDictionary<string, object?> Arguments { get; set; } = new();
        internal string Prefix { get; set; } = "";

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = Prefix + method!.Name;
            Calls.Enqueue(name + "(" + string.Join(", ", (args ?? []).Select(Show)) + ")");
            Arguments[name] = args is [var first, ..] ? first : null;
            if (method.Name == "Preference")
            {
                var preference = Create<IPluginPreference, UIRecorder>();
                var inner = (UIRecorder)(object)preference;
                (inner.Calls, inner.Arguments, inner.Prefix) = (Calls, Arguments, $"Preference({args![0]}).");
                return preference;
            }
            return method.ReturnType == typeof(ValueTask) ? ValueTask.CompletedTask : null;
        }

        private static string Show(object? value) => value switch
        {
            null => "null",
            JsonNode node => node.ToJsonString(),
            string or Enum => value.ToString()!,
            _ => value.GetType().Name,
        };
    }

    /// <summary>Calls back into the runner; the checks' thread may wait, unlike the app's UI thread.</summary>
    private sealed class PeerCallbacks(Func<IsolationPeer> peer) : IIsolatedCallbacks
    {
        internal ConcurrentQueue<string> Surfaces { get; } = new();

        public async ValueTask<string> CallAsync(int member, long handle, string payload, CancellationToken cancellationToken) =>
            (await peer().CallAsync(member, payload, handle, cancellationToken: cancellationToken)).Payload;

        public void OneWay(int member, long handle, string payload) => peer().OneWay(member, payload, handle);

        public T Sync<T>(int member, long handle, string payload, string crossing, Func<string, T> read) => read(Call(member, handle, payload));

        public object Surface(int member, long handle, string payload)
        {
            Surfaces.Enqueue(Call(member, handle, payload));
            return null!;
        }

        private string Call(int member, long handle, string payload) =>
            peer().CallAsync(member, payload, handle).WaitAsync(PluginChannelChecks.Patience).GetAwaiter().GetResult().Payload;
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
        foreach (var mode in new[] { "runner", "trusted-ui", "trusted-host" })
        {
            var clean = Generate(Clean, Pending, mode);
            Require(clean.Diagnostics.IsEmpty, $"The clean snippet must pass every rule ({mode}): " + string.Join("; ", clean.Diagnostics));
            var errors = clean.Output.GetDiagnostics().Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden).ToArray();
            Require(errors.Length == 0, $"The clean snippet's generated code ({mode}) must compile without warnings: " + string.Join("; ", errors.Take(5)));
            Require(clean.Output.SyntaxTrees.Count() > 1, $"The generator must write code for the clean snippet ({mode}).");
        }

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

    private static (ImmutableArray<Diagnostic> Diagnostics, Compilation Output) Generate(string source, string pending, string mode = "runner")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll")
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SharpRail.Plugins.Api.PluginJson).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(IsolationMember).Assembly.Location));
        var compilation = CSharpCompilation.Create("CrossingSnippet", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create([new IsolationGenerator().AsSourceGenerator()], [new Text(IsolationGenerator.PendingFile, pending)],
            new CSharpParseOptions(LanguageVersion.Preview), new Options(mode));
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