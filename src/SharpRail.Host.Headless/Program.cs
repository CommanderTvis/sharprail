using System.Runtime.InteropServices;

using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Headless;

// stdout carries protocol frames only: keep the real stream for them and send anything a library prints to stderr.
var frames = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

const string Usage = "Usage: sharprail-headless mcp [--workspace <dir>]\n" +
    "Serves MCP over stdio for one workspace: --workspace, else SHARPRAIL_ROOT, else the current directory.\n" +
    "State (settings, language server installs and sessions) lives in SHARPRAIL_STATE_DIR, else ~/.sharprail/headless.";

string? workspaceArgument = null;
var command = "mcp";
for (var index = 0; index < args.Length; index++)
{
    var argument = args[index];
    if (argument is "--help" or "-h") { using var help = new StreamWriter(frames) { AutoFlush = true }; help.WriteLine(Usage); return 0; }
    if (argument == "--workspace" && index + 1 < args.Length) workspaceArgument = args[++index];
    else if (argument.StartsWith("--workspace=", StringComparison.Ordinal)) workspaceArgument = argument["--workspace=".Length..];
    else if (index == 0 && !argument.StartsWith('-')) command = argument;
    else return Fail($"Unknown argument: {argument}");
}
if (command != "mcp") return Fail($"Unknown command: {command}");

var fromEnvironment = Environment.GetEnvironmentVariable("SHARPRAIL_ROOT");
var workspace = Path.GetFullPath(workspaceArgument ?? (string.IsNullOrEmpty(fromEnvironment) ? Directory.GetCurrentDirectory() : fromEnvironment));
workspace = Path.TrimEndingDirectorySeparator(workspace);
if (!Directory.Exists(workspace)) return Fail($"The workspace {workspace} is not a directory.");
var stateDirectory = Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") is { Length: > 0 } configured ? Path.GetFullPath(configured)
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail", "headless");
HostStateStore state;
try
{
    Directory.CreateDirectory(stateDirectory);
    state = new HostStateStore(stateDirectory);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    return Fail($"The state directory {stateDirectory} cannot be used: {error.Message}");
}

var plugins = new PluginRuntime(new PluginHostSeams { StateDirectory = stateDirectory, State = state, Builtins = BuiltinPlugins.Headless });
await plugins.Start();
var language = LanguageMcpTools.Create(state, workspace);
// One workspace per process: a tool's optional "workspace" argument cannot name another, so it is dropped.
// Plugin tools are read per request, so a plugin turned off stops serving its tools at once.
IReadOnlyList<McpServer.McpTool> Tools() =>
    [.. plugins.McpTools(null, workspace).Concat(language).Select(tool => tool with { Call = (arguments, token) => { arguments.Remove("workspace"); return tool.Call(arguments, token); } })];

using var stop = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); });
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
Console.Error.WriteLine($"sharprail-headless: serving {workspace}, state in {stateDirectory}");
const string Instructions = "SharpRail's language, spec and Blueprint tools for the one workspace this server runs in. Paths are relative to its root. The optional workspace argument is ignored. Use lsp_navigate instead of grep to find symbols, implementations and references, and lsp_diagnostics to check edited files.";
await new StdioMcp(Console.OpenStandardInput(), frames, Tools, Instructions, Console.Error).RunAsync(stop);
Console.Error.WriteLine("sharprail-headless: stopping");
// Plugins stop first, then the language servers they use.
await plugins.DisposeAsync();
await state.StopLanguagesAsync();
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine("sharprail-headless: " + message);
    return 2;
}