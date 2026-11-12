using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SharpRail.Performance;

internal sealed partial class Runner
{
    public async Task Diagnose(string output)
    {
        RejectRuntimeOverrides();
        var artifact = Validate();
        await PrepareProbe();
        FreshOutput(output);
        var scratch = Path.Combine(output, "scratch");
        if (Directory.Exists(scratch)) throw new IOException("Choose a fresh diagnostic output directory.");
        Directory.CreateDirectory(scratch);
        Process? target = null;
        try
        {
            var fixture = Path.Combine(scratch, "workspace");
            var fixtureHash = await Fixture(fixture);
            var tools = Path.Combine(root, "performance");
            await Commands.Run(dotnet, ["tool", "restore"], tools);
            var env = new Dictionary<string, string>
            {
                ["SHARPRAIL_ROOT"] = fixture,
                ["SHARPRAIL_PROFILE"] = Path.Combine(scratch, "profile"),
                ["SHARPRAIL_STATE_DIR"] = Path.Combine(scratch, "host"),
                ["SHARPRAIL_REMOTE"] = "",
                ["SHARPRAIL_PERFORMANCE"] = "1",
                ["DOTNET_DiagnosticPorts"] = "",
                ["DOTNET_DefaultDiagnosticPortSuspend"] = "0"
            };
            var tracePath = Path.Combine(output, "startup.nettrace");
            await using var trace = new Child(dotnet, ["tool", "run", "dotnet-trace", "--", "collect", "--profile", "dotnet-common,dotnet-sampled-thread-time", "--duration", "00:00:00:15", "--output", tracePath, "--show-child-io", "--", Ui], tools, env, Path.Combine(output, "trace.log"), Probe);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ready = await trace.Wait("workspace-ready", timeout.Token);
            if (ready.Status is "error" or "failed") throw new IOException("Diagnostic target did not become ready: " + ready.Detail);
            var tree = JsonSerializer.Deserialize<MemorySnapshot>(await Commands.Run(Probe, ["memory", trace.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], token: timeout.Token), Data.Json)!;
            Data.Save(Path.Combine(output, "diagnostic-memory.json"), tree);
            var power = await Commands.Run("pmset", ["-g", "batt"]);
            target = tree.Processes.Select(row => Process.GetProcessById(row.Pid)).FirstOrDefault(process => process.ProcessName == "SharpRail.UI") ?? throw new IOException("Cannot identify the traced UI process.");
            var counters = await Commands.Run(dotnet, ["tool", "run", "dotnet-counters", "--", "collect", "--process-id", target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "--counters", "System.Runtime", "--duration", "00:00:00:05", "--format", "json", "--output", Path.Combine(output, "runtime-counters.json")], tools, timeout.Token);
            File.WriteAllText(Path.Combine(output, "counters.log"), counters);
            await trace.Process.WaitForExitAsync(timeout.Token);
            if (trace.Process.ExitCode != 0) throw new IOException("Trace collection failed; see trace.log.");
            var hot = await Commands.Run(dotnet, ["tool", "run", "dotnet-trace", "--", "report", tracePath, "topN", "-n", "40"], tools, timeout.Token);
            File.WriteAllText(Path.Combine(output, "hot-methods.txt"), hot);
            await Commands.Run(dotnet, ["tool", "run", "dotnet-trace", "--", "convert", tracePath, "--format", "Speedscope", "--output", Path.Combine(output, "startup")], tools, timeout.Token);
            var speedscope = Path.Combine(output, "startup.speedscope.json");
            Data.Save(Path.Combine(output, "diagnostic.json"), new { schema = 1, artifact, fixtureHash, ready, power, diagnosticMemory = "diagnostic-memory.json (instrumented run, immediately after readiness; not headline RAM)", toolVersion = "10.0.750501", scope = "Separate instrumented embedded launch; managed thread sampling, not CPU-only or native profiling. Counters begin after workspace readiness and cover five seconds. Not included in benchmark aggregates." });
            File.WriteAllText(Path.Combine(output, "report.html"), "<!doctype html><html lang=en><meta charset=utf-8><title>SharpRail startup diagnostics</title><style>body{font:14px/1.5 system-ui;max-width:1200px;margin:24px auto;padding:20px}svg{width:100%;height:auto}pre{white-space:pre-wrap;overflow-wrap:anywhere}text{font:11px system-ui}</style><h1>Instrumented startup diagnostics</h1><p>Power during this diagnostic: " + WebUtility.HtmlEncode(power) + "</p><p>Separate run: tracing may suspend startup and adds overhead. Managed thread sample weights include waiting; they are not precise CPU time. Native/GPU stacks and physical GPU residency are unavailable. Counters start after workspace readiness. Headline startup/RAM comparisons use the measure command without EventPipe diagnostics.</p>" + Flamegraph(speedscope) + "<h2>Hot managed methods</h2><pre>" + WebUtility.HtmlEncode(hot) + "</pre><p><a href=startup.nettrace>Raw nettrace</a> · <a href=startup.speedscope.json>Speedscope</a> · <a href=runtime-counters.json>Runtime counters</a> · <a href=diagnostic.json>Provenance</a> · <a href=diagnostic-memory.json>Instrumented process memory</a></p></html>");
        }
        finally
        {
            if (target is not null)
            {
                if (!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); }
                target.Dispose();
            }
            Directory.Delete(scratch, true);
        }
    }

    private sealed class FrameNode(string name)
    {
        public string Name { get; } = name;
        public double Weight;
        public Dictionary<int, FrameNode> Children { get; } = [];
    }
    internal static string Flamegraph(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var names = root.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(frame => frame.GetProperty("name").GetString() ?? "unknown").ToArray();
        var tree = new FrameNode("All managed thread samples");
        var depth = 0;
        foreach (var profile in root.GetProperty("profiles").EnumerateArray())
        {
            var unit = profile.GetProperty("unit").GetString();
            var multiplier = unit switch { "nanoseconds" => 1e-9, "microseconds" => 1e-6, "milliseconds" => 1e-3, _ => 1.0 };
            void Add(int[] ids, double weight)
            {
                if (weight <= 0) return;
                weight *= multiplier;
                var node = tree;
                node.Weight += weight;
                depth = Math.Max(depth, ids.Length);
                foreach (var id in ids)
                {
                    if (!node.Children.TryGetValue(id, out var child)) node.Children[id] = child = new(names[id]);
                    node = child;
                    node.Weight += weight;
                }
            }
            if (profile.GetProperty("type").GetString() == "sampled")
            {
                var samples = profile.GetProperty("samples").EnumerateArray().ToArray();
                var weights = profile.TryGetProperty("weights", out var values) ? values.EnumerateArray().Select(value => value.GetDouble()).ToArray() : Enumerable.Repeat(1.0, samples.Length).ToArray();
                for (var index = 0; index < samples.Length; index++) Add(samples[index].EnumerateArray().Select(value => value.GetInt32()).ToArray(), weights[index]);
            }
            else if (profile.GetProperty("type").GetString() == "evented")
            {
                var stack = new List<int>();
                var previous = profile.GetProperty("startValue").GetDouble();
                foreach (var entry in profile.GetProperty("events").EnumerateArray())
                {
                    var at = entry.GetProperty("at").GetDouble();
                    if (stack.Count > 0) Add([.. stack], at - previous);
                    previous = at;
                    var frame = entry.GetProperty("frame").GetInt32();
                    if (entry.GetProperty("type").GetString() == "O") stack.Add(frame);
                    else
                    {
                        if (stack.Count == 0 || stack[^1] != frame) throw new InvalidDataException("Unbalanced Speedscope stack.");
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
                if (stack.Count > 0) Add([.. stack], profile.GetProperty("endValue").GetDouble() - previous);
            }
        }
        if (tree.Weight <= 0) return "<p>No managed thread samples available.</p>";
        var height = (depth + 1) * 22;
        var svg = new StringBuilder($"<h2>Managed stack weights</h2><p>Hover frames for full names and inclusive sample weights.</p><svg viewBox=\"0 0 1200 {height}\" role=img aria-label=\"Managed thread stack flame graph\">");
        void Draw(FrameNode node, double x, double width, int level)
        {
            if (width < 0.3) return;
            var y = height - (level + 1) * 22;
            var name = WebUtility.HtmlEncode(node.Name);
            svg.Append($"<g><title>{name}: {(node.Weight / tree.Weight * 100).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}% inclusive sample weight</title><rect x=\"{x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\" y={y} width=\"{width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\" height=21 fill=\"hsl({25 + level * 7 % 45} 80% 70%)\" stroke=white stroke-width=.5/>");
            if (width > 100) svg.Append($"<text x=\"{(x + 4).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\" y={y + 15}>{WebUtility.HtmlEncode(node.Name[..Math.Min(node.Name.Length, Math.Max(1, (int)(width / 7) - 2))])}</text>");
            svg.Append("</g>");
            foreach (var child in node.Children.Values.OrderByDescending(child => child.Weight))
            {
                var childWidth = width * child.Weight / node.Weight;
                Draw(child, x, childWidth, level + 1);
                x += childWidth;
            }
        }
        Draw(tree, 0, 1200, 0);
        return svg.Append("</svg>").ToString();
    }
}