using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Tereditor.Plugins.Isolation.Generator;

/// <summary>
/// Generates the isolated runtime's member tables, proxies and dispatchers from the plugin API's symbols and its crossing
/// attributes, and fails the build on what cannot cross (SRI001 to SRI006). A project opts in with the MSBuild property
/// <c>TereditorIsolation</c>: <c>runner</c> (proxies the plugin sees, for both contexts), <c>trusted-host</c> (the host
/// context's table and dispatcher) or <c>trusted-ui</c> (the UI context's). <c>isolation-pending.txt</c>, an additional
/// file, lists the members the isolated runtime does not implement yet.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class IsolationGenerator : IIncrementalGenerator
{
    public const string HostContext = "Tereditor.Plugins.Api.Host.IPluginHostContext";
    public const string UIContext = "Tereditor.Plugins.Api.UI.IPluginUIContext";
    public const string PendingFile = "isolation-pending.txt";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var options = context.AnalyzerConfigOptionsProvider.Select((provider, _) =>
        {
            provider.GlobalOptions.TryGetValue("build_property.TereditorIsolation", out var mode);
            provider.GlobalOptions.TryGetValue("build_property.RootNamespace", out var space);
            return (Mode: mode ?? "", Namespace: string.IsNullOrEmpty(space) ? "Tereditor.Plugins.Isolation" : space!);
        });
        var pending = context.AdditionalTextsProvider.Where(text => System.IO.Path.GetFileName(text.Path) == PendingFile).Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(options).Combine(pending),
            (output, input) => Execute(output, input.Left.Left, input.Left.Right.Mode, input.Left.Right.Namespace, input.Right));
    }

    private static void Execute(SourceProductionContext output, Compilation compilation, string mode, string space, ImmutableArray<AdditionalText> pendingFiles)
    {
        string[] roots = mode switch
        {
            "runner" => [HostContext, UIContext],
            "trusted-host" => [HostContext],
            "trusted-ui" => [UIContext],
            _ => [],
        };
        if (roots.Length == 0) return;

        var pending = new PendingList(pendingFiles.FirstOrDefault());
        var builder = new ModelBuilder(compilation, pending);
        var tables = new List<Table>();
        foreach (var name in roots)
        {
            if (compilation.GetTypeByMetadataName(name) is not { } root) continue;
            var table = builder.Build(root);
            tables.Add(table);
            foreach (var diagnostic in table.Diagnostics) output.ReportDiagnostic(diagnostic);
            output.AddSource(Short(root) + "Members.g.cs", SourceText.From(Emitter.Table(table, space), Encoding.UTF8));
        }
        if (tables.Count == 2)
            foreach (var diagnostic in ModelBuilder.Stale(pending, tables)) output.ReportDiagnostic(diagnostic);
        foreach (var diagnostic in DefaultMembers(compilation, tables)) output.ReportDiagnostic(diagnostic);
        if (tables.Any(table => table.Diagnostics.Count > 0)) return;

        foreach (var table in tables)
        {
            var emitter = new Emitter(table, space);
            foreach (var (hint, source) in mode == "runner" ? emitter.Runner() : emitter.Trusted())
                output.AddSource(hint, SourceText.From(source, Encoding.UTF8));
            foreach (var diagnostic in emitter.Diagnostics) output.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>The root context's name without its leading I: <c>PluginUIContext</c>.</summary>
    public static string Short(INamedTypeSymbol type) => type.TypeKind == TypeKind.Interface && type.Name.StartsWith("I") ? type.Name.Substring(1) : type.Name;

    /// <summary>
    /// SRI005 for hand-written code: a class in this compilation that implements a plugin-facing interface must implement
    /// every default member itself, so it never answers with the interface's silent default.
    /// </summary>
    private static IEnumerable<Diagnostic> DefaultMembers(Compilation compilation, List<Table> tables)
    {
        var interfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var table in tables)
        {
            interfaces.Add(table.Root);
            foreach (var (handle, _) in table.Handles) interfaces.Add(handle);
        }
        if (interfaces.Count == 0) yield break;
        foreach (var type in Types(compilation.Assembly.GlobalNamespace))
        {
            if (type.TypeKind != TypeKind.Class) continue;
            foreach (var implemented in type.AllInterfaces.Where(interfaces.Contains))
            {
                foreach (var member in implemented.GetMembers().Where(member => member is IMethodSymbol { IsAbstract: false, IsStatic: false, MethodKind: MethodKind.Ordinary } or IPropertySymbol { IsAbstract: false, IsStatic: false }))
                {
                    var implementation = type.FindImplementationForInterfaceMember(member);
                    if (implementation is not null && implementation.ContainingType.TypeKind != TypeKind.Interface) continue;
                    yield return Diagnostic.Create(Rules.DefaultMember, type.Locations.FirstOrDefault() ?? Location.None,
                        $"{type.Name} inherits the default {member.ToDisplayString()}; implement it explicitly so an isolated plugin never gets the silent default");
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol space)
    {
        foreach (var type in space.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers()) yield return nested;
        }
        foreach (var inner in space.GetNamespaceMembers())
            foreach (var type in Types(inner))
                yield return type;
    }
}