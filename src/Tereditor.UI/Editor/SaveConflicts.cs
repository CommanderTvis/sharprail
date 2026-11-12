using Tereditor.Host.Abstractions;

namespace Tereditor.UI.Editor;

/// <summary>What the user decided when a save found the file changed on disk.</summary>
internal enum SaveConflictChoice { Cancel, Compare, Overwrite, Reload }

/// <summary>A save the user declined to complete after a conflict; the buffer keeps its edits and nothing is reported.</summary>
internal sealed class SaveDeclinedException() : OperationCanceledException("The save was not completed.");

/// <summary>
/// Saving an editor buffer whose file changed on disk: the user picks Compare, Overwrite, Reload or Cancel, and the
/// choice is carried out through the host. The questions are delegates so the window supplies dialogs and a check supplies answers.
/// </summary>
internal static class SaveConflicts
{
    /// <summary>
    /// Saves <paramref name="view"/>; null when it was saved or reloaded, a <see cref="SaveDeclinedException"/> when the user
    /// cancelled or went to compare (the edits stay), and any other failure as it came. Reloading asks <paramref name="confirmReload"/>
    /// first and returns to the question when declined.
    /// </summary>
    internal static async Task<Exception?> SaveAsync(CodeDocumentView view, Func<Task<SaveConflictChoice>> ask,
        Func<Task<bool>> confirmReload, Func<string, Task> compare)
    {
        var error = await view.TrySaveAsync();
        while (error is HostException { Code: HostErrorCode.FileChangedOnDisk })
        {
            view.ChangedOnDisk = true;
            switch (await ask())
            {
                case SaveConflictChoice.Overwrite:
                    error = await view.TrySaveAsync(overwrite: true);
                    break;
                case SaveConflictChoice.Reload:
                    if (!await confirmReload()) continue;
                    try { await view.ReloadFromDiskAsync(); return null; }
                    catch (Exception failure) when (failure is not OperationCanceledException) { return failure; }
                case SaveConflictChoice.Compare:
                    await compare(view.Editor.Text);
                    return new SaveDeclinedException();
                default:
                    return new SaveDeclinedException();
            }
        }
        return error;
    }
}