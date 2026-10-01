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
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--native-terminal"))
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            NativeTerminalChecks.Run(args); return;
        }
        if (args.Contains("--native-texture") || args.Contains("--texture-fallback")) { NativeTextureChecks.Run(args); return; }
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
        if (args.SequenceEqual(["--workspaces"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            UiChecks.RunWorkspaceSuites(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS workspace and project E2E checks");
            return;
        }
        CheckHosts(root).GetAwaiter().GetResult();
        TerminalHostChecks.Run(root).GetAwaiter().GetResult();
        ProjectChecks.Run(root).GetAwaiter().GetResult();
        StateChecks.Run(root).GetAwaiter().GetResult();
        FileSavingChecks.Run(root).GetAwaiter().GetResult();
        LayoutChecks.Run();
        CheckOpenWorld();
        UiChecks.Run(root);
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
