using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.EnvPython.Host;

/// <summary>
/// Activates a workspace's Python environment the way <c>activate</c> would, for the main ways of making one: an
/// in-project venv (venv, virtualenv, uv, pdm, and poetry or pipenv configured to keep it in the project), a conda
/// prefix, a poetry or pipenv environment kept elsewhere, and the pyenv version a <c>.python-version</c> names. It
/// never creates an environment or installs anything.
/// </summary>
public sealed class EnvPythonHost : PluginHostModule
{
    internal static readonly string[] Directories = [".venv", "venv", "env"];

    public override PluginContract Contract => EnvPythonContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.WorkspaceEnvironment(EnvPythonContract.Order, ApplyAsync);
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    internal static async ValueTask ApplyAsync(IWorkspaceEnvironmentEdit edit, CancellationToken cancellationToken)
    {
        Pyenv(edit);
        // An earlier step that chose an environment keeps it.
        if (edit.Edited("VIRTUAL_ENV") || edit.Edited("CONDA_PREFIX")) return;
        foreach (var name in Directories)
        {
            var directory = Path.Combine(edit.Directory, name);
            if (File.Exists(Path.Combine(directory, "pyvenv.cfg"))) { Venv(edit, directory); return; }
            if (Directory.Exists(Path.Combine(directory, "conda-meta"))) { Conda(edit, directory); return; }
        }
        if (await Managed(edit, cancellationToken) is { } managed && File.Exists(Path.Combine(managed, "pyvenv.cfg"))) Venv(edit, managed);
    }

    private static void Venv(IWorkspaceEnvironmentEdit edit, string directory)
    {
        edit.Set("VIRTUAL_ENV", directory);
        edit.PrependPath(Path.Combine(directory, OperatingSystem.IsWindows() ? "Scripts" : "bin"));
        edit.Report("venv", directory);
    }

    private static void Conda(IWorkspaceEnvironmentEdit edit, string directory)
    {
        edit.Set("CONDA_PREFIX", directory);
        edit.Report("conda", directory);
        if (OperatingSystem.IsWindows()) edit.PrependPath(Path.Combine(directory, "Scripts"));
        edit.PrependPath(OperatingSystem.IsWindows() ? directory : Path.Combine(directory, "bin"));
    }

    // The version a .python-version names and pyenv's shims, so a shell without pyenv's own init resolves python the same way.
    private static void Pyenv(IWorkspaceEnvironmentEdit edit)
    {
        var file = Path.Combine(edit.Directory, ".python-version");
        var root = edit.Get("PYENV_ROOT") ?? Path.Combine(Home(edit), ".pyenv");
        if (!File.Exists(file) || !Directory.Exists(Path.Combine(root, "shims"))) return;
        var versions = File.ReadLines(file).Select(line => line.Split('#')[0].Trim()).Where(line => line.Length > 0).ToArray();
        if (versions.Length == 0) return;
        if (!edit.Edited("PYENV_VERSION")) edit.Set("PYENV_VERSION", string.Join(':', versions));
        if (edit.Get("PYENV_ROOT") is null) edit.Set("PYENV_ROOT", root);
        edit.PrependPath(Path.Combine(root, "shims"));
        edit.Report("pyenv " + versions[0], "pyenv versions from .python-version: " + string.Join(", ", versions));
    }

    // poetry and pipenv keep environments outside the project by default and report the path of the project's one.
    private static async ValueTask<string?> Managed(IWorkspaceEnvironmentEdit edit, CancellationToken cancellationToken)
    {
        (string Marker, string Tool, string[] Arguments)[] managers =
            [("poetry.lock", "poetry", ["env", "info", "--path"]), ("Pipfile.lock", "pipenv", ["--venv"]), ("Pipfile", "pipenv", ["--venv"])];
        foreach (var (marker, tool, arguments) in managers)
        {
            if (!File.Exists(Path.Combine(edit.Directory, marker)) || Locate(edit, tool) is not { } program) continue;
            var result = await edit.RunAsync(program, arguments, null, cancellationToken);
            if (result.Succeeded && result.Output.Trim().Split('\n')[^1].Trim() is { Length: > 0 } path && Directory.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Where poetry and pipenv are looked for besides PATH: Homebrew's and the user's bin, since an app started from the dock has only the system's PATH.</summary>
    internal static string[] SearchDirectories { get; set; } = ["/opt/homebrew/bin", "/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin"];

    private static string? Locate(IWorkspaceEnvironmentEdit edit, string tool)
    {
        if (edit.Find(tool) is { } found) return found;
        var home = Home(edit);
        return SearchDirectories.Concat(home.Length == 0 ? [] : [Path.Combine(home, ".local", "bin")]).Select(directory => Path.Combine(directory, tool)).FirstOrDefault(File.Exists);
    }

    private static string Home(IWorkspaceEnvironmentEdit edit) => edit.Get("HOME") ?? edit.Get("USERPROFILE") ?? "";
}