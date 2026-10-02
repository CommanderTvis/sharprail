using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.FileIcons;
using SharpRail.Plugins.FileIcons.UI;

namespace SharpRail.Checks;

// The builtin file-icons plugin: the fork's fileIcon.test.ts name rules, and its host test that a manifest-only
// builtin's assets are served from the staged build output, with a missing file and an escape refused.
internal static partial class FileIconsChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Host(string root)
    {
        Names();
        await Assets(Path.Combine(root, "file-icons-host"));
        await RemoteAssets(Path.Combine(root, "file-icons-remote-assets"));
        Console.WriteLine("PASS file-icons plugin: whole names over extensions, longest extension first, the fallback icon, assets served from the build output");
    }

    private static void Names()
    {
        Require(FileIconsUI.Name("src/Main.kt") == "kotlin" && FileIconsUI.Name("data.JSON") == "json" && FileIconsUI.Name("/abs/path/app.tsx") == "react_ts",
            "An extension names the icon, whatever case it arrives in.");
        Require(FileIconsUI.Name("vitest.config.ts") == "vitest" && FileIconsUI.Name("Dockerfile") == "docker" && FileIconsUI.Name("package.json") == "nodejs",
            "A whole filename wins over its extension.");
        Require(FileIconsUI.Name("types/api.d.ts") == "typescript-def" && FileIconsUI.Name("types/api.ts") == "typescript", "A longer extension wins over a shorter one.");
        Require(FileIconsUI.Name(".gitignore") == "git" && FileIconsUI.Name(".env") == "tune", "A dotfile is read the same way.");
        Require(FileIconsUI.Name("notes.qqqq") == "file" && FileIconsUI.Name("LICENSE-something") == "file", "Anything unrecognised falls back to the plain file icon.");
        var first = FileIconsUI.Resolve("src/a.ts", FileIconKind.File);
        Require(first == "asset:file-icons/typescript.svg" && FileIconsUI.Resolve("lib/b.ts", FileIconKind.File) == first &&
            FileIconsUI.Resolve("readme.md", FileIconKind.File) != first && FileIconsUI.Resolve("src", FileIconKind.Directory) is null,
            "The slot answers one asset per icon, so the host reads it once, and leaves directories to core.");
    }

    private static async Task Assets(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = new HostStateStore(directory) });
        await runtime.Start();
        Require((await runtime.ListAsync()).Single(entry => entry.Id == FileIconsManifest.Id) is { Status: PluginStatus.Active, Origin: PluginOrigin.Builtin, Assets: "assets", Channels.Count: 0 },
            "The file-icons plugin is a manifest-only builtin, on by default, declaring its assets.");
        var bytes = await runtime.ReadFileAsync(FileIconsManifest.Id, "assets/file-icons/typescript.svg");
        Require(bytes is not null, "The generated builtin SVG reaches the output directory.");
        var icon = System.Text.Encoding.UTF8.GetString(bytes!);
        Require(icon.Contains("<svg", StringComparison.Ordinal) && icon.Contains("currentColor", StringComparison.Ordinal),
            "A manifest-only builtin's asset is served from the staged build output, recoloured to currentColor.");
        foreach (var path in new[] { "assets/file-icons/does-not-exist.svg", "assets/../../SharpRail.Checks.dll" })
        {
            var refused = false;
            try { refused = await runtime.ReadFileAsync(FileIconsManifest.Id, path) is null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PluginCallException) { refused = true; }
            Require(refused, "A missing asset and an escape from the plugin's directory are refused: " + path);
        }
    }

    private static async Task RemoteAssets(string directory)
    {
        Directory.CreateDirectory(directory);
        await using var server = RemoteServer.Create(directory, IPAddress.Loopback, 0, "file-icons", directory + "-state");
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemotePluginAdapter(address, "file-icons");
            var runtime = server.Services.GetRequiredService<PluginRuntime>();
            foreach (var name in new[] { "readme", "markdown", "pdf", "image", "typescript", "kotlin", "file" })
            {
                var path = "assets/file-icons/" + name + ".svg";
                var expected = await runtime.ReadFileAsync(FileIconsManifest.Id, path);
                var actual = await remote.ReadFileAsync(FileIconsManifest.Id, path);
                Require(expected is not null && actual is not null && actual.SequenceEqual(expected), "Remote asset bytes match the staged builtin: " + name);
            }
            Require(await remote.ReadFileAsync(FileIconsManifest.Id, "assets/file-icons/missing.svg") is null &&
                await remote.ReadFileAsync(FileIconsManifest.Id, "assets/../../SharpRail.Checks.dll") is null,
                "The remote adapter preserves missing-asset and containment semantics.");
        }
        finally { await server.StopAsync(); }
    }
}