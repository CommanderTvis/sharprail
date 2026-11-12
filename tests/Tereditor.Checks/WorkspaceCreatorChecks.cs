using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;

namespace Tereditor.Checks;

/// <summary>A workspace's creator is recorded only by an attributed workspace_create and never outlives its workspace.</summary>
internal static class WorkspaceCreatorChecks
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "workspace-creator");
        var project = Path.Combine(directory, "project"); Directory.CreateDirectory(project);
        var first = Path.Combine(directory, "workspace-1"); Directory.CreateDirectory(first);
        var store = new HostStateStore(Path.Combine(directory, "state"));
        WorkspaceRecord Default() => new("default", project, WorkspaceKinds.Default, project, "main", "main");
        WorkspaceRecord Managed() => new("managed", project, WorkspaceKinds.Managed, first, "workspace-1", "main");
        store.ChangeWorkspaces(_ => [Default(), Managed()]);
        // A workspace the registry made without any terminal identity (the UI's path) has no creator.
        if (store.WorkspaceCreator(first) is not null) throw new InvalidOperationException("A workspace made without attribution has a creator.");
        store.RecordWorkspaceCreator(first, new(project, "terminal:a"));
        store.ChangeWorkspaces(_ => [Default()]);
        store.ChangeWorkspaces(_ => [Default(), Managed()]);
        if (store.WorkspaceCreator(first) is not null || store.Current.WorkspaceCreators.Count != 0)
            throw new InvalidOperationException("A reused workspace path inherited the creator of its deleted predecessor.");
    }
}