using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>
/// The app-owned composition every window shares: one host (its shared-state subscription and a
/// factory for per-window project sessions), the terminal factory and the profile. Each window
/// keeps its own layout in its <see cref="WindowProfile"/>.
/// </summary>
public sealed class Workbench : IDisposable
{
    private readonly Func<IProjectServices>? sessions;
    private readonly List<WorkbenchWindow> windows = [];

    public Workbench(ProfileStore profile, SharedState state, Terminal.TerminalFactory terminals, bool remote, Func<IProjectServices>? sessions)
    {
        Profile = profile; State = state; Terminals = terminals; Remote = remote; this.sessions = sessions;
        state.Start();
    }

    public ProfileStore Profile { get; }
    public SharedState State { get; }
    public Terminal.TerminalFactory Terminals { get; }
    public bool Remote { get; }
    public IReadOnlyList<WorkbenchWindow> Windows => windows;
    public bool CanOpenWindows => sessions is not null;
    /// <summary>Set while the app quits, so closing windows keep their profile entries for the next launch.</summary>
    public bool ShuttingDown { get; set; }
    public event Action<WorkbenchWindow>? WindowOpened;

    /// <summary>Opens a window for a profile entry, starting at <paramref name="root"/> when given.</summary>
    public WorkbenchWindow Open(WindowProfile slot, string root) =>
        Attach(new WorkbenchWindow(this, sessions?.Invoke() ?? throw new InvalidOperationException("This workbench cannot open windows."), slot, root));

    internal WorkbenchWindow Attach(WorkbenchWindow window)
    {
        windows.Add(window);
        window.Closed += (_, _) =>
        {
            windows.Remove(window);
            if (!ShuttingDown && windows.Count > 0) Profile.Data.Windows.Remove(window.Slot);
            Profile.Save();
            if (window.Host is IDisposable session && sessions is not null) session.Dispose();
            if (windows.Count == 0) Dispose();
        };
        WindowOpened?.Invoke(window);
        return window;
    }

    /// <summary>Opens another window at the Project Home of <paramref name="source"/>'s project, with a fresh frame.</summary>
    public WorkbenchWindow? NewWindow(WorkbenchWindow source)
    {
        if (!CanOpenWindows) return null;
        var project = source.ProjectRoot;
        var slot = new WindowProfile { LastProject = project, LastProjectRoot = project, LastAtHome = project.Length > 0 };
        Profile.Data.Windows.Add(slot);
        Profile.Save();
        var window = Open(slot, project);
        window.Width = source.Width; window.Height = source.Height;
        window.Show();
        return window;
    }

    public void Dispose() => State.Dispose();
}