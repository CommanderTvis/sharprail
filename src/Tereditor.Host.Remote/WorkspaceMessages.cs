using Tereditor.Host.Abstractions;
using Tereditor.Host.Protocol;

namespace Tereditor.Host.Remote;

internal static class WorkspaceMessages
{
    internal static WorkspaceRecordMessage Map(WorkspaceRecord workspace) => new()
    {
        Id = workspace.Id,
        ProjectRoot = workspace.ProjectRoot,
        Kind = workspace.Kind,
        Path = workspace.Path,
        Branch = workspace.Branch,
        BaseBranch = workspace.BaseBranch,
        InitialTerminalPending = workspace.InitialTerminalPending
    };
}