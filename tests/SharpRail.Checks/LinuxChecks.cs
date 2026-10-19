using System.Text;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Scintilla;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

/// <summary>
/// The Linux desktop gate (<c>--linux</c>), run from the packaged AppImage payload on Linux. The portable checks also run
/// elsewhere; the PTY, copy-on-write clone and native-library checks need Linux and are skipped on other platforms.
/// </summary>
internal static class LinuxChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Linux support: " + message); }

    internal static async Task RunHost(string root)
    {
        // Under check:packaged this verifies that the checks run beside the AppImage's own files; elsewhere it does nothing.
        PackagedApp.Run();
        Require(OperatingSystem.IsMacOS() == (new ProfileStore(Path.Combine(root, "linux-profile")).Data.Preferences.TerminalRenderer == TerminalRenderers.Texture),
            "Only macOS defaults to the Metal texture renderer.");
        Console.WriteLine("PASS Linux portable checks: terminal renderer default");
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("SKIP Linux runtime checks: Linux is required."); return; }
        await Terminal(root);
        Clone(root);
        Console.WriteLine("PASS Linux runtime: PTY input, Unicode, resize, exit code and copy-on-write workspace clone");
    }

    private static async Task Terminal(string root)
    {
        var workspace = Path.Combine(root, "linux host λ");
        Directory.CreateDirectory(workspace);
        await using var local = new PtyTerminalService(shell: "/bin/sh");
        await using var session = await new LocalTerminalAdapter(local).AttachAsync(new("linux-terminal", workspace, "holder", 100, 30));
        var output = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        var reader = Task.Run(async () =>
        {
            await foreach (var chunk in session.ReadAsync())
            {
                var characters = new char[chunk.Length];
                var count = decoder.GetChars(chunk.Span, characters, false);
                lock (output) output.Append(characters, 0, count);
            }
        });
        async Task Seen(string value)
        {
            var end = Awake.Now.AddSeconds(30);
            while (true)
            {
                lock (output) if (output.ToString().Contains(value, StringComparison.Ordinal)) return;
                if (Awake.Now > end) throw new InvalidOperationException("Linux support: terminal output missing: " + value + "\n" + output);
                await Task.Delay(25);
            }
        }
        Task Send(string line) => session.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).AsTask();
        // The echoed command line carries the format string, so only the shell's own output contains the joined text.
        await Send("printf 'READY_%s\\n' 'λ😀'");
        await Seen("READY_λ😀");
        await Send("printf 'CWD_%s\\n' \"$(pwd -P)\"");
        await Seen("CWD_" + Path.GetFullPath(workspace));
        await session.ResizeAsync(113, 37);
        await Send("stty size");
        await Seen("37 113");
        await Send("exit 23");
        Require(await session.Exit.WaitAsync(TimeSpan.FromSeconds(20)) == 23, "The shell's exit code was lost.");
        await reader.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void Clone(string root)
    {
        var source = Path.Combine(root, "linux-clone-source");
        Directory.CreateDirectory(Path.Combine(source, "deps", "pkg"));
        File.WriteAllText(Path.Combine(source, "deps", "pkg", "index.js"), "module λ");
        File.CreateSymbolicLink(Path.Combine(source, "deps", "link"), "/nonexistent-shared-store");
        var target = Path.Combine(root, "linux-clone-target");
        Require(WorkspaceClone.Supported && WorkspaceClone.TryClone(Path.Combine(source, "deps"), target), "A directory tree did not clone.");
        Require(File.ReadAllText(Path.Combine(target, "pkg", "index.js")) == "module λ", "The cloned content differs.");
        Require(new FileInfo(Path.Combine(target, "link")).LinkTarget == "/nonexistent-shared-store", "A symlink was not kept as a symlink.");
        File.WriteAllText(Path.Combine(target, "pkg", "index.js"), "changed");
        Require(File.ReadAllText(Path.Combine(source, "deps", "pkg", "index.js")) == "module λ", "A clone writes through to its source.");
        Require(!WorkspaceClone.TryClone(Path.Combine(source, "missing"), Path.Combine(root, "linux-clone-none")) && !Directory.Exists(Path.Combine(root, "linux-clone-none")),
            "A failed clone left a path behind.");
    }

    internal static void RunUi(string root)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var editor = new ScintillaEditor("alpha\nλ😀", EditorChecks.Font);
        var window = new Window { Width = 640, Height = 320, Content = editor };
        window.Show(); Pump(); editor.Focus();
        try
        {
            window.KeyTextInput("edited λ😀"); Pump();
            Require(editor.Text == "edited λ😀alpha\nλ😀" && editor.IsModified, "Unicode input did not edit the document.");
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z"); Pump();
            Require(editor.Text == "alpha\nλ😀" && !editor.IsModified, "Ctrl+Z did not restore the original document and save point.");
            using var view = new GhosttySkiaView { Typeface = EditorChecks.Font };
            window.Content = view; Pump();
            view.Write(Encoding.UTF8.GetBytes("Linux λ😀\r\n\e[31mred\e[0m")); Pump();
            Require(view.ReadScreen().Contains("Linux λ😀", StringComparison.Ordinal), "The Linux native terminal library did not render Unicode.");
            using var image = window.CaptureRenderedFrame();
            Require(image is not null && image.PixelSize.Width > 0, "The Linux Skia view did not paint.");
            using var backend = TerminalBackends.Ghostty(new NeverAttach(), () => TerminalRenderers.Texture)(new(root, "linux-renderer", root, "client"));
            Require(backend is SkiaTerminal, "A saved Metal preference must select Skia on Linux.");
        }
        finally { window.Content = null; window.Close(); Pump(); }
        Console.WriteLine("PASS Linux UI: native editor, Skia native loading and rendering, and Metal preference migration");
    }

    private sealed class NeverAttach : ITerminalService
    {
        public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("An unmounted backend must not attach.");
        public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static void Pump()
    { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}