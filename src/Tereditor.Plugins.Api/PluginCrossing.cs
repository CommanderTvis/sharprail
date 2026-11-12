namespace SharpRail.Plugins.Api;

/// <summary>
/// How a synchronous member, or a callback a plugin passes in, crosses into the isolated runtime, where the plugin runs
/// in another process than SharpRail. Asynchronous members cross as calls and <see langword="void"/> members as one-way
/// messages without being named here. Builtin plugins run in SharpRail's process and are unaffected.
/// </summary>
public enum PluginCrossing
{
    /// <summary>The plugin process keeps a copy fed by SharpRail; a read never waits.</summary>
    Mirrored,

    /// <summary>A pure callback SharpRail calls with a deadline and caches per argument until the plugin invalidates it.</summary>
    Resolver,

    /// <summary>A callback SharpRail calls with a deadline at a known moment and does not cache; a miss counts as no contribution.</summary>
    Live,

    /// <summary>A callback SharpRail calls before it takes a lock, applying the result under the lock.</summary>
    UnderLock,

    /// <summary>A synchronous call the plugin process blocks on with a short deadline; a miss throws <see cref="TimeoutException"/>.</summary>
    Blocking,

    /// <summary>Implemented inside the plugin process, against what its sandbox already allows.</summary>
    LocalInRunner,

    /// <summary>Not offered to isolated plugins; <see cref="PluginCrossingAttribute.IsolatedShape"/> says what takes its place.</summary>
    Unavailable,
}

/// <summary>Classifies how a member, a callback parameter or record property, or every member of a type crosses into the isolated runtime.</summary>
/// <param name="crossing">The crossing class.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Interface | AttributeTargets.Class,
    Inherited = false)]
public sealed class PluginCrossingAttribute(PluginCrossing crossing) : Attribute
{
    /// <summary>The crossing class.</summary>
    public PluginCrossing Crossing { get; } = crossing;

    /// <summary>For a member whose signature cannot cross as it is, the shape it takes in the isolated runtime.</summary>
    public string? IsolatedShape { get; init; }
}

/// <summary>Marks a member, or every member of a type, as ambient: enabling the plugin grants it, with no permission of its own.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class PluginAmbientAttribute : Attribute;

/// <summary>Names the permission an isolated plugin needs to use a member, or every member of a type.</summary>
/// <param name="name">The permission, such as <c>files.read</c> or <c>terminal.input</c>.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface | AttributeTargets.Class, Inherited = false)]
public sealed class PluginPermissionAttribute(string name) : Attribute
{
    /// <summary>The permission.</summary>
    public string Name { get; } = name;

    /// <summary>When set, the permission applies only under this condition, which the trusted side checks; the member is ambient otherwise.</summary>
    public string? Condition { get; init; }
}