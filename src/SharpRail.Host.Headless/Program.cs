using System.Runtime.InteropServices;

using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Headless;
using SharpRail.Plugins.Api;

// stdout carries protocol frames only: keep the real stream for them and send anything a library prints to stderr.
var frames = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

const string Usage = "Usage: sharprail-headless mcp|check-config [--workspace <dir>] [--arm <name>] [--disable-tools <a,b>]\n" +
    "       sharprail-headless warm [server ...] [--workspace <dir>]   (installs the pinned runtimes and servers, default csharp; needs network)\n" +
    "       sharprail-headless languages   (prints the language servers this build serves, one per line: id, name, extensions)\n" +
    "check-config prints the effective .sharprail/mcp.toml configuration as JSON and exits 0, or 2 when it is invalid.\n" +
    "Serves MCP over stdio for one workspace: --workspace, else SHARPRAIL_ROOT, else the current directory.\n" +
    "State (settings, language server installs and sessions) lives in SHARPRAIL_STATE_DIR, else ~/.sharprail/headless.";

string? workspaceArgument = null;
var flags = new McpFlags();
var command = "mcp";
var warmServers = new List<string>();
for (var index = 0; index < args.Length; index++)
{
    var argument = args[index];
    if (argument is "--help" or "-h") { using var help = new StreamWriter(frames) { AutoFlush = true }; help.WriteLine(Usage); return 0; }
    if (flags.TryTake(args, ref index)) { }
    else if (argument == "--workspace" && index + 1 < args.Length) workspaceArgument = args[++index];
    else if (argument.StartsWith("--workspace=", StringComparison.Ordinal)) workspaceArgument = argument["--workspace=".Length..];
    else if (index == 0 && !argument.StartsWith('-')) command = argument;
    else if (command == "warm" && !argument.StartsWith('-')) warmServers.Add(argument);
    else return Fail($"Unknown argument: {argument}");
}
if (command is not ("mcp" or "warm" or "check-config" or "languages")) return Fail($"Unknown command: {command}");

// Language servers whose runtimes are all pinned downloads; the others need a toolchain this host cannot fetch (HeadlessLanguages.cs).
var roster = HeadlessLanguages.Roster(BuiltinPlugins.Headless, ManagedArtifacts.Platform());
var servers = HeadlessLanguages.Servers(roster);
if (command == "languages")
{
    using var output = new StreamWriter(frames) { AutoFlush = true };
    foreach (var spec in servers) output.WriteLine($"{spec.Id}\t{spec.Name}\t{string.Join(' ', spec.Extensions)}");
    return 0;
}

var fromEnvironment = Environment.GetEnvironmentVariable("SHARPRAIL_ROOT");
var workspace = Path.GetFullPath(workspaceArgument ?? (string.IsNullOrEmpty(fromEnvironment) ? Directory.GetCurrentDirectory() : fromEnvironment));
workspace = Path.TrimEndingDirectorySeparator(workspace);
if (!Directory.Exists(workspace)) return Fail($"The workspace {workspace} is not a directory.");
McpConfig config;
try { config = McpConfig.Load(workspace, flags, Environment.GetEnvironmentVariable); }
catch (McpConfigException error) { return Fail(error.Message); }
if (command == "check-config")
{
    using var output = new StreamWriter(frames) { AutoFlush = true };
    output.WriteLine(config.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
foreach (var warning in config.Warnings) Console.Error.WriteLine("sharprail-headless: " + McpConfig.FileName + ": " + warning);
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

var plugins = new PluginRuntime(new PluginHostSeams { StateDirectory = stateDirectory, State = state, Builtins = roster, Kind = HostKind.Headless });
await plugins.Start();
if (command == "warm") return await WarmAsync();
var language = LanguageMcpTools.Create(state, workspace);
// One workspace per process: a tool's optional "workspace" argument cannot name another, so it is dropped.
// Plugin tools are read per request, so a plugin turned off stops serving its tools at once.
ToolSwitches switches = new(config, null);
IReadOnlyList<McpServer.McpTool> Tools() =>
    switches.Apply([.. plugins.McpTools(null, workspace).Concat(language).Select(tool => tool with { Call = (arguments, token) => { arguments.Remove("workspace"); return tool.Call(arguments, token); } })]);
// The tool switches and the opt-in call log (McpConfig.cs, ToolSwitches.cs, CallLog.cs). The log needs the offered names, so the switches exist first.
CallLog? callLog = null;
try
{
    var offered = Tools().Select(tool => tool.Name).ToArray();
    if (config.Log.Enabled)
    {
        callLog = CallLog.Start(config, Console.Error, offered);
        switches = new ToolSwitches(config, callLog);
        Console.Error.WriteLine($"sharprail-headless: call log {callLog.Path}");
    }
}
catch (McpConfigException error)
{
    await plugins.DisposeAsync();
    return Fail(error.Message);
}

using var stop = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); });
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
Console.Error.WriteLine($"sharprail-headless: serving {workspace}, state in {stateDirectory}");
var Instructions = ToolSwitches.Instructions(Tools(), HeadlessLanguages.Summary(servers));
await new StdioMcp(Console.OpenStandardInput(), frames, Tools, Instructions, Console.Error).RunAsync(stop);
Console.Error.WriteLine("sharprail-headless: stopping");
if (callLog is not null) await callLog.DisposeAsync();
// Plugins stop first, then the language servers they use.
await plugins.DisposeAsync();
await state.StopLanguagesAsync();
return 0;

async Task<int> WarmAsync()
{
    using var cancel = new CancellationTokenSource();
    using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; cancel.Cancel(); });
    using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancel.Cancel(); });
    if (warmServers.Count == 0) warmServers.Add("csharp");
    Console.Error.WriteLine($"sharprail-headless: installing {string.Join(", ", warmServers)} into {stateDirectory} (progress: {SharpRail.Host.Abstractions.CodeIntelligenceLog.Path})");
    try
    {
        foreach (var id in warmServers)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!await state.WarmLanguageServerAsync(id, workspace, cancel.Token)) return Fail($"No language server named {id}.");
            Console.Error.WriteLine($"sharprail-headless: {id} ready in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0} s");
        }
    }
    catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or InvalidOperationException)
    {
        return Fail("warm failed: " + error.Message);
    }
    finally { await plugins.DisposeAsync(); await state.StopLanguagesAsync(); }
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine("sharprail-headless: " + message);
    return 2;
}