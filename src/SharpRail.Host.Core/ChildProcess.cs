using System.Diagnostics;

namespace SharpRail.Host.Core;

/// <summary>Runs a prompt-free child under a wall-clock budget, killing its whole process tree on expiry.</summary>
internal static class ChildProcess
{
    internal sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>Environment overrides; a null value removes the variable. Throws <see cref="TimeoutException"/> on expiry.</summary>
    internal static async Task<Result> RunAsync(string file, string workingDirectory, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (name, value) in environment)
            {
                if (value is null) start.Environment.Remove(name);
                else start.Environment[name] = value;
            }
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
        process.StandardInput.Close();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var all = Task.WhenAll(output, error, process.WaitForExitAsync(CancellationToken.None));
        try { await all.WaitAsync(budget.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            try { await all.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{file} did not finish within {timeout.TotalSeconds:0} s.");
        }
        return new(process.ExitCode, await output, await error);
    }
}