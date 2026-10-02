using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// Reads identity and ChatGPT usage allowances over one lazy, retained app-server connection per activation. Concurrent
/// reads share one request sequence; nothing is polled or cached between reads.
/// </summary>
public sealed class CodexAccountReader(Func<string> command, TimeSpan? timeout = null)
{
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(15);
    private readonly Lock gate = new();
    private CodexAppServer? server;
    private Task<CodexAccount>? inFlight;
    private bool stopped;

    public Task<CodexAccount> ReadAsync()
    {
        lock (gate)
        {
            if (stopped) return Task.FromException<CodexAccount>(new InvalidOperationException("Codex account reader stopped."));
            return inFlight ??= Run();
        }

        async Task<CodexAccount> Run()
        {
            try { return await ReadOnceAsync(); }
            finally { lock (gate) inFlight = null; }
        }
    }

    private async Task<CodexAccount> ReadOnceAsync()
    {
        await Task.Yield();
        CodexAppServer connection;
        CodexAppServer? stale = null;
        lock (gate)
        {
            if (server is not { Alive: true })
            {
                stale = server;
                if (stopped) throw new InvalidOperationException("Codex account reader stopped.");
                server = new CodexAppServer(CodexAppServer.Executable(command()), timeout);
            }
            connection = server;
        }
        if (stale is not null) await stale.StopAsync();
        var version = await ReadVersionAsync(CodexAppServer.Executable(command()), timeout);
        var response = await connection.RequestAsync("account/read", new { refreshToken = false });
        if (!ValidAccount(response, out var account))
        {
            await connection.StopAsync();
            throw new InvalidOperationException("Invalid Codex account response.");
        }
        var type = account is { } signedIn ? signedIn.GetProperty("type").GetString() : null;
        var result = new CodexAccount(account is not null, response.GetProperty("requiresOpenaiAuth").GetBoolean(), [])
        {
            Version = version,
            AuthMethod = type,
            Email = account is { } a && a.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String && email.GetString() is { Length: > 0 } address ? address : null,
            Plan = account is { } b && b.TryGetProperty("planType", out var plan) && plan.ValueKind == JsonValueKind.String ? plan.GetString() : null
        };
        if (type != "chatgpt") return result;
        try
        {
            var limits = await connection.RequestAsync("account/rateLimits/read");
            if (UsageOf(limits) is not { } usage)
            {
                await connection.StopAsync();
                throw new InvalidOperationException("Invalid Codex rate-limits response.");
            }
            return result with { Usage = usage, UsageFetchedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        }
        catch (Exception error) { return result with { UsageError = error.Message }; }
    }

    private static bool ValidAccount(JsonElement response, out JsonElement? account)
    {
        account = null;
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("requiresOpenaiAuth", out var auth) || auth.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        if (!response.TryGetProperty("account", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        if (value.TryGetProperty("email", out var email) && email.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
        if (value.TryGetProperty("planType", out var plan) && plan.ValueKind != JsonValueKind.String) return false;
        account = value;
        return true;
    }

    private static CodexUsageWindow? Window(JsonElement window, string id, string label)
    {
        if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number) return null;
        double? Number(string name) => window.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        bool NumberOrNull(string name) => !window.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Number or JsonValueKind.Null;
        if (!NumberOrNull("windowDurationMins") || !NumberOrNull("resetsAt")) return null;
        return new(id, label, used.GetDouble()) { WindowDurationMins = Number("windowDurationMins"), ResetsAt = Number("resetsAt") is { } at ? (long)at : null };
    }

    // The multi-bucket view wins over the legacy single bucket; null when the response does not have the documented shape.
    private static IReadOnlyList<CodexUsageWindow>? UsageOf(JsonElement limits)
    {
        if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty("rateLimits", out var legacy) || legacy.ValueKind != JsonValueKind.Object) return null;
        var buckets = new List<(string Id, JsonElement Bucket)>();
        if (limits.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
            buckets.AddRange(byId.EnumerateObject().Select(pair => (pair.Name, pair.Value)));
        else if (limits.TryGetProperty("rateLimitsByLimitId", out var other) && other.ValueKind != JsonValueKind.Null) return null;
        else buckets.Add((legacy.TryGetProperty("limitId", out var limitId) && limitId.ValueKind == JsonValueKind.String ? limitId.GetString()! : "codex", legacy));
        var usage = new List<CodexUsageWindow>();
        foreach (var (id, bucket) in buckets)
        {
            if (bucket.ValueKind != JsonValueKind.Object) return null;
            var label = bucket.TryGetProperty("limitName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : id;
            foreach (var kind in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(kind, out var window)) return null;
                if (window.ValueKind == JsonValueKind.Null) continue;
                if (Window(window, $"{id}:{kind}", label) is not { } parsed) return null;
                usage.Add(parsed);
            }
        }
        return usage;
    }

    private static async Task<string?> ReadVersionAsync(string executable, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return null;
            using var cancel = new CancellationTokenSource(timeout);
            var output = await process.StandardOutput.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            return process.ExitCode == 0 && output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() is { } version ? version : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException) { return null; }
    }

    public Task StopAsync()
    {
        CodexAppServer? current;
        lock (gate) { stopped = true; current = server; }
        return current?.StopAsync() ?? Task.CompletedTask;
    }
}

/// <summary>The local CLI's model catalog (<c>model/list</c>), read once per connection; a failure offers no models.</summary>
public sealed class CodexModelReader(Func<string> command, TimeSpan? timeout = null)
{
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(15);
    private readonly Lock gate = new();
    private CodexAppServer? server;
    private IReadOnlyList<CodexModel>? cached;
    private Task<IReadOnlyList<CodexModel>>? inFlight;
    private bool stopped;

    public Task<IReadOnlyList<CodexModel>> ReadAsync()
    {
        lock (gate)
        {
            if (stopped) return Task.FromException<IReadOnlyList<CodexModel>>(new InvalidOperationException("Codex model reader stopped."));
            if (cached is not null) return Task.FromResult(cached);
            return inFlight ??= Run();
        }

        async Task<IReadOnlyList<CodexModel>> Run()
        {
            try
            {
                var models = await ReadOnceAsync();
                lock (gate) cached = models;
                return models;
            }
            finally { lock (gate) inFlight = null; }
        }
    }

    private async Task<IReadOnlyList<CodexModel>> ReadOnceAsync()
    {
        await Task.Yield();
        CodexAppServer connection;
        lock (gate)
        {
            if (stopped) throw new InvalidOperationException("Codex model reader stopped.");
            if (server is not { Alive: true }) server = new CodexAppServer(CodexAppServer.Executable(command()), timeout);
            connection = server;
        }
        var models = new List<CodexModel>();
        string? cursor = null;
        do
        {
            var response = await connection.RequestAsync("model/list", new { cursor, includeHidden = false, limit = 100 });
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Invalid Codex model catalog.");
            foreach (var model in data.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("model", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Invalid Codex model catalog.");
                var label = model.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : id.GetString()!;
                models.Add(new(id.GetString()!, label));
            }
            cursor = response.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (!string.IsNullOrEmpty(cursor));
        return models;
    }

    public Task StopAsync()
    {
        CodexAppServer? current;
        lock (gate) { stopped = true; current = server; }
        return current?.StopAsync() ?? Task.CompletedTask;
    }
}