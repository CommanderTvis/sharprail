using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Tereditor.Plugins.Isolation.Generator;

/// <summary>Where a member sits in the surface a root context reaches.</summary>
internal enum MemberRole
{
    /// <summary>A member of the root context.</summary>
    Root,
    /// <summary>A member of an interface a root member returns, reached through that member (the accessor).</summary>
    Handle,
    /// <summary>A delegate property of a record the plugin passes in.</summary>
    CallbackProperty,
    /// <summary>A delegate parameter of a root or handle member.</summary>
    CallbackParameter,
}

/// <summary>One entry of a member table.</summary>
internal sealed class Member
{
    public Member(ISymbol symbol, string docId, MemberRole role, INamedTypeSymbol owner)
    {
        Symbol = symbol;
        DocId = docId;
        Role = role;
        Owner = owner;
        Id = Fnv(docId);
    }

    public ISymbol Symbol { get; }
    public string DocId { get; }
    public int Id { get; }
    public MemberRole Role { get; }
    public INamedTypeSymbol Owner { get; }
    public string Permission { get; set; } = "";
    public string Condition { get; set; } = "";
    public bool Ambient { get; set; }
    public string? Crossing { get; set; }
    public string? Shape { get; set; }
    public bool Pending { get; set; }
    /// <summary>For a handle member, the root member whose result it is a member of.</summary>
    public Member? Accessor { get; set; }
    /// <summary>For a callback parameter, the member it is a parameter of.</summary>
    public Member? Parent { get; set; }
    /// <summary>The constant's name in the generated table.</summary>
    public string Name { get; set; } = "";

    public IMethodSymbol? Method => Symbol as IMethodSymbol;
    public IPropertySymbol? Property => Symbol as IPropertySymbol;

    /// <summary>For a callback, the delegate type.</summary>
    public INamedTypeSymbol? Delegate => Symbol switch
    {
        IParameterSymbol parameter => parameter.Type as INamedTypeSymbol,
        IPropertySymbol property when Role == MemberRole.CallbackProperty => property.Type as INamedTypeSymbol,
        _ => null,
    };

    public bool IsCallback => Role is MemberRole.CallbackProperty or MemberRole.CallbackParameter;

    public static int Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes(text)) hash = unchecked((hash ^ value) * 16777619u);
        return unchecked((int)hash);
    }
}

/// <summary>The member table of one root context, with what the rules found.</summary>
internal sealed class Table
{
    public Table(INamedTypeSymbol root) => Root = root;

    public INamedTypeSymbol Root { get; }
    public List<Member> Members { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public string Hash { get; set; } = "";

    /// <summary>The interfaces reached through an accessor, with their accessor.</summary>
    public List<(INamedTypeSymbol Interface, Member Accessor)> Handles { get; } = [];
}

/// <summary>The pending list: members the isolated runtime does not implement yet.</summary>
internal sealed class PendingList
{
    public PendingList(AdditionalText? file)
    {
        if (file?.GetText() is not { } text) return;
        File = file;
        foreach (var line in text.Lines)
        {
            var entry = line.ToString().Trim();
            if (entry.Length == 0 || entry.StartsWith("#")) continue;
            Entries[entry] = line.Span;
            Text = text;
        }
    }

    public AdditionalText? File { get; }
    public SourceText? Text { get; }
    public Dictionary<string, TextSpan> Entries { get; } = [];

    public bool Contains(string docId) => Entries.ContainsKey(docId);

    public Location LocationOf(string entry)
    {
        if (File is null || Text is null || !Entries.TryGetValue(entry, out var span)) return Location.None;
        return Location.Create(File.Path, span, Text.Lines.GetLinePositionSpan(span));
    }
}

/// <summary>Builds a root context's member table and applies SRI001 to SRI004 and SRI006 to it.</summary>
internal sealed class ModelBuilder
{
    public const string ApiNamespace = "Tereditor.Plugins.Api";
    private const string CrossingAttribute = "Tereditor.Plugins.Api.PluginCrossingAttribute";
    private const string AmbientAttribute = "Tereditor.Plugins.Api.PluginAmbientAttribute";
    private const string PermissionAttribute = "Tereditor.Plugins.Api.PluginPermissionAttribute";
    private static readonly string[] JsonContexts =
    [
        "Tereditor.Plugins.Api.PluginApiJson",
        "Tereditor.Plugins.Isolation.IsolationJsonContext",
        "Tereditor.Plugins.Isolation.UI.IsolationUiJsonContext",
    ];

