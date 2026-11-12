#!/usr/bin/env dotnet
// Smoke test of the headless image: starts it on a small fixture workspace, sends initialize, notifications/initialized and
// tools/list as JSON lines on stdin, and checks that stdout holds exactly the two replies, as valid JSON-RPC, with the expected tools.
// Needs docker. Usage: dotnet run --file scripts/container-smoke.cs -- <image>
#:include lib/Tasks.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;

if (args.Length < 1 || args[0].Length == 0) { Console.Error.WriteLine("container-smoke: usage: container-smoke.cs <image>"); return 1; }
var tmp = Directory.CreateTempSubdirectory().FullName;
try { return Smoke(args[0], tmp); }
finally { Tasks.DeleteAll(tmp); }

static int Smoke(string image, string tmp)
{
    // The container user is uid 1000, so the fixture must be readable by anyone.
    var workspace = Path.Combine(tmp, "workspace");
    Directory.CreateDirectory(workspace);
    File.WriteAllText(Path.Combine(workspace, "README.md"), "hello\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(workspace, (UnixFileMode)0b111_111_111);
        File.SetUnixFileMode(Path.Combine(workspace, "README.md"), (UnixFileMode)0b110_110_110);
    }

    var input = string.Join('\n',
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
        """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""") + "\n";
    var info = new ProcessStartInfo(Tasks.Resolve("docker"))
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
    };
    foreach (var argument in new[] { "run", "-i", "--rm", "--init", "-v", $"{workspace}:/workspace", image }) info.ArgumentList.Add(argument);
    string output, error;
    int exit;
    using (var child = Process.Start(info)!)
    {
        var outputTask = child.StandardOutput.ReadToEndAsync();
        var errorTask = child.StandardError.ReadToEndAsync();
        child.StandardInput.Write(input);
        child.StandardInput.Close();
        child.WaitForExit();
        (output, error, exit) = (outputTask.Result, errorTask.Result, child.ExitCode);
    }
    if (exit != 0)
    {
        Console.WriteLine($"container exited with {exit}");
        Console.Write(error);
        return 1;
    }

    int Fail(string reason)
    {
        Console.WriteLine($"smoke test failed: {reason}");
        Console.WriteLine("--- stdout");
        Console.Write(output);
        Console.WriteLine("--- stderr");
        Console.Write(error);
        return 1;
    }

    if (output.Count(c => c == '\n') != 2) return Fail("stdout must hold exactly two lines");
    var replies = new List<JsonElement>();
    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        JsonElement reply;
        try { reply = JsonDocument.Parse(line).RootElement; }
        catch (JsonException) { return Fail($"not a JSON-RPC result: {line}"); }
        if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
            || !reply.TryGetProperty("id", out _) || !reply.TryGetProperty("result", out _))
            return Fail($"not a JSON-RPC result: {line}");
        replies.Add(reply);
    }
    JsonElement? Result(int id) => replies.Where(reply => reply.GetProperty("id").ValueKind == JsonValueKind.Number && reply.GetProperty("id").GetInt32() == id).Select(reply => (JsonElement?)reply.GetProperty("result")).FirstOrDefault();
    static JsonElement? Member(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var member) ? member : null;

    var initialize = Result(1);
    if (Member(Member(initialize, "serverInfo"), "name") is not { ValueKind: JsonValueKind.String } name || name.GetString() != "tereditor") return Fail("initialize does not name the server");
    if (Member(initialize, "instructions") is not { ValueKind: JsonValueKind.String } instructions || instructions.GetString()!.Length == 0) return Fail("initialize has no instructions");
    if (Member(Result(2), "tools") is not { ValueKind: JsonValueKind.Array } tools) return Fail("tools/list has no tools");
    var names = tools.EnumerateArray().Select(tool => Member(tool, "name")?.GetString()).ToList();
    if (names.Any(n => n is null)) return Fail("tools/list has no tools");
    foreach (var want in new[] { "lsp_navigate", "lsp_diagnostics", "lsp_status", "blueprint_check" })
        if (!names.Contains(want)) return Fail($"tools/list lacks {want}");
    if (!names.Any(n => n!.StartsWith("spec_", StringComparison.Ordinal))) return Fail("tools/list has no spec_ tools");
    Console.WriteLine($"smoke test passed: {names.Count} tools");
    return 0;
}
