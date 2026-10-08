using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpRail.Host.Core;

/// <summary>The one bounded runner for prompt-free children: every Git, <c>gh</c> and probe call goes through it.</summary>
/// <remarks>
/// The child gets its own session, so nothing it starts can reach a terminal to prompt on and the whole group
/// can be killed. Completion follows the child's exit rather than the end of its pipes: a grandchild that
/// keeps them open costs at most the drain grace, never the budget.
/// </remarks>
internal static class ChildProcess
{
    internal static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxBudget = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>A finished child. <see cref="Bytes"/> is its undecoded standard output.</summary>
    internal sealed record Result(int ExitCode, byte[] Bytes, string Error)
    {
        internal string Output => Encoding.UTF8.GetString(Bytes);
    }

    /// <summary>The budget ran out and the child's process group was killed; <see cref="Error"/> is what it had written.</summary>
    internal sealed class ExpiredException(string file, TimeSpan waited, string error)
        : TimeoutException($"{file} did not finish within {Math.Max(1, Math.Round(waited.TotalSeconds)):0} s.")
    {
        internal TimeSpan Waited { get; } = waited;
        internal string Error { get; } = error;
    }

    /// <summary>
    /// Environment overrides apply over the live environment; a null value removes the variable. Throws
    /// <see cref="ExpiredException"/> on expiry and <see cref="Win32Exception"/> when the program cannot start.
    /// </summary>
    internal static Task<Result> RunAsync(string file, string workingDirectory, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ct.ThrowIfCancellationRequested();
        var budget = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout > MaxBudget ? MaxBudget : timeout;
        var arguments = args.ToArray();
        if (OperatingSystem.IsWindows()) return RunPortableAsync(file, workingDirectory, arguments, budget, ct, environment);
        Posix.EnsureSupported();
        var variables = LiveEnvironment(environment);
        var program = Locate(file, variables.GetValueOrDefault("PATH") ?? "")
            ?? throw new Win32Exception(2, $"{file} was not found on PATH.");
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        // One thread owns the child from spawn to reap, so its pipes are read from the first byte.
        new Thread(() =>
        {
            try { completion.SetResult(Run(file, program, workingDirectory, arguments, variables, budget, ct)); }
            catch (Exception error) { completion.SetException(error); }
        })
        { IsBackground = true, Name = "child " + file }.Start();
        return completion.Task;
    }

