namespace Tereditor.Plugins.Isolation;

/// <summary>One entry of a generated member table (Security.SPEC.md, Calls across the boundary).</summary>
/// <param name="Id">FNV-1a 32 of <paramref name="DocId"/>, the member id frames carry.</param>
/// <param name="DocId">The member's documentation-comment id; a callback parameter adds <c>#</c> and its name.</param>
/// <param name="Permission">The permission it needs, or empty.</param>
/// <param name="Condition">When the permission applies only sometimes, when.</param>
/// <param name="Ambient">Whether enabling the plugin grants it.</param>
/// <param name="Crossing">Its <c>PluginCrossing</c> class, or empty for a call or a one-way.</param>
/// <param name="Pending">Whether the isolated runtime does not implement it yet.</param>
public sealed record IsolationMember(int Id, string DocId, string Permission, string Condition, bool Ambient, string Crossing, bool Pending);