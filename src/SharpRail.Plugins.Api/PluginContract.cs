namespace SharpRail.Plugins.Api;

/// <summary>
/// One request method a plugin declares: its name and the record types of its params and result. Params are
/// validated at dispatch, before the registered handler runs: on the wire by strict deserialization into
/// <see cref="ParamsType"/> with <see cref="PluginJson.Options"/>, in process by the type itself. Results are
/// not validated per call. Declare methods as <see cref="PluginMethod{TParams, TResult}"/>.
/// </summary>
public abstract class PluginMethodSpec
{
    private protected PluginMethodSpec(string name, Type paramsType, Type resultType)
    {
        Name = name; ParamsType = paramsType; ResultType = resultType;
    }

    /// <summary>The method name, unique within the contract; the wire name is <see cref="PluginIdentity.MethodName"/>.</summary>
    public string Name { get; }

    /// <summary>The record type params deserialize into.</summary>
    public Type ParamsType { get; }

    /// <summary>The type of the method's result.</summary>
    public Type ResultType { get; }
}

/// <summary>
/// A typed request method. Both halves of a plugin, and a dependent plugin, name the method by this value, so
/// a call's params and result are typed from it.
/// </summary>
/// <typeparam name="TParams">The params record; a method without params uses an empty record.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="name">The method name, unique within the contract.</param>
/// <example>
/// <code>
/// public sealed record ListTodos;
/// public static readonly PluginMethod&lt;ListTodos, string[]&gt; List = new("listTodos");
/// </code>
/// </example>
public sealed class PluginMethod<TParams, TResult>(string name) : PluginMethodSpec(name, typeof(TParams), typeof(TResult));

/// <summary>Whether a channel carries recoverable state or lossy events.</summary>
public enum PluginChannelKind
{
    /// <summary>A state channel: a subscriber reads its keyed snapshot on subscribe and on every reconnect, then receives pushes.</summary>
    State,

    /// <summary>An event channel: lossy, with no snapshot, for host-to-client requests that time out on their own.</summary>
    Event
}

/// <summary>
/// One channel a plugin declares. A state channel names the method that returns its current value and the
/// param fields that key a subscription to one scope, so a subscriber reads the snapshot for its own scope on
/// subscribe and on reconnect rather than relying on any replay. Declare channels as
/// <see cref="PluginChannel{TPayload}"/>.
/// </summary>
public abstract class PluginChannelSpec
{
    private protected PluginChannelSpec(string name, PluginChannelKind kind, Type payloadType, string? snapshot, IReadOnlyList<string> key)
    {
        Name = name; Kind = kind; PayloadType = payloadType; Snapshot = snapshot; Key = key;
    }

    /// <summary>The channel name, unique within the contract; the wire name is <see cref="PluginIdentity.ChannelName"/>.</summary>
    public string Name { get; }

    /// <summary>Whether the channel carries state or events.</summary>
    public PluginChannelKind Kind { get; }

    /// <summary>The type of each pushed payload.</summary>
    public Type PayloadType { get; }

    /// <summary>For a state channel, the name of the method returning the current value; <see langword="null"/> for an event channel.</summary>
    public string? Snapshot { get; }

    /// <summary>
    /// For a state channel, the JSON names of the fields that key a subscription. They are fields of the
    /// snapshot method's params and of every payload; a subscriber's scope supplies their values, and a push
    /// whose key fields differ from the scope is not delivered to it. Empty for an unkeyed or event channel.
    /// </summary>
    public IReadOnlyList<string> Key { get; }
}

/// <summary>A typed channel, built with <see cref="State{TParams}"/> or <see cref="Event"/>.</summary>
/// <typeparam name="TPayload">The type of each pushed payload.</typeparam>
public sealed class PluginChannel<TPayload> : PluginChannelSpec
{
    private PluginChannel(string name, PluginChannelKind kind, string? snapshot, IReadOnlyList<string> key)
        : base(name, kind, typeof(TPayload), snapshot, key) { }

    /// <summary>Declares a state channel whose current value <paramref name="snapshot"/> returns.</summary>
    /// <typeparam name="TParams">The snapshot method's params, which carry the key fields.</typeparam>
    /// <param name="name">The channel name, unique within the contract.</param>
    /// <param name="snapshot">The method returning the current value for a scope; it must be declared in the same contract.</param>
    /// <param name="key">The JSON names of the fields keying a subscription; empty for one unkeyed value.</param>
    /// <returns>The channel.</returns>
    public static PluginChannel<TPayload> State<TParams>(string name, PluginMethod<TParams, TPayload> snapshot, params IReadOnlyList<string> key) =>
        new(name, PluginChannelKind.State, snapshot.Name, key);

    /// <summary>Declares a lossy event channel with no snapshot.</summary>
    /// <param name="name">The channel name, unique within the contract.</param>
    /// <returns>The channel.</returns>
    public static PluginChannel<TPayload> Event(string name) => new(name, PluginChannelKind.Event, null, []);
}

