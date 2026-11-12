#!/usr/bin/env dotnet
// Runs a command in a GitHub Actions step and, when it fails, says why on the run page: an annotation with the first
// error lines and the step summary with the log's tail. The output is also kept in $RUNNER_TEMP/tereditor-ci/ for the
// job's diagnostics artifact. Outside GitHub Actions it just runs the command.
// Usage: scripts/mise x -- dotnet run --file scripts/ci-run.cs -- "<step title>" <command> [arguments...]
// (a leading `--` before the command is dropped). The pinned SDK is needed to run this file, so the workflows start it through mise.
#:include lib/Tasks.cs
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 2) { Console.Error.WriteLine("usage: ci-run.cs -- \"<step title>\" <command> [arguments...]"); return 2; }
var title = args[0];
var command = args.Skip(1).ToList();
if (command[0] == "--" && command.Count > 1) command.RemoveAt(0);
if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
{
    try { return Tasks.Run(command[0], command.Skip(1)); }
    catch (TaskFailure failure) { Console.Error.WriteLine(failure.Message); return failure.Code; }
}

var directory = Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } temp ? temp : Path.GetTempPath(), "tereditor-ci");
Directory.CreateDirectory(directory);
var log = Path.Combine(directory, $"{Regex.Replace(title, "[^A-Za-z0-9]", "-")}-{Environment.ProcessId}.log");
var code = Tee(command, log);
if (code != 0) Diagnose(title, log, code);
return code;

// Runs the command with its two output streams merged into this process's output and the log.
static int Tee(List<string> command, string log)
{
    var info = new ProcessStartInfo(Tasks.Resolve(command[0])) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in command.Skip(1)) info.ArgumentList.Add(argument);
    using var file = File.Create(log);
    using var console = Console.OpenStandardOutput();
    var gate = new object();
    void Write(byte[] buffer, int count)
    {
        lock (gate)
        {
            console.Write(buffer, 0, count);
            console.Flush();
            file.Write(buffer, 0, count);
        }
    }
    Process child;
    try { child = Process.Start(info)!; }
    catch (System.ComponentModel.Win32Exception exception)
    {
        var message = Encoding.UTF8.GetBytes($"{command[0]}: {exception.Message}\n");
        Write(message, message.Length);
        return 127;
    }
    using (child)
    {
        var pumps = new[] { child.StandardOutput.BaseStream, child.StandardError.BaseStream }.Select(stream => Task.Run(() =>
        {
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) Write(buffer, count);
        })).ToArray();
        child.WaitForExit();
        Task.WaitAll(pumps);
        return child.ExitCode;
    }
}

// Turns a failed step's log into annotations and a step-summary section.
static void Diagnose(string title, string log, int code)
{
    if (!File.Exists(log)) return;
    var lines = File.ReadAllText(log, Encoding.UTF8).Split('\n').ToList();
    if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    // MSBuild: "path(line,col): error CODE: message" becomes a file annotation; other error lines a plain one.
    var error = new Regex(@": error [A-Za-z]*[0-9]+:|(^|[^A-Za-z])(error|ERROR|fatal|FAIL|FAILED)[: ]|NETSDK[0-9]+|MSB[0-9]{4}|Unhandled exception");
    var noise = new Regex(@"(^|[^0-9])0 Error\(s\)|Warning\(s\)|Time Elapsed|error: .*retry|^::");
    var errors = lines.Where(line => error.IsMatch(line) && !noise.IsMatch(line)).Distinct().Take(12).ToList();
    static string Escape(string text) => text.Replace("%", "%25").Replace("\r", "");
    var located = new Regex(@"^ *(?<file>.+)\((?<row>[0-9]+),(?<col>[0-9]+)\): error (?<message>.*)$");
    foreach (var line in errors.Where(line => line.Length > 0))
    {
        var match = located.Match(line.Replace("\r", ""));
        if (match.Success) Console.Out.Write($"::error file={match.Groups["file"].Value},line={match.Groups["row"].Value},col={match.Groups["col"].Value},title={title}::{Escape(match.Groups["message"].Value)}\n");
        else Console.Out.Write($"::error title={title}::{Escape(line)}\n");
    }
    Console.Out.Write($"::error title={title} failed::Step \"{title}\" exited with code {code}; see the run summary for the first errors and the log tail.\n");
    Console.Out.Flush();
    if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } summary) return;
    var text = new StringBuilder($"### Step failed: {title} (exit code {code})\n\n");
    if (errors.Count > 0) text.Append($"First error lines:\n\n```\n{string.Join('\n', errors)}\n```\n\n");
    else text.Append("No error line matched; the log tail below is the best lead.\n\n");
    text.Append("<details><summary>Last 40 lines of the log</summary>\n\n```\n");
    foreach (var line in lines.TakeLast(40)) text.Append(line.Replace("```", "'''")).Append('\n');
    text.Append("```\n</details>\n\n");
    File.AppendAllText(summary, text.ToString(), new UTF8Encoding(false));
}
