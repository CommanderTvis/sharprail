using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

using Avalonia.Controls;

using Ghostty.Avalonia;

namespace Tereditor.Checks;

/// <summary>
/// Measures this process's IOSurface and physical footprint (macOS <c>footprint</c>) as Metal terminals are shown,
/// hidden, detached and disposed. It prints a table and asserts only that the numbers could be read.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class NativeSurfaceMemoryMeasure
{
    private const int Terminals = 8;
    private static readonly (int Width, int Height)[] Sizes = [(1400, 900), (900, 600)];

    public static async Task Run(Window window)
    {
        var rows = new List<(string Step, double Surface, double Graphics, double Footprint)>();
        async Task Record(string step)
        {
            await Task.Delay(2000);
            var (surface, graphics, footprint) = await Footprint();
            rows.Add((step, surface, graphics, footprint));
        }

        await Verify(window);
        foreach (var (width, height) in Sizes)
        {
            window.Width = width;
            window.Height = height;
            var size = $"{width}x{height}";
            var panel = new Panel();
            window.Content = panel;
            window.Show();
            await Task.Delay(500);
            if (width == Sizes[0].Width) await Record($"{size} a baseline");
            var views = new List<GhosttyTextureView>();
            try
            {
                for (var index = 0; index < Terminals; index++)
                {
                    var view = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), ExternalIo: new GhosttyExternalIo()));
                    views.Add(view);
                    panel.Children.Add(view);
                }
                foreach (var view in views) await view.Ready.WaitAsync(TimeSpan.FromSeconds(30));
                for (var index = 0; index < views.Count; index++)
                    views[index].WriteOutput(Encoding.UTF8.GetBytes("\e[2J\e[H" + string.Concat(Enumerable.Range(0, 40).Select(line => $"terminal {index} line {line}\r\n"))));
                await Record($"{size} b 8 visible");
                foreach (var view in views) view.ReclaimDelay = TimeSpan.FromHours(1);
                foreach (var view in views.Skip(1)) view.IsVisible = false;
                await Record($"{size} c 7 hidden, inside delay");
                foreach (var view in views.Skip(1)) view.ReclaimDelay = TimeSpan.FromSeconds(1);
                await Record($"{size} c2 7 hidden, reclaimed");
                Require(views.Skip(1).All(view => view.Reclaims == 1), "Hidden terminals did not reclaim once.");
                var reclaimed = rows[^1];
                var hiddenRow = rows[^2];
                Require(reclaimed.Surface < hiddenRow.Surface * 0.6, $"Reclaim left {reclaimed.Surface:0.0} MB of IOSurface of {hiddenRow.Surface:0.0} MB.");
                foreach (var view in views.Skip(1)) view.IsVisible = true;
                for (var round = 0; round < 4; round++)
                {
                    for (var index = 0; index < views.Count; index++) views[index].WriteOutput(Encoding.UTF8.GetBytes($"shown again {index} {round}\r\n"));
                    await Task.Delay(300);
                }
                await Record($"{size} c3 7 shown again");
                for (var index = 0; index < views.Count; index++)
                    Require(views[index].ReadScreen().Contains($"shown again {index} 3", StringComparison.Ordinal), $"Terminal {index} lost content across reclaim.");
                if (width != Sizes[0].Width) continue;
                foreach (var view in views.Skip(1)) view.IsVisible = false;
                foreach (var view in views.Skip(1)) panel.Children.Remove(view);
                await Record($"{size} d 7 detached, reclaimed");
                foreach (var view in views.Skip(1)) view.Dispose();
                await Record($"{size} e 7 disposed");
            }
            finally
            {
                window.Content = null;
                foreach (var view in views) view.Dispose();
            }
            await Task.Delay(1000);
        }

        Console.WriteLine($"{"step",-34} {"IOSurface MB",13} {"graphics MB",12} {"footprint MB",13}");
        foreach (var (step, surface, graphics, footprint) in rows)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{step,-34} {surface,13:0.0} {graphics,12:0.0} {footprint,13:0.0}"));
        Console.WriteLine("PASS native surface memory measured");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    // Hiding past the delay reclaims GPU targets without touching the grid, the PTY, the screen or the posted frames.
    private static async Task Verify(Window window)
    {
        window.Width = 900;
        window.Height = 600;
        using var terminal = new GhosttyTextureView(new GhosttyLaunch(Directory.GetCurrentDirectory(), "/bin/sh")) { ReclaimDelay = TimeSpan.FromSeconds(1) };
        window.Content = terminal;
        window.Show();
        await terminal.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        async Task<string> PtySize(string marker)
        {
            terminal.Type($"echo {marker}; stty size\n");
            string? size = null;
            await Until(() =>
            {
                var lines = terminal.ReadScreen().Split('\n').Select(line => line.Trim()).ToList();
                var at = lines.LastIndexOf(marker);
                size = at >= 0 && at + 1 < lines.Count && Regex.IsMatch(lines[at + 1], @"^\d+ \d+$") ? lines[at + 1] : null;
                return size is not null;
            }, "The PTY did not report its size.");
            return size!;
        }
        var grid = terminal.Size;
        var texture = terminal.TextureSize;
        var pty = await PtySize("SIZE_BEFORE");
        Require(pty == $"{grid.Rows} {grid.Columns}", $"PTY {pty} differs from the grid {grid.Rows}x{grid.Columns}.");

        terminal.IsVisible = false;
        terminal.Type("echo hidden-$((20+1))\n");
        await Task.Delay(2500);
        Require(terminal.Reclaims == 1, "A terminal hidden past the delay did not reclaim exactly once.");
        Require(terminal.RetainedTextureCount == 1, "Reclaim must keep exactly the last presented frame.");
        var posts = terminal.UiNotifications;
        terminal.Type("echo again-$((30+2))\n");
        await Task.Delay(500);
        Require(terminal.UiNotifications == posts, "A reclaimed hidden terminal posted frames.");
        Require(terminal.Size == grid, "Hiding changed the grid.");

        var frames = terminal.Frames;
        terminal.IsVisible = true;
        await Until(() => terminal.Frames > frames, "No frame was drawn after showing a reclaimed terminal.");
        Require(terminal.Size == grid && terminal.TextureSize == texture, "Showing changed the grid or the texture size.");
        var screen = terminal.ReadScreen();
        Require(screen.Contains("hidden-21", StringComparison.Ordinal) && screen.Contains("again-32", StringComparison.Ordinal), "Output written while hidden is missing after show.");
        Require(await PtySize("SIZE_AFTER") == pty, "The PTY size changed across hide, reclaim and show.");

        // A flip shorter than the delay never reclaims.
        terminal.ReclaimDelay = TimeSpan.FromSeconds(2);
        for (var flip = 0; flip < 3; flip++)
        {
            terminal.IsVisible = false;
            await Task.Delay(400);
            terminal.IsVisible = true;
            await Task.Delay(200);
        }
        await Task.Delay(3000);
        Require(terminal.Reclaims == 1, "A quick hide and show reclaimed.");
        Console.WriteLine($"PASS reclaim: grid {grid.Columns}x{grid.Rows}, PTY '{pty}' and texture {texture.Width}x{texture.Height} unchanged; hidden output kept; no posts while hidden; no reclaim inside the delay");
        window.Content = null;
    }

    // `footprint` lists dirty/clean sizes per category; IOSurface and the owned-graphics lines are the GPU side.
    private static async Task<(double Surface, double Graphics, double Total)> Footprint()
    {
        var info = new ProcessStartInfo("/usr/bin/footprint", Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
        { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        double surface = 0, graphics = 0, total = -1;
        foreach (var line in output.Split('\n'))
        {
            if (TotalLine().Match(line) is { Success: true } totalMatch) total = Megabytes(totalMatch.Groups[1].Value, totalMatch.Groups[2].Value);
            else if (CategoryLine().Match(line) is { Success: true } category)
            {
                var value = Megabytes(category.Groups[1].Value, category.Groups[2].Value);
                var name = category.Groups[3].Value;
                if (name.StartsWith("IOSurface", StringComparison.Ordinal)) surface += value;
                else if (name.Contains("graphics", StringComparison.OrdinalIgnoreCase) || name.StartsWith("IOAccelerator", StringComparison.Ordinal)) graphics += value;
            }
        }
        if (total < 0) throw new InvalidOperationException("footprint printed no total: " + output);
        return (surface, graphics, total);
    }

    private static double Megabytes(string number, string unit) => double.Parse(number, CultureInfo.InvariantCulture) * unit switch
    {
        "B" => 1.0 / 1048576,
        "KB" => 1.0 / 1024,
        "MB" => 1,
        "GB" => 1024,
        _ => throw new InvalidOperationException("Unknown unit " + unit),
    };

    [GeneratedRegex(@"Footprint:\s+([\d.]+)\s+(B|KB|MB|GB)\b")]
    private static partial Regex TotalLine();

    // The first (dirty) column of a category row: "  648 MB  0 B  0 B  29  IOSurface".
    [GeneratedRegex(@"^\s*([\d.]+)\s+(B|KB|MB|GB)\s+[\d.]+\s+(?:B|KB|MB|GB)\s+[\d.]+\s+(?:B|KB|MB|GB)\s+\d+\s+(.+?)\s*$")]
    private static partial Regex CategoryLine();
}