using SharpRail.Host.Abstractions;
using SharpRail.Host.Protocol;

namespace SharpRail.Host.Client;

internal static class WorkspaceMessages
{
    internal static WorkspaceRecord Map(WorkspaceRecordMessage message) =>
        new(message.Id, message.ProjectRoot, message.Kind, message.Path, message.Branch, message.BaseBranch) { InitialTerminalPending = message.InitialTerminalPending };
}