    private static Dictionary<string, string> LiveEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) variables[(string)entry.Key] = (string?)entry.Value ?? "";
        if (overrides is not null)
            foreach (var (name, value) in overrides)
            {
                if (value is null) variables.Remove(name);
                else variables[name] = value;
            }
        return variables;
    }

    /// <summary>Resolves a bare program name against <paramref name="path"/>, as the child would see it.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string? Locate(string file, string path)
    {
        if (file.Contains('/')) return file;
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, file);
            if (File.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return candidate;
        }
        return null;
    }

    private static Result Run(string file, string program, string workingDirectory, string[] args, Dictionary<string, string> variables,
        TimeSpan budget, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var (outRead, outWrite) = Posix.OpenPipe();
        int errRead, errWrite;
        try { (errRead, errWrite) = Posix.OpenPipe(); }
        catch { Posix.Close(outRead); Posix.Close(outWrite); throw; }
        int pid;
        try { pid = Spawn(file, program, workingDirectory, args, variables, outWrite, errWrite); }
        catch { Posix.Close(outRead); Posix.Close(errRead); throw; }
        finally { Posix.Close(outWrite); Posix.Close(errWrite); }

        var streams = new[] { new MemoryStream(), new MemoryStream() };
        var fds = new[] { new Posix.PollFd { Fd = outRead, Events = Posix.PollIn }, new Posix.PollFd { Fd = errRead, Events = Posix.PollIn } };
        var open = 2;
        var buffer = new byte[64 * 1024];
        var exitCode = -1;
        var exited = false; var timedOut = false; var cancelled = false;
        var drainUntil = TimeSpan.MaxValue;
        try
        {
            while (open > 0 || !exited)
            {
                if (!exited && !timedOut && !cancelled)
                {
                    cancelled = ct.IsCancellationRequested;
                    timedOut = !cancelled && clock.Elapsed >= budget;
                    // The child leads its own session, so its pid names the group and everything it started.
                    if ((cancelled || timedOut) && Posix.Kill(-pid, Posix.Sigkill) != 0) Posix.Kill(pid, Posix.Sigkill);
                }
                if (!exited && Reap(pid, out exitCode))
                {
                    exited = true;
                    drainUntil = clock.Elapsed + DrainGrace;
                }
                if (exited && clock.Elapsed >= drainUntil) break;
                if (open == 0) { Thread.Sleep(1); continue; }
                var limit = exited ? drainUntil - clock.Elapsed : timedOut || cancelled ? TimeSpan.FromMilliseconds(5) : budget - clock.Elapsed;
                var ready = Posix.Poll(fds, 2, (int)Math.Clamp(limit.TotalMilliseconds, 1, 20));
                if (ready <= 0) continue;
                for (var index = 0; index < fds.Length; index++)
                {
                    if (fds[index].Fd < 0 || fds[index].Revents == 0) continue;
                    var read = Posix.Read(fds[index].Fd, buffer, buffer.Length);
                    if (read > 0) { streams[index].Write(buffer, 0, (int)read); continue; }
                    if (read < 0 && Marshal.GetLastPInvokeError() is Posix.Eintr or Posix.Eagain or Posix.EagainLinux) continue;
                    Posix.Close(fds[index].Fd);
                    // poll ignores negative descriptors.
                    fds[index].Fd = -1;
                    open--;
                }
            }
        }
        finally
        {
            foreach (var fd in fds)
                if (fd.Fd >= 0) Posix.Close(fd.Fd);
            if (!exited)
            {
                Posix.Kill(-pid, Posix.Sigkill);
                Reap(pid, out _, block: true);
            }
        }
        var error = Encoding.UTF8.GetString(streams[1].GetBuffer(), 0, (int)streams[1].Length);
        if (cancelled) throw new OperationCanceledException(ct);
        if (timedOut) throw new ExpiredException(file, clock.Elapsed, error);
        return new(exitCode, streams[0].ToArray(), error);
    }

    private static bool Reap(int pid, out int exitCode, bool block = false)
    {
        exitCode = -1;
        int result, status;
        while ((result = Posix.WaitPid(pid, out status, block ? 0 : Posix.Wnohang)) < 0)
            if (Marshal.GetLastPInvokeError() != Posix.Eintr) return true;
        if (result == 0) return false;
        var signal = status & 0x7f;
        exitCode = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
        return true;
    }

    private static int Spawn(string file, string program, string workingDirectory, string[] args, Dictionary<string, string> variables, int output, int error)
    {
        // posix_spawnattr_t and posix_spawn_file_actions_t are pointers on macOS and structs on Linux.
        var attributes = Marshal.AllocHGlobal(1024);
        var actions = Marshal.AllocHGlobal(1024);
        var mask = Marshal.AllocHGlobal(256);
        var defaults = Marshal.AllocHGlobal(256);
        var attributesReady = false; var actionsReady = false;
        try
        {
            Check(Posix.SpawnAttrInit(attributes)); attributesReady = true;
            Check(Posix.FileActionsInit(actions)); actionsReady = true;
            Posix.SigEmptySet(mask); Posix.SigFillSet(defaults);
            Check(Posix.SpawnAttrSetMask(attributes, mask));
            Check(Posix.SpawnAttrSetDefault(attributes, defaults));
            Check(Posix.SpawnAttrSetFlags(attributes, Posix.SpawnFlags));
            Check(Posix.FileActionsOpen(actions, 0, "/dev/null", 0, 0));
            Check(Posix.FileActionsDup(actions, output, 1));
            Check(Posix.FileActionsDup(actions, error, 2));
            Check(Posix.FileActionsChdir(actions, workingDirectory));
            var argv = new string?[args.Length + 2];
            argv[0] = file;
            args.CopyTo(argv, 1);
            var envp = variables.Select(pair => pair.Key + "=" + pair.Value).Append(null).ToArray();
            var result = Posix.Spawn(out var pid, program, actions, attributes, argv, envp);
            if (result != 0) throw new Win32Exception(result, $"Could not start {file}: {Posix.Error(result)}.");
            return pid;
        }
        finally
        {
            if (actionsReady) Posix.FileActionsDestroy(actions);
            if (attributesReady) Posix.SpawnAttrDestroy(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(mask); Marshal.FreeHGlobal(defaults);
        }

        void Check(int result)
        {
            if (result != 0) throw new IOException($"Could not configure {file}: {Posix.Error(result)}.");
        }
    }

    // Windows has no sessions to detach from; the process tree stands in for the group.
    private static async Task<Result> RunPortableAsync(string file, string workingDirectory, string[] args, TimeSpan budget,
        CancellationToken ct, IReadOnlyDictionary<string, string?>? environment)
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
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {file}.");
        process.StandardInput.Close();
        using var output = new MemoryStream();
        var copied = process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var expired = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            expired = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        try { await Task.WhenAll(copied, error).WaitAsync(DrainGrace, CancellationToken.None); }
        catch (Exception failure) when (failure is TimeoutException or IOException or ObjectDisposedException) { }
        var text = error.IsCompletedSuccessfully ? error.Result : "";
        ct.ThrowIfCancellationRequested();
        if (expired) throw new ExpiredException(file, clock.Elapsed, text);
        return new(process.ExitCode, output.ToArray(), text);
    }
}