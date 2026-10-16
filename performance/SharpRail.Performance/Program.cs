using SharpRail.Performance;

var root = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(root, "SharpRail.slnx"))) root = Directory.GetParent(root)?.FullName ?? throw new IOException("Run inside the SharpRail checkout.");
var dotnet = Environment.GetEnvironmentVariable("DOTNET") ?? Path.Combine(root, ".tools/dotnet/dotnet");
var command = args.FirstOrDefault() ?? "help";
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 1; index < args.Length; index += 2)
{
    if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) throw new ArgumentException("Options use --name value pairs.");
    if (!options.TryAdd(args[index][2..], args[index + 1])) throw new ArgumentException("Duplicate option: " + args[index]);
}
string Option(string key, string fallback) => options.GetValueOrDefault(key, fallback);
int Number(string key, int fallback, int minimum)
{
    var number = int.Parse(Option(key, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
    return number >= minimum ? number : throw new ArgumentException(key + " is too small.");
}
var allowed = command switch
{
    "measure" => new[] { "output", "warmups", "repetitions", "modes", "memory-delay-ms" },
    "diagnose" => ["output"],
    "report" => ["output", "input", "baseline", "max-startup-regression-percent", "max-memory-regression-percent"],
    _ => Array.Empty<string>()
};
if (options.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown option for " + command + ": " + string.Join(", ", options.Keys.Except(allowed)));
var output = Path.GetFullPath(Option("output", Path.Combine(root, "artifacts/performance/" + command + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"))));
try
{
    if (command is "publish" or "measure" or "diagnose" && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Profiling currently supports macOS only.");
    var runner = new Runner(root, dotnet);
    switch (command)
    {
        case "publish": await runner.Publish(); break;
        case "measure":
            var modes = Option("modes", "embedded,remote").Split(',');
            if (modes.Length == 0 || modes.Distinct().Count() != modes.Length || modes.Any(mode => mode is not ("embedded" or "remote"))) throw new ArgumentException("Use --modes embedded,remote or one of those modes.");
            var result = await runner.Measure(output, Number("warmups", 1, 0), Number("repetitions", 10, 1), Number("memory-delay-ms", 5000, 0), modes);
            Console.WriteLine(Path.Combine(output, "report.html"));
            if (result.Samples.Any(sample => !sample.Success)) Environment.ExitCode = 1;
            break;
        case "diagnose": await runner.Diagnose(output); Console.WriteLine(Path.Combine(output, "report.html")); break;
        case "report":
            var input = Data.Read<Result>(Path.GetFullPath(Option("input", Path.Combine(output, "results.json"))));
            var baseline = options.TryGetValue("baseline", out var file) ? Data.Read<Result>(Path.GetFullPath(file)) : null;
            Directory.CreateDirectory(output);
            Report.Write(input, Path.Combine(output, "report.html"), baseline);
            Console.WriteLine(Path.Combine(output, "report.html"));
            double? Limit(string key)
            {
                if (!options.TryGetValue(key, out var text)) return null;
                if (baseline is null) throw new ArgumentException("Regression limits require --baseline.");
                var value = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                return double.IsFinite(value) && value >= 0 ? value : throw new ArgumentException("Regression limits must be finite nonnegative percentages.");
            }
            var startupLimit = Limit("max-startup-regression-percent");
            var memoryLimit = Limit("max-memory-regression-percent");
            if (baseline is not null)
            {
                var outcomes = Gates.Evaluate(input, baseline, startupLimit, memoryLimit);
                Data.Save(Path.Combine(output, "comparison.json"), outcomes);
                foreach (var outcome in outcomes)
                    Console.WriteLine($"{outcome.Status.ToUpperInvariant()} {outcome.Mode} {outcome.Metric}: baseline={outcome.BaselineMean}, current={outcome.CurrentMean}, change={outcome.ChangePercent}%, limit={outcome.LimitPercent}% — {outcome.Reason}");
                if (outcomes.Any(outcome => outcome.Status != "pass")) Environment.ExitCode = 1;
            }
            break;
        case "self-test": SelfTests.Run(); break;
        default:
            Console.WriteLine("""
                SharpRail macOS startup and initial RAM toolkit
                  performance/run.sh publish
                  performance/run.sh measure [--warmups 1] [--repetitions 10] [--modes embedded,remote] [--memory-delay-ms 5000] [--output PATH]
                  performance/run.sh diagnose [--output PATH]
                  performance/run.sh report --input results.json [--baseline baseline.json] [--output PATH]
                  performance/run.sh self-test
                Build/publish runs outside measurement. Reports default under artifacts/performance.
                """);
            break;
    }
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }