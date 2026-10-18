using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.AirContext.Host;

internal static partial class ContextOutput
{
    [GeneratedRegex("""(?i)(bearer\s+|["']?authorization["']?\s*[=:]\s*["']?(?:bearer\s+)?|["']?(?:[\w-]*(?:token|secret|password)|credential|api[-_]?key)["']?\s*[=:]\s*["']?)[^\s,}"']+""")]
    private static partial Regex Secrets();
    [GeneratedRegex(@"(https?://[^\s?]+)\?[^\s]+")]
    private static partial Regex UrlQueries();
    internal static bool RequiresLogin(string text) => text.Contains("need to log in", StringComparison.OrdinalIgnoreCase) || text.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) || text.Contains("authentication required", StringComparison.OrdinalIgnoreCase) || text.Contains("sign in again", StringComparison.OrdinalIgnoreCase);
    internal static string Sanitize(string text) => UrlQueries().Replace(Secrets().Replace(text, "$1[redacted]"), "$1?[redacted]");
}

internal sealed class ContextRuntime(string executable, string workspace, Action<string> log, Func<CancellationToken, Task> authorize) : IContextRuntime
{
    private ContextMcpConnection? mcp;
    private readonly SemaphoreSlim search = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private JsonObject? searchSchema;
    private int disposed;

    internal static ProcessStartInfo Start(string executable, string workspace, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public async Task<string> PrepareLoginAsync(CancellationToken token)
    {
        await authorize(token);
        return "'" + executable.Replace("'", "'\\''", StringComparison.Ordinal) + "' login";
    }

    public async Task LogoutAsync(CancellationToken token)
    {
        await authorize(token);
        // A live MCP session holds the old login; the next search starts a fresh one.
        await search.WaitAsync(token);
        try { if (mcp is { } connection) { mcp = null; searchSchema = null; await connection.DisposeAsync(); } }
        finally { search.Release(); }
        await RunAsync(["logout"], TimeSpan.FromMinutes(1), token);
    }

    public async Task IndexAsync(CancellationToken token)
    {
        await authorize(token);
        await RunAsync(["index", "--project-path=" + workspace, "--json-output"], TimeSpan.FromMinutes(30), token, progress: true);
    }

    public async Task<string> StatusAsync(CancellationToken token)
    {
        await authorize(token);
        return await RunAsync(["status", "--project-path=" + workspace, "--json-output"], TimeSpan.FromMinutes(1), token);
    }

    public async Task<string> SearchAsync(string query, string path, int limit, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var queryToken = operation.Token;
        await search.WaitAsync(queryToken);
        try
        {
            if (mcp is null)
            {
                await authorize(queryToken);
                queryToken.ThrowIfCancellationRequested();
                mcp = new(Start(executable, workspace, "mcp"), workspace, log);
                var tools = await mcp.InitializeAsync(queryToken);
                searchSchema = tools["tools"]?.AsArray().OfType<JsonObject>().SingleOrDefault(tool => tool["name"]?.ToString() == "code_search")?["inputSchema"] as JsonObject
                    ?? throw new IOException("This Context build does not advertise code_search.");
            }
            var properties = searchSchema?["properties"] as JsonObject ?? throw new IOException("Context code_search schema is missing.");
            var queryName = properties.ContainsKey("text") ? "text" : properties.ContainsKey("query") ? "query" : throw new IOException("Unsupported Context code_search query schema.");
            var args = new JsonObject { [queryName] = query };
            if (properties.ContainsKey("pathFilter")) args["pathFilter"] = path;
            else if (path != ".") throw new IOException("This Context build does not support path filters.");
            if (properties.ContainsKey("limit")) args["limit"] = limit;
            var result = await mcp.SearchAsync(args, queryToken);
            var content = result["content"]?.AsArray().OfType<JsonObject>().Where(item => item["type"]?.ToString() == "text").Select(item => item["text"]?.ToString() ?? "").ToArray()
                ?? throw new IOException("Context returned no text content.");
            if (result["isError"]?.GetValue<bool>() == true)
            {
                var reason = string.Join('\n', content);
                if (ContextOutput.RequiresLogin(reason)) throw new ContextLoginRequiredException();
                throw new IOException(ContextOutput.Sanitize(reason));
            }
            var text = string.Join('\n', content);
            return text.Length > 30_000 ? text[..30_000] + "\n[Output truncated; narrow the query or path filter.]" : text;
        }
        catch
        {
            if (mcp is { } failed) { mcp = null; searchSchema = null; await failed.DisposeAsync(); }
            throw;
        }
        finally { search.Release(); }
    }

    private async Task<string> RunAsync(string[] arguments, TimeSpan timeout, CancellationToken token, bool progress = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); deadline.CancelAfter(timeout);
        var start = Start(executable, workspace, arguments);
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("JetBrains Context did not start.");
        process.StandardInput.Close();
        using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        async Task<string> ReadAsync(StreamReader reader)
        {
            var output = new StringBuilder(); var pendingLog = new StringBuilder(); var buffer = new char[2048]; int count;
            while ((count = await reader.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (progress)
                {
                    pendingLog.Append(buffer, 0, count);
                    var lines = pendingLog.ToString().Split('\n');
                    foreach (var line in lines.SkipLast(1)) log(ContextOutput.Sanitize(line));
                    pendingLog.Clear(); pendingLog.Append(lines[^1]);
                }
                output.Append(buffer, 0, count);
                if (output.Length > 1_048_576) { if (!process.HasExited) process.Kill(true); throw new IOException("Context output exceeded 1 MiB."); }
            }
            if (progress && pendingLog.Length > 0) log(ContextOutput.Sanitize(pendingLog.ToString()));
            return output.ToString();
        }
        try
        {
            var stdout = ReadAsync(process.StandardOutput); var stderr = ReadAsync(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            var output = await stdout; var error = await stderr;
            var text = output + "\n" + error;
            if (ContextOutput.RequiresLogin(text)) throw new ContextLoginRequiredException();
            if (process.ExitCode != 0)
            {
                log(ContextOutput.Sanitize(text));
                throw new IOException("JetBrains Context exited with code " + process.ExitCode + ". View this workspace's Context logs.");
            }
            return ContextOutput.Sanitize(output.Length > 30_000 ? output[..30_000] + "\n[Output truncated.]" : output);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await search.WaitAsync();
        try { if (mcp is { } connection) await connection.DisposeAsync(); }
        finally { search.Release(); lifetime.Dispose(); }
    }
}

internal sealed class ContextLoginRequiredException() : IOException("JetBrains Context requires login. Choose Sign in in the Air Context pane.");