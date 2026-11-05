using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Pi.Host;

/// <summary>The Pi plugin's host half: the version probe and the chat sessions over <c>pi --mode rpc</c>.</summary>
public sealed class PiHost : PluginHostModule
{
    public override PluginContract Contract => PiContract.Contract;

    public override IJsonTypeInfoResolver Json => PiHostJson.Default;

    internal static async Task<PiStatus> ReadStatusAsync(string command)
    {
        if (PiLinks.InTab is not null) return new PiStatus(true, "in this tab") { Version = PiLinks.InTabVersion is { } version ? await version() : null };
        var executable = command.Trim() is { Length: > 0 } trimmed ? trimmed : "pi";
        var status = new PiStatus(false, executable);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return status with { Detail = "Pi could not be started." };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            var text = (await output).Trim();
            if (process.ExitCode != 0) return status with { Detail = (await errors).Trim() is { Length: > 0 } detail ? detail : $"pi --version exited {process.ExitCode}." };
            return status with { Installed = true, Version = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() };
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return status with { Detail = $"{executable} was not found on PATH." };
        }
        catch (OperationCanceledException) { return status with { Detail = "pi --version timed out." }; }
    }

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.Method(PiContract.Status, async (_, _, _) => await Task.Run(() => ReadStatusAsync(context.Settings<PiSettings>().Command)));
        var chats = new PiChatHost(context);
        chats.Register();
        return ValueTask.FromResult<PluginDisposer?>(() => new ValueTask(chats.DisposeAsync()));
    }
}