    private readonly Compilation compilation;
    private readonly PendingList pending;
    private readonly HashSet<ITypeSymbol> json = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, Member> members = [];
    private readonly Queue<Member> queue = new();
    private Table table = null!;

    public ModelBuilder(Compilation compilation, PendingList pending)
    {
        this.compilation = compilation;
        this.pending = pending;
        foreach (var name in JsonContexts)
        {
            if (compilation.GetTypeByMetadataName(name) is not { } context) continue;
            foreach (var attribute in context.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() != "System.Text.Json.Serialization.JsonSerializableAttribute") continue;
                if (attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol type) json.Add(Plain(type));
            }
        }
    }

    public Table Build(INamedTypeSymbol root)
    {
        table = new Table(root);
        members.Clear();
        foreach (var symbol in Surface(root)) Add(symbol, MemberRole.Root, root, null, null);
        while (queue.Count > 0) Visit(queue.Dequeue());

        table.Members.AddRange(members.Values.OrderBy(member => member.DocId, System.StringComparer.Ordinal));
        Name(table.Members);
        foreach (var group in table.Members.GroupBy(member => member.Id).Where(group => group.Count() > 1))
            Report(Rules.Collision, Location.None, $"{string.Join(" and ", group.Select(member => member.DocId))} share the member id {group.Key:x8}; rename one");

        var lines = table.Members.Select(member =>
            $"{member.DocId}|{member.Id:x8}|{member.Permission}|{member.Condition}|{(member.Ambient ? "ambient" : "")}|{member.Crossing}|{(member.Pending ? "pending" : "")}");
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
            table.Hash = string.Concat(bytes.Take(8).Select(value => value.ToString("x2")));
        }
        return table;
    }

    /// <summary>Reports pending entries that name no member any table reached.</summary>
    public static IEnumerable<Diagnostic> Stale(PendingList pending, IEnumerable<Table> tables)
    {
        var known = new HashSet<string>(tables.SelectMany(table => table.Members).Select(member => member.DocId));
        foreach (var entry in pending.Entries.Keys)
        {
            if (known.Contains(entry)) continue;
            yield return Diagnostic.Create(Rules.Unclassified, pending.LocationOf(entry),
                $"isolation-pending.txt names {entry}, which no non-pending member reaches; remove the line");
        }
    }

    /// <summary>The members of an interface or record a plugin sees, in declaration order.</summary>
    public static IEnumerable<ISymbol> Surface(INamedTypeSymbol type) =>
        type.GetMembers().Where(symbol => symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary, IsStatic: false } or IPropertySymbol { IsStatic: false }
            && symbol.DeclaredAccessibility == Accessibility.Public);

    public static string DocId(ISymbol symbol) => symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString();

    private Member Add(ISymbol symbol, MemberRole role, INamedTypeSymbol owner, Member? accessor, Member? parent)
    {
        var docId = symbol is IParameterSymbol parameter ? parent!.DocId + "#" + parameter.Name : DocId(symbol);
        if (members.TryGetValue(docId, out var existing)) return existing;
        var member = new Member(symbol, docId, role, owner) { Accessor = accessor, Parent = parent, Pending = pending.Contains(docId) };
        Classify(member);
        members[docId] = member;
        queue.Enqueue(member);
        return member;
    }

    private void Classify(Member member)
    {
        foreach (var attribute in member.Symbol.GetAttributes().Concat(member.Symbol is IParameterSymbol ? [] : member.Owner.GetAttributes()))
        {
            switch (attribute.AttributeClass?.ToDisplayString())
            {
                case CrossingAttribute when member.Crossing is null:
                    var value = attribute.ConstructorArguments.FirstOrDefault();
                    member.Crossing = value.Type is INamedTypeSymbol enumType
                        ? enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(field => Equals(field.ConstantValue, value.Value))?.Name
                        : null;
                    member.Shape = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "IsolatedShape").Value.Value as string;
                    break;
                case AmbientAttribute:
                    member.Ambient = true;
                    break;
                case PermissionAttribute when member.Permission.Length == 0:
                    member.Permission = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "";
                    member.Condition = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "Condition").Value.Value as string ?? "";
                    break;
            }
        }
    }

    private void Visit(Member member)
    {
        if (member.Role is MemberRole.Root || !member.Pending) CheckSignature(member);
        if (member.IsCallback)
        {
            if (!member.Pending) VisitCallback(member);
            return;
        }

        var (returnType, isAsync, isVoid) = Returns(member.Symbol);
        var where = member.Symbol.ToDisplayString();
        var classified = member.Role == MemberRole.Root || !member.Pending;
        if (classified && !member.Ambient && member.Permission.Length == 0)
            Report(Rules.Unclassified, Location.None, $"{where} has no [PluginAmbient] or [PluginPermission]");
        if (classified && !isAsync && !isVoid)
        {
            if (member.Crossing is null && !IsDisposable(returnType))
                Report(Rules.Unclassified, Location.None, $"{where} is synchronous: classify it [PluginCrossing(Mirrored | Blocking | LocalInRunner | Unavailable)]");
            else if (member.Crossing is "Resolver" or "Live" or "UnderLock")
                Report(Rules.Unclassified, Location.None, $"{where} is called by the plugin: {member.Crossing} is for callbacks; use Mirrored, Blocking or LocalInRunner");
        }
        if (member.Pending) return;

        if (member.Crossing == "LocalInRunner")
        {
            if (Unwrap(returnType) is INamedTypeSymbol { TypeKind: TypeKind.Interface } handle && IsApi(handle) && !IsLocalType(handle))
            {
                if (member.Role == MemberRole.Handle)
                    Report(Rules.Uncrossable, Location.None, $"{where} returns {handle.Name} from a handle; the generator reaches handles one level deep");
                else
                {
                    table.Handles.Add((handle, member));
                    foreach (var symbol in Surface(handle)) Add(symbol, MemberRole.Handle, handle, member, null);
                }
            }
            return;
        }
        if (member.Crossing is "Blocking" or "Unavailable")
        {
            Report(Rules.Uncrossable, Location.None, $"{where} is {member.Crossing}, which the generator has no shape for yet; list it in isolation-pending.txt");
            return;
        }

        var parameters = member.Symbol is IMethodSymbol method ? method.Parameters : ImmutableArray<IParameterSymbol>.Empty;
        foreach (var parameter in parameters)
        {
            if (Is(parameter.Type, "System.Threading.CancellationToken")) continue;
            if (parameter.Type.TypeKind == TypeKind.Delegate)
            {
                Add(parameter, MemberRole.CallbackParameter, member.Owner, null, member);
                continue;
            }
            Argument(member, parameter.Type, where);
        }
        if (isVoid || IsResultless(returnType)) return;
        if (IsDisposable(returnType) || Unwrap(returnType) is INamedTypeSymbol { TypeKind: TypeKind.Interface } r && IsApi(r))
        {
            Report(Rules.Uncrossable, Location.None, $"{where} returns a handle, which the generator has no shape for yet; list it in isolation-pending.txt");
            return;
        }
        Json(Unwrap(returnType), where);
    }

    /// <summary>A plugin-passed value: a by-value type, or a record whose delegate properties become callbacks.</summary>
    private void Argument(Member member, ITypeSymbol type, string where)
    {
        if (type is ITypeParameterSymbol) return;
        if (type is INamedTypeSymbol named && IsApi(named) && named.TypeKind == TypeKind.Class && HasCallbacks(named))
        {
            foreach (var property in Surface(named).OfType<IPropertySymbol>())
            {
                if (property.Type.TypeKind == TypeKind.Delegate) Add(property, MemberRole.CallbackProperty, named, null, member);
                else if (property.Type is INamedTypeSymbol { TypeKind: TypeKind.Interface } inner && IsApi(inner))
                    Report(Rules.Uncrossable, Location.None, $"{property.ToDisplayString()} lends a handle inside a record, which the generator has no shape for yet");
                else Json(property.Type, property.ToDisplayString());
            }
            return;
        }
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Interface } handle && IsApi(handle))
        {
            Report(Rules.Uncrossable, Location.None, $"{where} takes a handle, which the generator has no shape for yet; list it in isolation-pending.txt");
            return;
        }
        if (IsByName(type))
        {
            Report(Rules.Uncrossable, Location.None, $"{where} takes {type.Name}, which crosses by name; the generator has no shape for it yet");
            return;
        }
        Json(type, where);
    }

    private void VisitCallback(Member member)
    {
        var where = member.DocId;
        if (member.Delegate?.DelegateInvokeMethod is not { } invoke)
        {
            Report(Rules.Uncrossable, Location.None, $"{where} is not a delegate the generator can call");
            return;
        }
        var (returnType, isAsync, isVoid) = Returns(invoke);
        if (!isAsync && !isVoid && !IsControl(returnType) && member.Crossing is not ("Resolver" or "Live" or "UnderLock"))
            Report(Rules.Unclassified, Location.None, $"{where} is a synchronous callback: classify it [PluginCrossing(Resolver | Live | UnderLock)]");
        foreach (var parameter in invoke.Parameters)
        {
            if (Is(parameter.Type, "System.Threading.CancellationToken")) continue;
            if (parameter.Type.TypeKind == TypeKind.Delegate || parameter.Type is INamedTypeSymbol { TypeKind: TypeKind.Interface } i && IsApi(i))
            {
                Report(Rules.Uncrossable, Location.None, $"{where} passes a callback or handle back to the plugin, which the generator has no shape for yet");
                continue;
            }
            Json(parameter.Type, where);
        }
        if (!isVoid && !IsControl(returnType)) Json(Unwrap(returnType), where);
    }

    private void Json(ITypeSymbol type, string where)
    {
        if (type is ITypeParameterSymbol || type.SpecialType == SpecialType.System_Void) return;
        if (IsControl(type)) return;
        if (json.Contains(Plain(type))) return;
        Report(Rules.MissingJson, Location.None,
            $"{where} serializes {type.ToDisplayString()}: add [JsonSerializable(typeof({Plain(type).ToDisplayString()}))] to IsolationJsonContext or IsolationUiJsonContext");
    }

    /// <summary>SRI002 and SRI006 over a member's whole signature.</summary>
    private void CheckSignature(Member member)
    {
        var where = member.DocId;
        var (parameters, returnType) = member.Symbol switch
        {
            IMethodSymbol method => (method.Parameters, method.ReturnType),
            IPropertySymbol property => (property.Parameters, property.Type),
            IParameterSymbol parameter => (ImmutableArray<IParameterSymbol>.Empty, parameter.Type),
            _ => (ImmutableArray<IParameterSymbol>.Empty, null as ITypeSymbol),
        };
        foreach (var parameter in parameters)
        {
            if (parameter.RefKind != RefKind.None && member.Shape is null)
                Report(Rules.Uncrossable, Location.None, $"{where}: {parameter.RefKind.ToString().ToLowerInvariant()} parameter {parameter.Name} cannot cross; give it an IsolatedShape");
            Walk(parameter.Type, member, factoryReturn: false);
        }
        if (returnType is not null) Walk(returnType, member, factoryReturn: false);
    }

    private void Walk(ITypeSymbol type, Member member, bool factoryReturn)
    {
        if (type is IArrayTypeSymbol array)
        {
            Walk(array.ElementType, member, false);
            return;
        }
        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
        {
            Uncrossable(member, "a pointer");
            return;
        }
        if (type is not INamedTypeSymbol named) return;
        if (named.IsTupleType)
        {
            foreach (var element in named.TupleElements)
            {
                if (!element.IsExplicitlyNamedTupleElement)
                    Report(Rules.UnnamedTuple, Location.None, $"{member.DocId}: tuple element {element.Name} has no name; name it so the generated record can");
                Walk(element.Type, member, false);
            }
            return;
        }
        if (Is(named, "System.Type") || Is(named, "System.Net.Http.HttpClient") || Derives(named, "System.IO.Stream")
            || Is(named.OriginalDefinition, "System.Span<T>") || Is(named.OriginalDefinition, "System.ReadOnlySpan<T>")
            || Derives(named, "Avalonia.Controls.TopLevel"))
        {
            Uncrossable(member, named.ToDisplayString());
            return;
        }
        if (IsControl(named))
        {
            if (!factoryReturn) Uncrossable(member, named.ToDisplayString() + " outside a factory's result");
            return;
        }
        if (named.TypeKind == TypeKind.Delegate && named.DelegateInvokeMethod is { } invoke)
        {
            foreach (var parameter in invoke.Parameters) Walk(parameter.Type, member, false);
            Walk(invoke.ReturnType, member, factoryReturn: true);
            return;
        }
        foreach (var argument in named.TypeArguments) Walk(argument, member, false);
    }

    private void Uncrossable(Member member, string what)
    {
        if (member.Shape is not null) return;
        Report(Rules.Uncrossable, Location.None, $"{member.DocId} uses {what}, which cannot cross; give it [PluginCrossing(..., IsolatedShape = \"...\")]");
    }

    private void Report(DiagnosticDescriptor rule, Location location, string message) =>
        table.Diagnostics.Add(Diagnostic.Create(rule, location, message));

    private static void Name(List<Member> list)
    {
        var used = new HashSet<string>();
        foreach (var member in list)
        {
            var owner = member.Owner.Name.StartsWith("I") && member.Owner.TypeKind == TypeKind.Interface ? member.Owner.Name.Substring(1) : member.Owner.Name;
            var name = member.Symbol is IParameterSymbol parameter
                ? $"{owner}_{member.Parent!.Symbol.Name}_{char.ToUpperInvariant(parameter.Name[0])}{parameter.Name.Substring(1)}"
                : $"{owner}_{member.Symbol.Name}";
            var candidate = name;
            for (var index = 2; !used.Add(candidate); index++) candidate = name + index;
            member.Name = candidate;
        }
    }

    public static (ITypeSymbol Type, bool Async, bool Void) Returns(ISymbol symbol)
    {
        var type = symbol switch
        {
            IMethodSymbol method => method.ReturnType,
            IPropertySymbol property => property.Type,
            _ => null,
        };
        if (type is null) return (null!, false, true);
        if (type.SpecialType == SpecialType.System_Void) return (type, false, true);
        return (type, IsTask(type), false);
    }

    public static bool IsTask(ITypeSymbol type) =>
        type is INamedTypeSymbol named && (Is(named.OriginalDefinition, "System.Threading.Tasks.Task") || Is(named.OriginalDefinition, "System.Threading.Tasks.Task<TResult>")
            || Is(named.OriginalDefinition, "System.Threading.Tasks.ValueTask") || Is(named.OriginalDefinition, "System.Threading.Tasks.ValueTask<TResult>"));

    /// <summary>A task's result type, or the type itself; <see langword="void"/>-like tasks give <see langword="null"/>.</summary>
    public static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { IsGenericType: true } named && IsTask(named) ? named.TypeArguments[0] : type;

    public static bool IsResultless(ITypeSymbol type) => IsTask(type) && type is INamedTypeSymbol { IsGenericType: false };

    public static bool Is(ITypeSymbol type, string name) => type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == name || type.ToDisplayString() == name;

    public static bool Derives(ITypeSymbol? type, string name)
    {
        for (; type is not null; type = type.BaseType)
            if (Is(type, name)) return true;
        return false;
    }

    public static bool IsControl(ITypeSymbol type) => Derives(type, "Avalonia.Controls.Control");

    public static bool IsDisposable(ITypeSymbol type) => Is(type, "System.IDisposable");

    public static bool IsApi(ITypeSymbol type)
    {
        var name = type.ContainingNamespace?.ToDisplayString() ?? "";
        return name == ApiNamespace || name.StartsWith(ApiNamespace + ".", System.StringComparison.Ordinal);
    }

    public static bool IsByName(ITypeSymbol type) =>
        type is INamedTypeSymbol named && IsApi(named) && named.Name is "PluginMethod" or "PluginChannel" or "PluginContract";

    public static bool HasCallbacks(INamedTypeSymbol type) =>
        Surface(type).OfType<IPropertySymbol>().Any(property => property.Type.TypeKind == TypeKind.Delegate);

    /// <summary>Whether a type is marked <c>[PluginCrossing(LocalInRunner)]</c> as a whole, so the runner implements it by hand.</summary>
    public static bool IsLocalType(INamedTypeSymbol type) =>
        type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == CrossingAttribute
            && attribute.ConstructorArguments.FirstOrDefault().Value is int value && value == 5);

    private static ITypeSymbol Plain(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
}