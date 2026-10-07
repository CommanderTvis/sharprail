using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Grpc.Core;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI;

namespace SharpRail.Checks;

internal static class Program
{
    internal static void Checks(string[] args)
    {
        // Fixture repositories are never pushed; their commits must not wait on the developer's signing agent.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "commit.gpgsign");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "false");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "tag.gpgsign");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", "false");
        if (args.Contains("--native-terminal"))
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            NativeTerminalChecks.Run(args); return;
        }
        if (args.Contains("--native-direct") || args.Contains("--native-texture") || args.Contains("--texture-fallback") || args.Contains("--native-skia") || args.Contains("--native-osc52")) { NativeTextureChecks.Run(args); return; }
        if (args.Contains("--terminal-relay")) { Environment.Exit(SharpRail.UI.Terminal.TerminalRelay.Run()); return; }
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".bench", "check-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", Directory.GetCurrentDirectory());
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Preview\n\nA **bold** paragraph with a [link](#preview).\n\n- [x] Done\n- [ ] Next\n\n| Name | Value |\n| --- | --- |\n| A | 1 |\n\n> [!NOTE]\n> A callout.\n\n\u0060\u0060\u0060cs\npublic class Example { }\n\u0060\u0060\u0060\n");
        File.WriteAllText(Path.Combine(root, "SPEC.md"), "---\nid: goal\ntitle: Project goal\ntype: product-goal\n---\n# Goal\n");
        File.WriteAllText(Path.Combine(root, "src", "SPEC.md"), "---\nid: architecture\ntitle: Architecture — components\nparent: goal\ntype: module-design\n---\n# Architecture\n");
        if (args.SequenceEqual(["--editor"]))
        {
            CheckOpenWorld();
            FileSavingChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            EditorChecks.Run();
            EditorTextChecks.Run();
            EditorWorkbenchChecks.Run(root);
            Console.WriteLine("PASS editor integration checks");
            return;
        }
        if (args.SequenceEqual(["--ghostty-skia"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            GhosttySkiaChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--terminal-replay"]))
        {
            TerminalReplayChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--terminals"]))
        {
            TerminalHostChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.TerminalsE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.BottomPanelE2E.Run(Path.Combine(root, "upstream-e2e"));
            GhosttySkiaChecks.Run(root);
            Console.WriteLine("PASS terminal checks");
            return;
        }
        if (args.SequenceEqual(["--sync"]))
        {
            // Host checks block on async work, so they run before the UI synchronisation context, as in the full run.
            StateChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            var e2e = Path.Combine(root, "upstream-e2e");
            E2E.MultiClientE2E.Run(e2e);
            E2E.ProjectContextE2E.Run(e2e);
            E2E.LayoutSettingsE2E.Run(e2e);
            E2E.LineWidthE2E.Run(e2e);
            E2E.ThemeE2E.Run(e2e);
            Console.WriteLine("PASS multi-window and multi-client checks");
            return;
        }
        if (args.SequenceEqual(["--host-state"]))
        {
            StateChecks.Run(root).GetAwaiter().GetResult();
            StateStoreChecks.Run(root).GetAwaiter().GetResult();
            ProjectPathChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--specs-panel"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.SpecsPanelE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.WelcomeE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--watchers"]))
        {
            WatchChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--specs"]))
        {
            SpecChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--files"]))
        {
            ProjectChecks.Run(root).GetAwaiter().GetResult();
            WatchChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.FilesE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.LiveRefreshE2E.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS file watching checks");
            return;
        }
        if (args.SequenceEqual(["--commands"]))
        {
            QuitConfirmationChecks.Run();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AppCommandChecks.Run(root);
            Console.WriteLine("PASS quit and close command checks");
            return;
        }
        if (args.SequenceEqual(["--shell"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ShellChecks.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS shell and header checks");
            return;
        }
        if (args.SequenceEqual(["--pull-requests"]))
        {
            PullRequestChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--content"]))
        {
            ContentChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            ContentChecks.RunUi(root);
            ResourceChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--documents"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            ResourceChecks.RunDocuments(root);
            return;
        }
        if (args.SequenceEqual(["--git-host"]))
        {
            GitHostChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ChangesScopeE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--process"]))
        {
            ProcessChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--changes"]))
        {
            ChangeChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--reconnect"]))
        {
            ReconnectChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ReconnectE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--registry"]))
        {
            WorkspaceRegistryChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ExternalWorkspaceE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--change-actions"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ChangeActionChecks.Run(Path.Combine(root, "change-actions"));
            return;
        }
        if (args.SequenceEqual(["--review"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ReviewChecks.Run(Path.Combine(root, "review"));
            return;
        }
        if (args.SequenceEqual(["--ui"]))
        {
            UiChecks.Run(root);
            return;
        }
        if (args.Length > 0 && args[0] == "--design")
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            Design.DesignChecks.Run(args.Contains("--write"));
            if (args.Contains("--write")) return;
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            Design.RoleChecks.Run();
            E2E.ThemeE2E.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS design-system checks");
            return;
        }
        if (args.SequenceEqual(["--conformance"]))
        {
            BoundaryChecks.Run(root);
            SurfaceChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--runner"]))
        {
            RunnerChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--workspaces"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            UiChecks.RunWorkspaceSuites(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS workspace and project E2E checks");
            return;
        }
        Gate.Case("packaged-app", PackagedApp.Run);
        Gate.Case("hosts", () => CheckHosts(root).GetAwaiter().GetResult());
        Gate.Case("boundaries", () => BoundaryChecks.Run(root));
        Gate.Case("public-surface", () => SurfaceChecks.Run(root));
        Gate.Case("runner", () => RunnerChecks.Run(root));
        Gate.Case("terminal-hosts", () => TerminalHostChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("projects", () => ProjectChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("pull-requests", () => PullRequestChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("changes", () => ChangeChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("content", () => ContentChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("state", () => StateChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("file-saving", () => FileSavingChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("process", () => ProcessChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("git-host", () => GitHostChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("registry", () => WorkspaceRegistryChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("watchers", () => WatchChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("reconnect", () => ReconnectChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("host-state", () => StateStoreChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("project-paths", () => ProjectPathChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("specs", () => SpecChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("layout", LayoutChecks.Run);
        Gate.Case("quit-confirmation", QuitConfirmationChecks.Run);
        Gate.Case("open-world", CheckOpenWorld);
        UiChecks.Run(root);
        Gate.Case("resources", () => ResourceChecks.Run(root));
        Gate.Case("design", () => Design.DesignChecks.Run(write: false));
        Gate.Case("design-roles", Design.RoleChecks.Run);
        Console.WriteLine("PASS prototype checks and open-world runtime");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckHosts(string root)
    {
        IWorkspaceHost core = new WorkspaceHost(root);
        var local = new LocalHostAdapter(core);
        var expected = await local.GetWorkspaceAsync();
        var files = await local.ListRootFilesAsync();
        Assert(files.Any(file => file.Name == "hello.txt" && !file.IsDirectory), "Local files missing.");
        Assert(files[0].IsDirectory, "Directories must sort first.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await local.ListRootFilesAsync(canceled.Token);
            throw new InvalidOperationException("Local cancellation ignored.");
        }
        catch (OperationCanceledException) { }

        await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "test-session-token");
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteHostAdapter(new Uri(address), "test-session-token");
            Assert(await remote.GetWorkspaceAsync() == expected, "Remote metadata differs.");
            Assert((await remote.ListRootFilesAsync()).SequenceEqual(files), "Remote listing differs.");
            try
            {
                await remote.ListRootFilesAsync(canceled.Token);
                throw new InvalidOperationException("Remote cancellation ignored.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Cancelled) { }
            catch (OperationCanceledException) { }

            using var unauthorized = new RemoteHostAdapter(new Uri(address), "wrong-token");
            try
            {
                await unauthorized.GetWorkspaceAsync();
                throw new InvalidOperationException("Remote accepted a bad token.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
        }
        finally { await app.StopAsync(); }
    }

    private static void CheckOpenWorld()
    {
        if (Environment.GetEnvironmentVariable("SHARPRAIL_REQUIRE_R2R") == "1")
        {
            foreach (var assembly in new[] { typeof(Program).Assembly, typeof(WorkbenchWindow).Assembly, typeof(ProjectServices).Assembly })
            {
                using var file = File.OpenRead(assembly.Location);
                using var pe = new PEReader(file);
                Assert(pe.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size > 0,
                    $"ReadyToRun header missing: {assembly.GetName().Name}");
            }
        }
        Assert(RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled, "JIT is unavailable.");
        var method = new DynamicMethod("ResearchProbe", typeof(int), Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, 42);
        il.Emit(OpCodes.Ret);
        Assert(method.CreateDelegate<Func<int>>()() == 42, "Runtime emission failed.");
        var context = new AssemblyLoadContext("ResearchProbe", isCollectible: true);
        using var stream = File.OpenRead(typeof(WorkspaceInfo).Assembly.Location);
        var loaded = context.LoadFromStream(stream);
        Assert(loaded.GetType(typeof(WorkspaceInfo).FullName!) is not null, "Dynamic assembly loading failed.");
        context.Unload();
    }

}