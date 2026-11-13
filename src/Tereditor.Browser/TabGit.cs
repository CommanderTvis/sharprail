using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia.Threading;

using Tereditor.Host.Core;

namespace Tereditor.UI.Browser;

/// <summary>The tab host's disk and Git, which live in <c>tabhost.js</c> beside the runtime's file system.</summary>
internal sealed partial class TabGit : IGitBackend
{
    private static Task? imported;

    [JSImport("start", "tabhost")]
    private static partial Task StartScript(string roots);
    [JSImport("gitRun", "tabhost")]
    private static partial Task<string> RunScript(string directory, string arguments, string? input);
    [JSImport("initRepository", "tabhost")]
    private static partial Task<string> InitializeScript(string directory);
    [JSImport("save", "tabhost")]
    private static partial Task SaveScript();

    internal sealed record Answer(int Exit, string Out, string Err);

    /// <summary>Loads the module, restores the saved files into the runtime's file system and starts saving them.</summary>
    public static async Task StartAsync(params string[] roots)
    {
        // The runtime keeps a failed import for good, so the page loads the module first: a fetch that failed is tried again on the next start.
        await BrowserInterop.Preload("tabhost.js");
        await (imported ??= JSHost.ImportAsync("tabhost", "../tabhost.js"));
        await StartScript(JsonSerializer.Serialize(roots, BrowserJson.Default.StringArray));
        GitBackends.Current = new TabGit();
    }

    /// <summary>Makes <paramref name="directory"/> a repository with one commit, unless it already is one.</summary>
    public static Task<string> InitializeAsync(string directory) => OnPage(() => InitializeScript(directory));

    public static Task SaveAsync() => OnPage(async () => { await SaveScript(); return 0; });

    public async Task<GitBackendResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, byte[]? input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = await OnPage(() => RunScript(workingDirectory, JsonSerializer.Serialize(arguments, BrowserJson.Default.IReadOnlyListString), input is null ? null : Convert.ToBase64String(input))).WaitAsync(cancellationToken);
        var answer = JsonSerializer.Deserialize(json, BrowserJson.Web.Answer)!;
        return new(answer.Exit, Convert.FromBase64String(answer.Out), answer.Err);
    }

    // Script calls belong to the page's own thread; the host runs elsewhere and only waits for the answer.
    internal static Task<T> OnPage<T>(Func<Task<T>> call) => Dispatcher.UIThread.InvokeAsync(call);
}
