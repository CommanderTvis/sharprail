using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Ghostty.Avalonia;

[SupportedOSPlatform("macos")]
internal static class Program
{
    internal static string Renderer = "", Root = "";
    internal static FileStream? Commands;
    internal static void Send(string command) { Commands!.WriteByte((byte)command[0]); Commands.Flush(); }
    [STAThread]
    static void Main(string[] args)
    {
        Renderer = args[0]; Root = Path.GetFullPath(args[1]); Directory.CreateDirectory(Root);
        Trace.Start();
        AppBuilder.Configure<BenchApp>().UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Metal] })
            .StartWithClassicDesktopLifetime([]);
    }
    internal static async Task Until(Func<bool> condition, string description)
    {
        double end = Native.Now() + 45;
        while (!condition()) { if (Native.Now() > end) throw new Exception("Timeout: " + description); await Task.Delay(2); }
    }
    internal static async Task<object> Measure(string name, Action start, Func<bool> done)
    {
        Console.WriteLine("PHASE " + name);
        using var process = Process.GetCurrentProcess();
        process.Refresh(); double cpu = process.TotalProcessorTime.TotalSeconds, time = Native.Now();
        long peak = process.WorkingSet64;
        double footprint = Native.Footprint();
        start();
        while (!done())
        {
            if (Native.Now() - time > 60) throw new Exception("Workload timeout: " + name);
            await Task.Delay(20); process.Refresh(); peak = Math.Max(peak, process.WorkingSet64);
            footprint = Math.Max(footprint, Native.Footprint());
        }
        process.Refresh();
        return new { name, seconds = Native.Now() - time, cpuSeconds = process.TotalProcessorTime.TotalSeconds - cpu,
            rssMiB = process.WorkingSet64 / 1048576.0, sampledPeakRssMiB = peak / 1048576.0,
            footprintMiB = Native.Footprint(), sampledPeakFootprintMiB = footprint };
    }
}

[SupportedOSPlatform("macos")]
internal sealed class BenchApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Window { Title = "Ghostty renderer benchmark", Width = 960, Height = 540,
            WindowDecorations = WindowDecorations.None, Position = new PixelPoint(100, 100) };
        desktop.MainWindow = window;
        Dispatcher.UIThread.Post(async () =>
        {
            IDisposable? control = null;
            try
            {
                string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
                var launch = new GhosttyLaunch(Directory.GetCurrentDirectory(),
                    Quote(Path.Combine(AppContext.BaseDirectory, "workload")) + " " + Quote(Program.Root));
                Action focus;
                Func<string> screen;
                if (Program.Renderer == "native")
                {
                    var native = new GhosttyView(launch); control = native; window.Content = native;
                    focus = native.FocusTerminal; screen = native.ReadScreen;
                }
                else
                {
                    var texture = new GhosttyTextureView(launch); control = texture; window.Content = texture;
                    focus = texture.FocusTerminal; screen = texture.ReadScreen;
                }
                window.Show();
                await Program.Until(() => File.Exists(Path.Combine(Program.Root, "ready.json")) && screen().Contains("BENCH_READY"), "PTY ready");
                Program.Commands = new FileStream(Path.Combine(Program.Root, "commands"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                int id = Native.Window(window.Title!);
                await Program.Until(() => Native.Active() != 0, "window activation");
                await Task.Delay(500);
                focus();
                focus(); await Task.Delay(1500);
                if (Environment.GetEnvironmentVariable("BENCH_TRACE") == "1")
                {
                    Console.WriteLine("FOCUSED " + ((Control)control).IsKeyboardFocusWithin);
                    window.KeyDown += (_, e) => Console.WriteLine($"KEY {e.Key} {e.PhysicalKey} {e.KeySymbol} handled={e.Handled}");
                    window.TextInput += (_, e) => Console.WriteLine("TEXT " + e.Text);
                }
                // No capture process during idle, paced output or flood CPU/RSS measurements.
                Program.Send("0"); await Task.Delay(500);
                var phases = new List<object>();
                double idleEnd = Native.Now() + 3;
                phases.Add(await Program.Measure("idle", () => {}, () => Native.Now() >= idleEnd));
                phases.Add(await Program.Measure("paced", () => Program.Send("p"), () => File.Exists(Path.Combine(Program.Root, "paced.json"))));
                await Task.Delay(1000);
                if (control is GhosttyTextureView rendered && rendered.Frames < 100) throw new Exception("Insufficient presented texture frames: " + rendered.Frames);
                Trace.Save(Path.Combine(Program.Root, "metal.json"));
                phases.Add(await Program.Measure("snapshot", () => {}, () => true));
                foreach (var tool in new[] { "vmmap", "footprint" })
                {
                    var info = new ProcessStartInfo("/usr/bin/" + tool) { RedirectStandardOutput = true, RedirectStandardError = true };
                    if (tool == "footprint") { info.ArgumentList.Add("-v"); info.ArgumentList.Add("-w"); }
                    info.ArgumentList.Add(Environment.ProcessId.ToString());
                    using var probe = Process.Start(info)!;
                    var output = probe.StandardOutput.ReadToEndAsync(); var errors = probe.StandardError.ReadToEndAsync();
                    await probe.WaitForExitAsync();
                    File.WriteAllText(Path.Combine(Program.Root, tool + ".txt"), await output + await errors);
                }
                var result = new { presentedTextureFrames = (control as GhosttyTextureView)?.Frames, renderer = Program.Renderer, pid = Environment.ProcessId, scale = window.RenderScaling, phases };
                File.WriteAllText(Path.Combine(Program.Root, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("BENCH_DONE " + Program.Root);
                control.Dispose(); window.Content = null; desktop.Shutdown(0);
            }
            catch (Exception e) { Console.Error.WriteLine(e); control?.Dispose(); desktop.Shutdown(1); }
        });
        base.OnFrameworkInitializationCompleted();
    }
}
internal static class Native
{
    [DllImport("BenchCapture", EntryPoint="bench_now")] internal static extern double Now();
    [DllImport("BenchCapture", EntryPoint="bench_footprint")] internal static extern double Footprint();
    [DllImport("BenchCapture", EntryPoint="bench_window")] internal static extern int Window([MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("BenchCapture", EntryPoint="bench_active")] internal static extern int Active();
    [DllImport("BenchCapture", EntryPoint="bench_hz")] internal static extern int Hz();
    [DllImport("BenchCapture", EntryPoint="bench_click")] internal static extern void Click();
    [DllImport("BenchCapture", EntryPoint="bench_key")] internal static extern void Key([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("BenchCapture", EntryPoint="bench_capture_start")] internal static extern void CaptureStart();
    [DllImport("BenchCapture", EntryPoint="bench_capture_state")] internal static extern int CaptureState();
    [DllImport("BenchCapture", EntryPoint="bench_capture_frames")] internal static extern int CaptureFrames();
    [DllImport("BenchCapture", EntryPoint="bench_capture_stop")] internal static extern void CaptureStop();
    [DllImport("BenchCapture", EntryPoint="bench_target")] internal static extern double Target(int color);
    [DllImport("BenchCapture", EntryPoint="bench_result")] internal static extern int Result(out double display, out double arrival);
}

internal static class Trace {
 [DllImport("MetalTrace", EntryPoint="trace_start")] internal static extern void Start();
 [DllImport("MetalTrace", EntryPoint="trace_save")] internal static extern void Save([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
}
