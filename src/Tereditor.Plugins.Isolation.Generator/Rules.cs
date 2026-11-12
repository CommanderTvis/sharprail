using Microsoft.CodeAnalysis;

namespace Tereditor.Plugins.Isolation.Generator;

/// <summary>The crossing rules, SRI001 to SRI006 (Security.SPEC.md, Calls across the boundary).</summary>
internal static class Rules
{
    private const string Category = "Isolation";

    public static readonly DiagnosticDescriptor Unclassified = new("SRI001", "Unclassified plugin-facing member",
        "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Uncrossable = new("SRI002", "A signature cannot cross the plugin channel",
        "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingJson = new("SRI003", "A by-value type has no JSON metadata",
        "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Collision = new("SRI004", "Two members share a member id",
        "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DefaultMember = new("SRI005", "A default interface member has no explicit implementation",
        "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnnamedTuple = new("SRI006", "A tuple element has no name",
        "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);
}