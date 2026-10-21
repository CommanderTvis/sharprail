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

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.UI;

namespace SharpRail.Checks;

internal static class Program
{
    internal static void Checks(string[] args)
    {
        if (args.SequenceEqual(["--native-editor-find"])) { NativeEditorFindChecks.Run(args); return; }
        if (args.Contains("--fake-agent-launch")) { Thread.Sleep(30_000); return; }
        Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "1");
        E2E.IsolatedGit.ForProcess();
        if (args.Length > 0 && E2E.CodexE2E.Fake(args[0], args[1..]) is { } fixtureExit) { Environment.Exit(fixtureExit); return; }
        if (!args.Contains("--terminal-relay")) E2E.IsolatedGit.CheckIsolation();
        if (args.Contains("--native-terminal"))
        {
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
            NativeTerminalChecks.Run(args); return;
        }
        if (args.Contains("--native-terminal-links") || args.Contains("--native-scroll") || args.Contains("--native-file-links") || args.Contains("--native-direct") || args.Contains("--native-texture") || args.Contains("--texture-fallback") || args.Contains("--native-skia") || args.Contains("--native-osc52")) { NativeTextureChecks.Run(args); return; }
        if (args.Contains("--terminal-relay")) { Environment.Exit(SharpRail.UI.Terminal.TerminalRelay.Run()); return; }
        var root = CheckRoot.Create("fixture");
        Environment.SetEnvironmentVariable("SHARPRAIL_STATE_DIR", Path.Combine(root, "host-state"));
        // Enabling Codex installs its skills under CODEX_HOME; no check may reach the developer's own.
        Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, "codex-home"));
        Directory.CreateDirectory(root);
        // The checks binary is the fake language server and lives in the repository, which executables are otherwise never taken from.
        SharpRail.Host.Core.Language.LanguageExecutables.CheckExemptions.Add(SharpRail.Host.Core.Language.LanguageExecutables.Real(Environment.ProcessPath!));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", CheckRoot.Parent);
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Preview\n\nA **bold** paragraph with a [link](#preview).\n\n- [x] Done\n- [ ] Next\n\n| Name | Value |\n| --- | --- |\n| A | 1 |\n\n> [!NOTE]\n> A callout.\n\n\u0060\u0060\u0060cs\npublic class Example { }\n\u0060\u0060\u0060\n");
        File.WriteAllText(Path.Combine(root, "SPEC.md"), "---\nid: goal\ntitle: Project goal\ntype: product-goal\n---\n# Goal\n");
        File.WriteAllText(Path.Combine(root, "src", "SPEC.md"), "---\nid: architecture\ntitle: Architecture — components\nparent: goal\ntype: module-design\n---\n# Architecture\n");
        if (args is ["--rendering-bench", var surface])
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            try { RenderingBenchmarks.Run(surface, root); }
            finally { Directory.Delete(root, true); }
            return;
        }
        if (args.SequenceEqual(["--drawn-rendering"]) || args.SequenceEqual(["--drawn-trees"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            if (args[0] == "--drawn-trees") DrawnRenderingChecks.Trees();
            else DrawnRenderingChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--windows"]))
        {
            try
            {
                CheckOpenWorld();
                WindowsChecks.RunHost(root).GetAwaiter().GetResult();
                AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
                WindowsChecks.RunUi(root);
            }
            finally { E2E.IsolatedGit.DeleteFixture(root); }
            return;
        }
        if (args.SequenceEqual(["--linux"]))
        {
            try
            {
                CheckOpenWorld();
                LinuxChecks.RunHost(root).GetAwaiter().GetResult();
                AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
                LinuxChecks.RunUi(root);
            }
            finally { E2E.IsolatedGit.DeleteFixture(root); }
            return;
        }
        if (args.SequenceEqual(["--browser"]) || args.SequenceEqual(["--transport"]))
        {
            BrowserTransportChecks.Run(root).GetAwaiter().GetResult();
            WebSocketFlowChecks.Run().GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--file-transfer-actions"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.FilesE2E.Transfers(root);
            return;
        }
        if (args.SequenceEqual(["--file-drop"]))
        {
            try
            {
                FileDropChecks.Host(root).GetAwaiter().GetResult();
                AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
                SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
                FileDropChecks.Gestures();
                FileDropChecks.WorkbenchDrops(root);
            }
            finally { Directory.Delete(root, true); }
            return;
        }
        if (args.SequenceEqual(["--project-host"]))
        {
            ProjectChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--workspace-tools"]))
        {
            WorkspaceToolChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            WorkspaceToolChecks.RunUi(root);
            return;
        }
        if (args.SequenceEqual(["--context-ui"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AirContextChecks.Ui(root);
            return;
        }
        if (args.SequenceEqual(["--managed-artifacts"]))
        {
            ManagedLanguageChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--project-language-file"]))
        {
            ProjectLanguageFileChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--context"]))
        {
            AirContextChecks.Run(root).GetAwaiter().GetResult();
            AirContextChecks.Lifecycle(root).GetAwaiter().GetResult();
            AirContextChecks.Migration(root).GetAwaiter().GetResult();
            AirContextChecks.ProjectScope(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--lsp-real-csharp"]))
        {
            LanguageChecks.RealCSharp(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--lsp-real"]))
        {
            LanguageChecks.Real(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--lsp-settings"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            LanguageChecks.SettingsChecks(root);
            return;
        }
        if (args.SequenceEqual(["--lsp-icons"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            LanguageChecks.ProviderIconsUi();
            return;
        }
        if (args.SequenceEqual(["--lsp-ui"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            LanguageChecks.UiChecks(root);
            return;
        }
        if (args.SequenceEqual(["--lsp-on-demand"]))
        {
            LanguageOnDemandChecks.Run(root).GetAwaiter().GetResult();
            LanguageOnDemandChecks.Lifetime(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--lsp"]))
        {
            LanguageChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--scintilla-core"]))
        {
            ScintillaCore.ScintillaCoreChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--syntax"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            SyntaxHighlightChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--editor"]))
        {
            CheckOpenWorld();
            FileSavingChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            EditorChecks.Run();
            EditorTextChecks.Run();
            EditorWorkbenchChecks.Run(root);
            E2E.EditorE2E.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS editor integration checks");
            return;
        }
        if (args.SequenceEqual(["--ghostty-skia"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            GhosttySkiaChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--switch"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            SwitchChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--state"]))
        {
            StateChecks.Run(root).GetAwaiter().GetResult();
            Console.WriteLine("PASS host state checks");
            return;
        }
        if (args.SequenceEqual(["--vertical-tabs"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.VerticalTabsE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--tab-overflow"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.LayoutE2E.OverflowFades(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--agent-marks"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.AgentTabMarksE2E.Run(root);
            return;
        }
        if (args.SequenceEqual(["--agent-launches"]))
        {
            AgentLaunchChecks.RunHost();
            AgentLaunchContributionChecks.Run();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AgentLaunchChecks.RunUi(root);
            return;
        }
        if (args.SequenceEqual(["--ui-smoke"]))
        {
            UiChecks.Run(root, translations: false);
            return;
        }
        if (args.SequenceEqual(["--bottom-panel"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.BottomPanelE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--project-close"]))
        {
            TerminalHostChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ProjectContextE2E.Run(root);
            Console.WriteLine("PASS project closure checks");
            return;
        }
        if (args.SequenceEqual(["--terminal-links"]))
        {
            TerminalFileLinkChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            GhosttySkiaChecks.CheckFileLinks();
            TerminalFileLinkChecks.RunUi(root);
            EditorWorkbenchChecks.FileLinkPosition(root);
            if (OperatingSystem.IsMacOS()) TerminalFileLinkChecks.RunNativeRenderers();
            return;
        }
        if (args.SequenceEqual(["--terminal-resume-hint"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            TerminalResumeHintChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--terminal-replay"]))
        {
            TerminalReplayChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--terminal-watch-replay"]))
        {
            TerminalHostChecks.Run(root, watchReplayOnly: true).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--terminals"]))
        {
            TerminalHostChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.TerminalsE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.BottomPanelE2E.Run(Path.Combine(root, "upstream-e2e"));
            GhosttySkiaChecks.Run(root);
            Console.WriteLine("PASS terminal checks");
            return;
        }
        if (args.SequenceEqual(["--shared-terminals"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            using var git = new E2E.IsolatedGit(Path.Combine(root, "terminals-git"));
            E2E.SharedTerminalsE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--sync"]))
        {
            // Host checks block on async work, so they run before the UI synchronisation context, as in the full run.
            StateChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
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
        if (args.SequenceEqual(["--live-diffs"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.RenderedDiffE2E.Run(Path.Combine(root, "upstream-e2e"));
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
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.SpecsPanelE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.WelcomeE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--native-fsevents"]))
        {
            NativeFSEventsChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--watcher-hygiene"]))
        {
            WatcherHygieneChecks.Run(root).GetAwaiter().GetResult();
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
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.FilesE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.LiveRefreshE2E.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS file watching checks");
            return;
        }
        if (args.SequenceEqual(["--commands"]))
        {
            QuitConfirmationChecks.Run();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AppCommandChecks.Run(root);
            Console.WriteLine("PASS quit and close command checks");
            return;
        }
        if (args.SequenceEqual(["--about"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AboutChecks.Run();
            Console.WriteLine("PASS about screen checks");
            return;
        }
        if (args.SequenceEqual(["--connection-errors"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ShellChecks.RunConnectionErrors(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS connection error details and retry");
            return;
        }
        if (args.SequenceEqual(["--shell"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ShellChecks.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS shell and header checks");
            return;
        }
        if (args.SequenceEqual(["--branch-status"]))
        {
            BranchStatusChecks.Run(root).GetAwaiter().GetResult();
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
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            ContentChecks.RunUi(root);
            ResourceChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--documents"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            ResourceChecks.RunDocuments(root);
            return;
        }
        if (args.SequenceEqual(["--git-host"]))
        {
            GitHostChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ChangesScopeE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--sharprail-exclude"]))
        {
            SharprailExcludeChecks.Host(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            SharprailExcludeChecks.Ui(root);
            return;
        }
        if (args.SequenceEqual(["--workspace-clone"]))
        {
            WorkspaceCloneChecks.Run(root).GetAwaiter().GetResult();
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
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ReconnectE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--registry"]))
        {
            WorkspaceRegistryChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.ExternalWorkspaceE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--change-actions"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ChangeActionChecks.Run(Path.Combine(root, "change-actions"));
            return;
        }
        if (args.SequenceEqual(["--review-presentation"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ChangesReviewChecks.Presentation();
            return;
        }
        if (args.SequenceEqual(["--diff-virtualization"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            DiffVirtualizationChecks.Run();
            ChangesReviewChecks.Run(Path.Combine(root, "diff-virtualization"));
            return;
        }
        if (args.SequenceEqual(["--review-retention"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ChangesReviewChecks.Retention();
            return;
        }
        if (args.SequenceEqual(["--review"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
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
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
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
            return;
        }
        if (args.SequenceEqual(["--zig-deps"]))
        {
            ZigDepsChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--lock-script"]))
        {
            LockScriptChecks.Run();
            return;
        }
        if (args is ["--android-package", ..] && args.Length <= 2)
        {
            AndroidPackageChecks.Run(args.ElementAtOrDefault(1));
            return;
        }
        if (args.SequenceEqual(["--android"]))
        {
            AndroidClientChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--check-root"]))
        {
            CheckRootChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--runner"]))
        {
            RunnerChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--plugins"]))
        {
            PluginHostChecks.Run(root).GetAwaiter().GetResult();
            SpecDialectChecks.Run(root).GetAwaiter().GetResult();
            BlueprintChecks.Run(root).GetAwaiter().GetResult();
            ClaudeCodeChecks.Run(root).GetAwaiter().GetResult();
            DiscordChecks.RunHostAsync(root).GetAwaiter().GetResult();
            WorkspaceWatchChecks.Run(root).GetAwaiter().GetResult();
            BranchGraphChecks.RunHostAsync(root).GetAwaiter().GetResult();
            VisualizeChecks.Host(root).GetAwaiter().GetResult();
            FileIconsChecks.Host(root).GetAwaiter().GetResult();
            CodexChecks.RunHost(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            PluginUiChecks.Run(root);
            FileActionChecks.Run(root);
            PluginWatchChecks.Run(root);
            SpecDialectChecks.RunUi(Path.Combine(root, "upstream-e2e"));
            BlueprintChecks.RunUi(root);
            ClaudeCodeChecks.RunUi(root);
            ClaudeCodeChecks.Launcher(root);
            DiscordChecks.RunUi(root);
            PdfPreviewChecks.Run(root);
            EpubChecks.Run(root);
            BranchGraphChecks.RunUi(root);
            E2E.VisualizeE2E.Run(root);
            FileIconsChecks.UiChecks(root);
            E2E.CodexE2E.Run(root);
            AwayNotificationChecks.Run(root);
            Console.WriteLine("PASS plugin checks");
            return;
        }
        if (args.SequenceEqual(["--gitless-panes"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            GitlessPaneChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--drawers"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            DrawerChecks.Run(root);
            DrawerChecks.Pages(root);
            return;
        }
        if (args.SequenceEqual(["--settings-touch"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            SettingsTouchChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--branch-graph"]))
        {
            BranchGraphChecks.RunHostAsync(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            BranchGraphChecks.RunUi(root);
            Console.WriteLine("PASS Branch Graph checks");
            return;
        }
        if (args.SequenceEqual(["--visualize"]))
        {
            VisualizeChecks.Host(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.VisualizeE2E.Run(root);
            E2E.MarkdownMermaidE2E.Run(root);
            return;
        }
        if (args.SequenceEqual(["--visualize-selection"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            VisualizeSelectionChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--visualize-host"]))
        {
            VisualizeChecks.Host(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--codex-terminals"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.CodexE2E.RunTerminals(root);
            return;
        }
        if (args.SequenceEqual(["--codex"]))
        {
            CodexChecks.RunHost(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.CodexE2E.Run(root);
            return;
        }
        if (args.SequenceEqual(["--file-icons"]))
        {
            FileIconsChecks.Host(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            FileIconsChecks.UiChecks(root);
            return;
        }
        if (args.SequenceEqual(["--file-watch"]))
        {
            WorkspaceWatchChecks.Run(root).GetAwaiter().GetResult();
            return;
        }
        if (args.SequenceEqual(["--pdf-preview"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            PdfPreviewChecks.Run(root);
            Console.WriteLine("PASS PDF Preview checks");
            return;
        }
        if (args.SequenceEqual(["--epub"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            EpubChecks.Run(root);
            Console.WriteLine("PASS EPUB Reader checks");
            return;
        }
        if (args.SequenceEqual(["--discord"]))
        {
            DiscordChecks.RunHostAsync(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            DiscordChecks.RunUi(root);
            Console.WriteLine("PASS Discord checks");
            return;
        }
        if (args.SequenceEqual(["--claude-code"]))
        {
            ClaudeCodeChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ClaudeCodeChecks.RunUi(root);
            ClaudeCodeChecks.Launcher(root);
            Console.WriteLine("PASS Claude Code checks");
            return;
        }
        if (args.SequenceEqual(["--notifications"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AwayNotificationChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--blueprint"]))
        {
            BlueprintChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            BlueprintChecks.RunUi(root);
            Console.WriteLine("PASS Blueprint checks");
            return;
        }
        if (args.SequenceEqual(["--shared-controls"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            SharedControlChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--create-project"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            CreateProjectChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--pane-retention"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            PaneRetentionChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--dock-input"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            DockInputChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--gc-settings"]))
        {
            GcSettingsChecks.Run(root);
            return;
        }
        if (args.SequenceEqual(["--startup"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            StartupChecks.Run(root);
            Console.WriteLine("PASS startup");
            return;
        }
        if (args.SequenceEqual(["--scratch"]))
        {
            LayoutChecks.Run();
            ProjectChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            var e2e = Path.Combine(root, "upstream-e2e");
            E2E.TerminalChromeChecks.Run(e2e);
            E2E.WorkspaceTabsE2E.Run(e2e);
            E2E.MarkdownDocumentE2E.Run(e2e);
            E2E.LayoutE2E.Run(e2e);
            NavigationChecks.Run(root);
            StartupChecks.Run(root);
            DockInputChecks.Run(root);
            Console.WriteLine("PASS scratch");
            return;
        }
        if (args.SequenceEqual(["--specs"]))
        {
            SpecDialectChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            var e2e = Path.Combine(root, "upstream-e2e");
            SpecDialectChecks.RunUi(e2e);
            E2E.PreviewTabsE2E.Run(e2e);
            E2E.LayoutE2E.NoSpecsOpensFiles(e2e);
            E2E.MarkdownDocumentE2E.SpecDocuments(e2e);
            E2E.LiveRefreshE2E.Run(e2e);
            Console.WriteLine("PASS spec dialect checks");
            return;
        }
        if (args.SequenceEqual(["--welcome"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.WelcomeE2E.Run(root);
            Console.WriteLine("PASS Welcome checks");
            return;
        }
        if (args.SequenceEqual(["--new-workspace"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.NewWorkspaceShortcutE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.NewWorkspaceE2E.Run(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS new-workspace E2E checks");
            return;
        }
        if (args.SequenceEqual(["--files"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.FilesE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--rail"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.CrossProjectTabsE2E.Run(Path.Combine(root, "upstream-e2e"));
            E2E.RailRetentionChecks.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--switch-latency"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.SwitchLatencyChecks.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--local-browser-fixture"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.LocalBrowserApprovalFixture.Run(root);
            return;
        }
        if (args.SequenceEqual(["--local-browser-approval"]))
        {
            LocalBrowserConnectionChecks.RunHost(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.LocalBrowserApprovalE2E.Run(root);
            return;
        }
        if (args.SequenceEqual(["--serving"]))
        {
            HostListenerChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.HostServingE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--markdown-chunks"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            DrawnRenderingChecks.MarkdownChunks();
            return;
        }
        if (args.SequenceEqual(["--markdown-find"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.MarkdownFindE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--markdown-tables"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            MarkdownTableDiffChecks.Run();
            MarkdownMermaidDiffChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--markdown-images"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            MarkdownImageChecks.Run();
            return;
        }
        if (args.SequenceEqual(["--frontmatter"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.MarkdownDocumentE2E.FrontmatterEditing(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--markdown-link-layout"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            MarkdownSelectionChecks.LinkDiffLayout();
            return;
        }
        if (args.SequenceEqual(["--markdown-code"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            MarkdownSelectionChecks.CodeCopyLayout();
            return;
        }
        if (args.SequenceEqual(["--markdown"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            var e2e = Path.Combine(root, "upstream-e2e");
            E2E.MarkdownLinksE2E.Run(e2e);
            E2E.MarkdownAlertsE2E.Run(e2e);
            E2E.MarkdownDocumentE2E.Run(e2e);
            return;
        }
        if (args.SequenceEqual(["--settings"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.SettingsGitHubE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--branches"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.BranchListE2E.Run(Path.Combine(root, "upstream-e2e"));
            return;
        }
        if (args.SequenceEqual(["--workspace-delete"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            using (new E2E.IsolatedGit(Path.Combine(root, "workspace-delete-git")))
                E2E.WorkspaceResponsivenessE2E.DeleteOther(Path.Combine(root, "workspace-delete"));
            return;
        }
        if (args.SequenceEqual(["--workspaces"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            UiChecks.RunWorkspaceSuites(Path.Combine(root, "upstream-e2e"));
            Console.WriteLine("PASS workspace and project E2E checks");
            return;
        }
        if (args.SequenceEqual(["--files-modes"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            E2E.FilesModesE2E.Run(root);
            Console.WriteLine("PASS Files modes checks");
            return;
        }
        if (args.SequenceEqual(["--changes"]) || args.SequenceEqual(["--changes-menu"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            if (args[0] == "--changes-menu")
                E2E.ChangesScopeE2E.FailedRead(root, E2E.ChangesFixture.Source ?? throw new InvalidOperationException("Set SHARPRAIL_TEST_GIT_SOURCE."));
            else
            {
                E2E.ChangesE2E.Run(root);
                E2E.ChangesScopeE2E.Run(root);
            }
            Console.WriteLine("PASS Changes checks");
            return;
        }
        if (args.SequenceEqual(["--air-governance"]))
        {
            AirGovernanceChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            AirGovernanceChecks.RunUi(root);
            return;
        }
        if (args.SequenceEqual(["--button-centering"]))
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            ButtonCenteringChecks.RunUi();
            return;
        }
        if (args.SequenceEqual(["--tailscale"]))
        {
            TailscaleChecks.Run(root).GetAwaiter().GetResult();
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().AfterSetup(HostHotkeys.Apply).SetupWithoutStarting();
            SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
            TailscaleChecks.RunUi(root);
            return;
        }
        if (args.SequenceEqual(["--headscale"]))
        {
            // Opt-in and slow: never a gate case. Needs `sh scripts/build-headscale-test.sh`.
            try { HeadscaleChecks.Run(root).GetAwaiter().GetResult(); }
            finally { Directory.Delete(root, true); }
            return;
        }
        Gate.Case("packaged-app", PackagedApp.Run);
        Gate.Case("gc-settings", () => GcSettingsChecks.Run(root));
        Gate.Case("boundaries", () => BoundaryChecks.Run(root));
        Gate.Case("zig-deps", () => ZigDepsChecks.Run());
        Gate.Case("lock-script", () => LockScriptChecks.Run());
        Gate.Case("check-root", CheckRootChecks.Run);
        Gate.Case("runner", () => RunnerChecks.Run(root));
        Gate.Case("layout", LayoutChecks.Run);
        Gate.Case("scintilla-core", ScintillaCore.ScintillaCoreChecks.Run);
        Gate.Case("quit-confirmation", QuitConfirmationChecks.Run);
        Gate.Case("android-client", () => AndroidClientChecks.Run(root));
        Gate.Case("open-world", CheckOpenWorld);
        Gate.Case("websocket-flow", () => WebSocketFlowChecks.Run().GetAwaiter().GetResult());
        Gate.Case("browser-transport", () => BrowserTransportChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("context-consent", () => AirContextChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("air-context-lifecycle", () => AirContextChecks.Lifecycle(root).GetAwaiter().GetResult());
        Gate.Case("air-context-migration", () => AirContextChecks.Migration(root).GetAwaiter().GetResult());
        Gate.Case("air-context-project", () => AirContextChecks.ProjectScope(root).GetAwaiter().GetResult());
        Gate.Case("global-jvm-provider", () => GlobalJvmProviderChecks.Run(root).GetAwaiter().GetResult());

        Gate.Case("managed-language-servers", () => ManagedLanguageChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("project-language-file", () => ProjectLanguageFileChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("project-language-autostart", () => ProjectLanguageAutostartChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("language-services", () => LanguageChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("language-on-demand", () => { LanguageOnDemandChecks.Run(root).GetAwaiter().GetResult(); LanguageOnDemandChecks.Lifetime(root).GetAwaiter().GetResult(); });
        Gate.Case("language-exit", () => LanguageExitChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("hosts", () => CheckHosts(root).GetAwaiter().GetResult());
        Gate.Case("terminal-commit-links", () => TerminalCommitLinkChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("terminal-links", () => TerminalFileLinkChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("terminal-hosts", () => TerminalHostChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("windows-host", () => WindowsChecks.RunHost(root).GetAwaiter().GetResult());
        Gate.Case("workspace-tools", () => WorkspaceToolChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("agent-launch-origins", AgentLaunchChecks.RunHost);
        Gate.Case("agent-launch-contributions", AgentLaunchContributionChecks.Run);
        Gate.Case("file-drop-host", () => FileDropChecks.Host(root).GetAwaiter().GetResult());
        Gate.Case("projects", () => ProjectChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("pull-requests", () => PullRequestChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("branch-status", () => BranchStatusChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("changes", () => ChangeChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("content", () => ContentChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("state", () => StateChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("file-saving", () => FileSavingChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("process", () => ProcessChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("workspace-clone", () => WorkspaceCloneChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("git-host", () => GitHostChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("sharprail-exclude", () => SharprailExcludeChecks.Host(root).GetAwaiter().GetResult());
        Gate.Case("registry", () => WorkspaceRegistryChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("watcher-hygiene", () => WatcherHygieneChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("watchers", () => WatchChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("reconnect", () => ReconnectChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("host-state", () => StateStoreChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("project-paths", () => ProjectPathChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("specs", () => SpecChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("air-governance", () => AirGovernanceChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("tailscale", () => TailscaleChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("local-browser-approval", () => LocalBrowserConnectionChecks.RunHost(root).GetAwaiter().GetResult());
        Gate.Case("host-listener", () => HostListenerChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("plugin-host", () => PluginHostChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("spec-dialect", () => SpecDialectChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("blueprint", () => BlueprintChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("claude-code", () => ClaudeCodeChecks.Run(root).GetAwaiter().GetResult());
        Gate.Case("DiscordChecks-RunHostAsync", () => DiscordChecks.RunHostAsync(root).GetAwaiter().GetResult());
        Gate.Case("BranchGraphChecks-RunHostAsync", () => BranchGraphChecks.RunHostAsync(root).GetAwaiter().GetResult());
        Gate.Case("VisualizeChecks-Host", () => VisualizeChecks.Host(root).GetAwaiter().GetResult());
        Gate.Case("FileIconsChecks-Host", () => FileIconsChecks.Host(root).GetAwaiter().GetResult());
        Gate.Case("codex-host", () => CodexChecks.RunHost(root).GetAwaiter().GetResult());
        Gate.Case("workspace-watch", () => WorkspaceWatchChecks.Run(root).GetAwaiter().GetResult());
        UiChecks.Run(root);
        Gate.Case("resources", () => ResourceChecks.Run(root));
        Gate.Case("markdown-table-diffs", MarkdownTableDiffChecks.Run);
        Gate.Case("markdown-mermaid-diffs", MarkdownMermaidDiffChecks.Run);
        Gate.Case("markdown-images", MarkdownImageChecks.Run);
        Gate.Case("drawn-rendering", DrawnRenderingChecks.Run);
        Gate.Case("design", () => Design.DesignChecks.Run(write: false));
        Gate.Case("design-roles", Design.RoleChecks.Run);
        Gate.Case("tailscale-ui", () => TailscaleChecks.RunUi(root));
        Gate.Case("button-centering", ButtonCenteringChecks.RunUi);
        Gate.Case("air-governance-ui", () => AirGovernanceChecks.RunUi(root));
        Gate.Case("plugin-ui", () => PluginUiChecks.Run(root));
        Gate.Case("file-drop-ui", () => FileDropChecks.WorkbenchDrops(root));
        Gate.Case("file-drop-gestures", FileDropChecks.Gestures);
        Gate.Case("file-manager", FileManagerChecks.Run);
        Gate.Case("file-reveal-ui", () => E2E.FileRevealE2E.Run(root));
        Gate.Case("files-modes-ui", () => E2E.FilesModesE2E.Run(root));
        Gate.Case("file-actions", () => FileActionChecks.Run(root));
        Gate.Case("spec-dialect-ui", () => SpecDialectChecks.RunUi(Path.Combine(root, "upstream-e2e")));
        Gate.Case("blueprint-ui", () => BlueprintChecks.RunUi(root));
        Gate.Case("claude-code-ui", () => ClaudeCodeChecks.RunUi(root));
        Gate.Case("DiscordChecks-RunUi", () => DiscordChecks.RunUi(root));
        Gate.Case("PdfPreviewChecks-Run", () => PdfPreviewChecks.Run(root));
        Gate.Case("epub-reader", () => EpubChecks.Run(root));
        Gate.Case("BranchGraphChecks-RunUi", () => BranchGraphChecks.RunUi(root));
        Gate.Case("FileIconsChecks-UiChecks", () => FileIconsChecks.UiChecks(root));
        Gate.Case("codex-ui", () => E2E.CodexE2E.Run(root));
        Gate.Case("away-notifications", () => AwayNotificationChecks.Run(root));
        Gate.Case("plugin-watch", () => PluginWatchChecks.Run(root));
        Gate.Case("visualize-ui", () => E2E.VisualizeE2E.Run(root));
        Gate.Case("visualize-selection", VisualizeSelectionChecks.Run);
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