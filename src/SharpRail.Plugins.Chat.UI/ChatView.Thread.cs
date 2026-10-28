using Avalonia.Controls;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

internal sealed partial class ChatView
{
    private sealed class ThreadActions(ChatTranscript thread) : IChatRowActions
    {
        public void Activate(ChatRow row, ChatHitKind kind, string? arg = null)
        {
            if (kind == ChatHitKind.Toggle) thread.Toggle(row.Id);
        }
    }

    /// <summary>The subagent's thread alone: its direct items at the top level, deeper ones nested as before.</summary>
    internal static ChatItem InThread(ChatItem item, string task) => item.ParentId == task ? item with { ParentId = null } : item;

    private void OpenThread(string taskId)
    {
        if (transcript.Item(taskId) is not ChatToolItem task) return;
        var thread = new ChatTranscript();
        foreach (var item in transcript.AllItems().Where(item => transcript.Under(item, taskId)).OrderBy(item => item.Seq)) thread.Upsert(InThread(item, taskId));
        void Follow(ChatItem item)
        {
            if (transcript.Under(item, taskId)) thread.Upsert(InThread(item, taskId));
        }
        transcript.ItemChanged += Follow;
        var threadList = new ChatList(thread, new ThreadActions(thread)) { Name = "ChatThreadList", Height = 460, MinWidth = 640 };
        var dialog = DialogWindow.Create("Subagent: " + (task.Summary ?? task.Name), 720);
        dialog.Tag = "ChatSubagentThread";
        dialog.FindControl<TextBlock>("DialogExplanation")!.Text = "Everything this subagent did, live.";
        dialog.FindControl<StackPanel>("DialogFields")!.Children.Add(threadList);
        dialog.FindControl<StackPanel>("DialogActions")!.Children.Add(Ui.Button("Close", () => dialog.Close()));
        dialog.Closed += (_, _) => transcript.ItemChanged -= Follow;
        threadDialog = threadList;
        threadTranscript = thread;
        if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner);
    }

    private ChatList? threadDialog;
    private ChatTranscript? threadTranscript;
    internal ChatTranscript? ThreadTranscript => threadTranscript;
    internal ChatList? ThreadList => threadDialog;
    internal void ShowThread(string taskId) => OpenThread(taskId);
}