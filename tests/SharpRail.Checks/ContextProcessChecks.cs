using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using SharpRail.Host.Core.Context;

namespace SharpRail.Checks;

internal static partial class ContextChecks
{
    private static async Task NativeWorkflowAsync(string root)
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Context native-command fixtures: the pinned integration has no Windows artifact."); return; }
        var workspace = Path.Combine(root, "native-workflow"); Directory.CreateDirectory(workspace);
        var executable = Path.Combine(workspace, "context-fixture.mjs");
        await File.WriteAllTextAsync(executable, """
            #!/usr/bin/env node
            import readline from 'node:readline';
            import fs from 'node:fs';
            const args=process.argv.slice(2), mode=args[0];
            fs.appendFileSync('invocations.jsonl',JSON.stringify(args)+'\n');
            if(mode==='index'){
              process.stdout.write(JSON.stringify({progress:50,token:'fixture-native-private'})+'\n');
              if(fs.existsSync('hold-index'))setInterval(()=>{},1000);
              else process.stdout.write('{"indexed":true}\n');
            }else if(mode==='status')process.stdout.write('{"indexed":true}\n');
            else if(mode==='mcp'){
              if(fs.existsSync('requires-login')){process.stdout.write('You need to log in to JetBrains AI Platform first\n');process.exit(1);}
              let initialize,root;
              const send=m=>process.stdout.write(JSON.stringify({jsonrpc:'2.0',...m})+'\n');
              readline.createInterface({input:process.stdin}).on('line',line=>{
                const m=JSON.parse(line);
                if(m.method==='initialize'){initialize=m.id;send({id:100,method:'roots/list'});}
                else if(m.id===100){root=m.result.roots;send({id:initialize,result:{protocolVersion:'2025-06-18',capabilities:{tools:{}}}});}
                else if(m.method==='tools/list')send({id:m.id,result:{tools:[{name:'code_search',inputSchema:{type:'object',properties:{text:{type:'string'},pathFilter:{type:'string'},limit:{type:'integer'}},required:['text']}}]}});
                else if(m.method==='tools/call'){
                  const a=m.params.arguments;
                  if(a.text==='cancel'){fs.writeFileSync('search-entered','');return;}
                  if(a.text==='needs-auth'){send({id:m.id,result:{isError:true,content:[{type:'text',text:'You need to log in to JetBrains AI Platform first'}]}});return;}
                  if(a.text==='large'){send({id:m.id,result:{content:[{type:'text',text:'x'.repeat(50000)}]}});return;}
                  process.stderr.write('Authorization: Bearer fixture-mcp-private\n');
                  send({id:m.id,result:{content:[{type:'text',text:JSON.stringify({root,...a})}]}});
                }
              });
            }else process.exit(22);
            """);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var logs = new ConcurrentQueue<string>(); var authorizations = 0;
        await using var runtime = new ContextRuntime(executable, workspace, logs.Enqueue, token => { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref authorizations); return Task.CompletedTask; });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await runtime.IndexAsync(deadline.Token);
        Require((await runtime.StatusAsync(deadline.Token)).Contains("indexed", StringComparison.Ordinal), "Context status command was not usable.");
        Require((await runtime.PrepareLoginAsync(deadline.Token)).EndsWith("' login", StringComparison.Ordinal), "Sign-in command did not preserve the pinned executable.");
        var data = JsonNode.Parse(await runtime.SearchAsync("locate behavior", "src", 7, deadline.Token))!;
        Require(data["text"]!.ToString() == "locate behavior" && data["pathFilter"]!.ToString() == "src" && data["limit"]!.GetValue<int>() == 7 &&
            data["root"]!.AsArray().Count == 1 && data["root"]![0]!["uri"]!.ToString() == new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri,
            "Native MCP schema adaptation or single-root initialization failed.");
        await runtime.SearchAsync("same warm session", ".", 2, deadline.Token);
        Require(authorizations == 4, "A warm search unnecessarily reprovisioned/restarted the server or a fresh execution skipped authorization.");
        Require((await runtime.SearchAsync("large", ".", 2, deadline.Token)).Length < 31_000, "Context result output was not bounded.");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await runtime.SearchAsync("cancel", ".", 2, cancelled.Token); throw new InvalidOperationException("Context MCP ignored cancellation."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        await runtime.SearchAsync("reconnect", ".", 2, deadline.Token);
        Require(authorizations == 5, "MCP cancellation failed to restart only its owned connection through the terms guard.");
        try { await runtime.SearchAsync("needs-auth", ".", 2, deadline.Token); throw new InvalidOperationException("A warm MCP authentication failure appeared successful."); }
        catch (ContextLoginRequiredException) { }
        await File.WriteAllTextAsync(Path.Combine(workspace, "hold-index"), "");
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            try { await runtime.IndexAsync(cancelled.Token); throw new InvalidOperationException("Context index ignored cancellation."); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        File.Delete(Path.Combine(workspace, "hold-index"));
        await Task.Delay(50, deadline.Token);
        Require(logs.Any(line => line.Contains("progress", StringComparison.Ordinal)) && logs.All(line => !line.Contains("fixture-native-private", StringComparison.Ordinal) && !line.Contains("fixture-mcp-private", StringComparison.Ordinal)),
            "Native Context progress was lost or credentials entered logs.");
        var calls = (await File.ReadAllLinesAsync(Path.Combine(workspace, "invocations.jsonl"))).Select(line => JsonNode.Parse(line)!.AsArray()).ToArray();
        Require(calls.All(call => call[0]!.ToString() is "index" or "status" or "mcp") && calls.Where(call => call[0]!.ToString() != "mcp").All(call => call.Any(arg => arg?.ToString() == "--project-path=" + workspace)),
            "Context changed global setup/watch state or omitted its fixed workspace argument.");
        File.Delete(Path.Combine(workspace, "search-entered"));
        var disposing = new ContextRuntime(executable, workspace, logs.Enqueue, _ => Task.CompletedTask);
        var waiting = disposing.SearchAsync("cancel", ".", 2, default);
        while (!File.Exists(Path.Combine(workspace, "search-entered"))) await Task.Delay(10, deadline.Token);
        await disposing.DisposeAsync().AsTask().WaitAsync(deadline.Token);
        await disposing.DisposeAsync();
        try { await waiting; throw new InvalidOperationException("Runtime disposal did not cancel a caller without a deadline."); }
        catch (OperationCanceledException) { }
        await File.WriteAllTextAsync(Path.Combine(workspace, "requires-login"), "");
        await using var signedOut = new ContextRuntime(executable, workspace, logs.Enqueue, _ => Task.CompletedTask);
        try { await signedOut.SearchAsync("query", ".", 2, deadline.Token); throw new InvalidOperationException("Signed-out native MCP appeared ready."); }
        catch (ContextLoginRequiredException) { }
        Console.WriteLine("PASS Context native-command fixtures: pinned launch, fresh-execution authorization, warm MCP schema/roots, progress, redaction, bounded results, cancellation, reconnect and login state");
    }
}