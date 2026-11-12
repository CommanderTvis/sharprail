using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The one editor-event stream: the code editor, the Markdown preview and plugins' own documents report into it,
/// so nothing in an editor needs to know which plugin listens.
/// </summary>
public sealed class EditorEvents
{
    private readonly List<Action<EditorEvent>> observers = [];

    public IDisposable On(Action<EditorEvent> handler)
    {
        observers.Add(handler);
        return new Subscription(() => observers.Remove(handler));
    }

    public void Emit(EditorEvent editorEvent)
    {
        foreach (var observer in observers.ToArray())
        {
            try { observer(editorEvent); }
            catch (Exception error) { Console.Error.WriteLine("Editor event observer failed: " + error.Message); }
        }
    }

    internal sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() { dispose?.Invoke(); dispose = null; }
    }
}