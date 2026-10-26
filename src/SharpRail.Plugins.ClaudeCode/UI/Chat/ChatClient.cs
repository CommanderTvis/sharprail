using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>
/// What the chat view needs of the plugin: one chat's pushes and the contract's methods. The view and its checks talk to
/// this, so a scripted fake can stand in for the host.
/// </summary>
internal interface IChatClient
{
    /// <summary>Delivers the chat's snapshot, then its pushes, on the UI thread; the snapshot is redelivered after a reconnect.</summary>
    IDisposable Subscribe(ChatKey key, Action<ChatPush> handler);

    ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default);
}

/// <summary>The real client: the plugin context's subscribe and call APIs over <see cref="ChatContract"/>.</summary>
internal sealed class PluginChatClient(IPluginUIContext context) : IChatClient
{
    public IDisposable Subscribe(ChatKey key, Action<ChatPush> handler) => context.Subscribe(ChatContract.Updates, handler, key);

    public ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default) =>
        context.RequestAsync(method, parameters, cancellationToken);
}