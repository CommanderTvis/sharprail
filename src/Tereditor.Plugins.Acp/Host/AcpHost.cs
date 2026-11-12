using System.Text.Json.Serialization.Metadata;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>The ACP plugin's host half: the agent list and the chat sessions over ACP agents' stdio.</summary>
public sealed class AcpHost : PluginHostModule
{
    public override PluginContract Contract => AcpContract.Contract;

    public override IJsonTypeInfoResolver Json => AcpHostJson.Default;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var chats = new AcpChatHost(context);
        chats.Register();
        return ValueTask.FromResult<PluginDisposer?>(() => new ValueTask(chats.DisposeAsync()));
    }
}