/// <summary>
/// A plugin's one wire contract: its methods, channels and settings type under a single wire version. The host
/// half hands it to the runtime, which validates params against it; the roster carries the channels' kind,
/// snapshot and key so a UI half never needs the host half's assembly.
/// </summary>
public sealed class PluginContract
{
    private PluginContract(string id, int wireVersion, IReadOnlyList<PluginMethodSpec> methods, IReadOnlyList<PluginChannelSpec> channels, Type? settingsType)
    {
        Id = id; WireVersion = wireVersion; Methods = methods; Channels = channels; SettingsType = settingsType;
    }

    /// <summary>The plugin's id; every method and channel name is namespaced under it.</summary>
    public string Id { get; }

    /// <summary>Incremented whenever <see cref="Methods"/> or <see cref="Channels"/> change; a UI half at a different version stays dormant.</summary>
    public int WireVersion { get; }

    /// <summary>The plugin's request methods.</summary>
    public IReadOnlyList<PluginMethodSpec> Methods { get; }

    /// <summary>The plugin's channels.</summary>
    public IReadOnlyList<PluginChannelSpec> Channels { get; }

    /// <summary>
    /// The record type the plugin's settings namespace deserializes into, its property initializers being the
    /// defaults; <see langword="null"/> when the plugin keeps no settings. It may not declare <c>enabled</c>,
    /// which core owns.
    /// </summary>
    public Type? SettingsType { get; }

    /// <summary>Declares a contract whose settings namespace is <typeparamref name="TSettings"/>.</summary>
    /// <typeparam name="TSettings">The settings record; a parameterless instance holds the defaults.</typeparam>
    /// <param name="id">The plugin's id.</param>
    /// <param name="wireVersion">The contract's wire version.</param>
    /// <param name="methods">The request methods.</param>
    /// <param name="channels">The channels.</param>
    /// <returns>The contract.</returns>
    /// <example>
    /// <code>
    /// public static readonly PluginContract Contract = PluginContract.Create&lt;TodoSettings&gt;(
    ///     "todo-board", 1, [List], [PluginChannel&lt;string[]&gt;.State("todos", List)]);
    /// </code>
    /// </example>
    public static PluginContract Create<TSettings>(string id, int wireVersion, IReadOnlyList<PluginMethodSpec> methods, IReadOnlyList<PluginChannelSpec> channels)
        where TSettings : class, new() => new(id, wireVersion, methods, channels, typeof(TSettings));

    /// <summary>Declares a contract with no settings namespace beyond the core-owned enable flag.</summary>
    /// <param name="id">The plugin's id.</param>
    /// <param name="wireVersion">The contract's wire version.</param>
    /// <param name="methods">The request methods.</param>
    /// <param name="channels">The channels.</param>
    /// <returns>The contract.</returns>
    public static PluginContract Create(string id, int wireVersion, IReadOnlyList<PluginMethodSpec> methods, IReadOnlyList<PluginChannelSpec> channels) =>
        new(id, wireVersion, methods, channels, null);
}

/// <summary>
/// What a declared dependency grants at runtime: calls to its methods and subscriptions to its channels. On the
/// host the runtime dispatches in process; in the app the call takes the ordinary plugin call path. It grants
/// nothing beyond these two operations.
/// </summary>
public interface IPluginDependencyHandle
{
    /// <summary>Calls one of the dependency's declared methods.</summary>
    /// <typeparam name="TParams">The method's params.</typeparam>
    /// <typeparam name="TResult">The method's result.</typeparam>
    /// <param name="method">The method, from the dependency's contract.</param>
    /// <param name="parameters">The params, validated against the dependency's contract.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The method's result.</returns>
    /// <exception cref="PluginCallException">The dependency is disabled, the method is unknown, the params are invalid or the handler failed.</exception>
    ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to one of the dependency's declared channels.</summary>
    /// <typeparam name="TPayload">The channel's payload.</typeparam>
    /// <param name="channel">The channel, from the dependency's contract.</param>
    /// <param name="handler">Called with each pushed payload.</param>
    /// <returns>Disposing it cancels the subscription.</returns>
    IDisposable Subscribe<TPayload>(PluginChannel<TPayload> channel, Action<TPayload> handler);
}

/// <summary>Why a plugin call failed.</summary>
public enum PluginCallError
{
    /// <summary>No known plugin has that id, or its contract declares no such method or channel.</summary>
    Unknown,

    /// <summary>The plugin is known but not active. Expected while a plugin is being turned off.</summary>
    Disabled,

    /// <summary>The params did not deserialize into the method's params type; the message names the offending path.</summary>
    InvalidParams,

    /// <summary>The plugin's handler threw; the message is the handler's.</summary>
    Failed
}

/// <summary>A failed plugin method call, carrying the same <see cref="Error"/> locally and over the wire.</summary>
/// <param name="error">Why the call failed.</param>
/// <param name="message">The message, naming the plugin and method.</param>
public sealed class PluginCallException(PluginCallError error, string message) : Exception(message)
{
    /// <summary>Why the call failed.</summary>
    public PluginCallError Error { get; } = error;
}
