using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.UI.Editor;

namespace SharpRail.Checks;

internal static class NativeEditorFindChecks
{
    private const string Title = "SharpRail native editor find check";
    internal static void Run(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (AppBuilder.Configure<CheckApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args) != 0)
            throw new InvalidOperationException("Native editor Find failed; see the assertion above.");
    }

    private sealed class CheckApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
            Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve(""));
        }
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new Window { Title = Title, Width = 1000, Height = 650 };
            NativeMenu.SetMenu(window, SharpRail.UI.ApplicationMenu.Build(window, null!));
            desktop.MainWindow = window;
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await Task.Delay(200);
                    var native = Content(Title);
                    Require(native != 0, "Native window missing.");
                    Activate(native);
                    await Until(() => Ready(native), "native window activation");
                    foreach (var mode in new MarkdownDocumentView.Mode?[] { null, MarkdownDocumentView.Mode.Source, MarkdownDocumentView.Mode.Split })
                    {
                        var markdown = mode is not null;
                        // The view inspects its language through the host shortly after opening; a null host threw from that
                        // inspection whenever it ran before the check finished.
                        var root = CheckRoot.Create("editor-find");
                        using var code = new CodeDocumentView(new("find.txt", "🚆 **Needle** first\nneedle second"), root,
                            new SharpRail.Host.Client.LocalProjectAdapter(new SharpRail.Host.Core.ProjectServices(root)), () => { }, _ => { }, () => { }, error => throw error, !markdown);
                        Control body = code;
                        if (markdown)
                        {
                            var document = new MarkdownDocumentView(code.Editor.Text, "find.md", new((_, _) => ValueTask.FromResult<byte[]?>(null), _ => null, (_, _) => { }, 13, 78, true), _ => code);
                            body = document;
                            window.Content = body;
                            await Task.Delay(100);
                            document.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == (mode == MarkdownDocumentView.Mode.Source ? "MarkdownSourceMode" : "MarkdownSplitMode"))
                                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                        }
                        else window.Content = body;
                        await Task.Delay(100);
                        Require(code.Editor.Focus(), "Editor cannot focus.");
                        await Task.Delay(50);
                        Command(native, "f", 3);
                        await Until(() => body.GetLogicalDescendants().OfType<TextBox>().Any(box => box.Name == "FindInput" && box.IsFocused), $"Command F in {mode}");
                        var input = body.GetLogicalDescendants().OfType<TextBox>().SingleOrDefault(box => box.Name == "FindInput" && box.IsEffectivelyVisible);
                        Require(input?.IsFocused == true, $"Native Command+F must open/focus {(markdown ? "Markdown source" : "code")} Find.");
                        input!.Text = "needle";
                        await Task.Delay(50);
                        Require(code.Editor.Selection.Text == "Needle" && !code.Editor.IsModified, "Find must select without modifying the native document.");
                        Key(native, "\r", 36, false);
                        await Task.Delay(50);
                        Require(code.Editor.Selection.Text == "needle", "Native Return must advance the match.");
                        Key(native, "\u001b", 53, false);
                        await Until(() => code.Editor.IsFocused, $"Escape in {mode}: focused {window.FocusManager?.GetFocusedElement()}, bar {input.IsEffectivelyVisible}");
                        Require(code.Editor.IsFocused, $"Native Escape must restore editor focus ({markdown}): focused {window.FocusManager?.GetFocusedElement()}, bar {input.IsEffectivelyVisible}, input {input.IsFocused}.");
                        Key(native, "x", 7, false);
                        await Task.Delay(50);
                        Require(code.Editor.Text.EndsWith("x second", StringComparison.Ordinal), "Native typing must replace the selected match.");
                        Command(native, "z", 6);
                        await Task.Delay(50);
                        Require(code.Editor.Text.EndsWith("needle second", StringComparison.Ordinal), "Native Command+Z must still undo editing.");
                        if (markdown) ((IDisposable)body).Dispose();
                        window.Content = null;
                    }
                    Console.WriteLine("PASS native AppKit editor Find: code, Markdown Source/Split, focus, selection, next, Escape, typing and menu undo");
                    desktop.Shutdown(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
            });
            base.OnFrameworkInitializationCompleted();
        }
    }
    private static async Task Until(Func<bool> condition, string operation)
    {
        var deadline = Awake.Now.AddSeconds(5);
        while (!condition() && Awake.Now < deadline) await Task.Delay(20);
        Require(condition(), "Native editor input timed out: " + operation);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("TerminalEvents", EntryPoint = "sr_texture_content")] private static extern nint Content([MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_activate")] private static extern void Activate(nint view);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_ready")][return: MarshalAs(UnmanagedType.I1)] private static extern bool Ready(nint view);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_key")] private static extern void Key(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, ushort code, [MarshalAs(UnmanagedType.I1)] bool control);
    [DllImport("TerminalEvents", EntryPoint = "sr_check_command")] private static extern void Command(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, ushort code);
}