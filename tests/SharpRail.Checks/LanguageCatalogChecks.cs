using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Core.Plugins;

namespace SharpRail.Checks;

/// <summary>The language server catalog is one snapshot, rebuilt only when the plugin roster changes, and queries of it allocate almost nothing.</summary>
internal static class LanguageCatalogChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(50);
        }
    }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "lsp-catalog");
        var state = new HostStateStore(Path.Combine(root, "state"));
        await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = Path.Combine(root, "state"), State = state });
        await runtime.Start();
        await Until(() => state.LanguageServerSpecs().Any(spec => spec.Id == "csharp"), "The builtin C# server never joined the catalog.");

        var servers = new LanguageServers(Path.Combine(root, "language"), catalog: state.LanguageServerSpecs);
        var first = servers.Current;
        var rebuilds = LanguageServers.Rebuilds;
        for (var i = 0; i < 1000; i++)
        {
            Require(ReferenceEquals(servers.Current, first) && ReferenceEquals(state.LanguageCatalog, first.Servers), "An unchanged roster rebuilt the catalog.");
            _ = servers.Find("csharp");
            _ = servers.Detect("/w/src/Program.cs");
        }
        Require(LanguageServers.Rebuilds == rebuilds, $"Queries rebuilt the catalog {LanguageServers.Rebuilds - rebuilds} times.");

        // Warm, then measure: the lookups a pane refresh, census and routing make, per query.
        const int queries = 10_000;
        string[] paths = ["/w/src/Program.cs", "/w/web/app.ts", "/w/Makefile", "/w/notes.unknown", "/w/main.go"];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < queries; i++)
        {
            _ = servers.Servers.Count;
            _ = servers.Find("csharp");
            _ = servers.Detect(paths[i % paths.Length]);
            _ = LanguageServers.Known("csharp");
        }
        var perQuery = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)queries;
        Require(perQuery < 8, $"Catalog queries allocate {perQuery:F1} bytes each; the budget is 8.");
        Console.WriteLine($"PASS catalog queries reuse one snapshot and allocate {perQuery:F2} bytes per query");

        // Each detection matches the roster-order scan it replaced.
        foreach (var path in paths.Append("/w/Dockerfile").Append("/w/CMakeLists.txt").Append("/w/x.h"))
        {
            var scan = servers.Servers.FirstOrDefault(server => server.Extensions.Contains(Path.GetExtension(path)))
                ?? servers.Servers.FirstOrDefault(server => server.Spec.FileNames.Contains(Path.GetFileName(path)));
            Require(ReferenceEquals(servers.Detect(path), scan), "Indexed detection differs from the roster scan for " + path);
        }

        await state.ChangeAsync([HostStateChange.PluginSettings("lsp-csharp", """{"enabled":false}""")]);
        await Until(() => servers.Find("csharp") is null, "Disabling the C# plugin left its server in the catalog.");
        var disabled = servers.Current;
        Require(!ReferenceEquals(disabled, first) && LanguageServers.Rebuilds > rebuilds, "A roster change did not rebuild the catalog.");
        rebuilds = LanguageServers.Rebuilds;
        for (var i = 0; i < 100; i++) _ = servers.Detect("/w/src/Program.cs");
        Require(LanguageServers.Rebuilds == rebuilds && ReferenceEquals(servers.Current, disabled), "The rebuilt catalog was not reused.");
        Require(LanguageServers.Known("csharp") is not null, "A disabled plugin's server stopped being known.");

        await state.ChangeAsync([HostStateChange.PluginSettings("lsp-csharp", """{"enabled":true}""")]);
        await Until(() => servers.Find("csharp") is not null, "Re-enabling the C# plugin did not restore its server.");
        Require(LanguageServers.Rebuilds > rebuilds, "Re-enabling did not rebuild the catalog.");
        Console.WriteLine("PASS the catalog is rebuilt only when the plugin roster changes");
    }
}
