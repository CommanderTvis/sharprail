using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

using Avalonia.Controls;

using Ghostty.Avalonia;

namespace SharpRail.Checks;

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
                foreach (var view in views.Skip(1)) view.IsVisible = false;
                await Record($"{size} c 7 hidden");
                if (width != Sizes[0].Width) continue;
                foreach (var view in views.Skip(1)) panel.Children.Remove(view);
                await Record($"{size} d 7 detached");
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

        Console.WriteLine($"{"step",-26} {"IOSurface MB",13} {"graphics MB",12} {"footprint MB",13}");
        foreach (var (step, surface, graphics, footprint) in rows)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{step,-26} {surface,13:0.0} {graphics,12:0.0} {footprint,13:0.0}"));
        Console.WriteLine("PASS native surface memory measured");
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