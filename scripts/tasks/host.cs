#!/usr/bin/env dotnet
// `scripts/mise run host`: launches only the remote host; scan the terminal QR code with the Android client.
// TEREDITOR_CONNECT_HOST overrides the advertised hostname/IP (without a port).
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    Environment.SetEnvironmentVariable("DOTNET_ROOT", Tool("dotnet"));
    Environment.SetEnvironmentVariable("TEREDITOR_ROOT", Env("TEREDITOR_ROOT") ?? Root);
    Environment.SetEnvironmentVariable("TEREDITOR_BIND", "0.0.0.0");
    Environment.SetEnvironmentVariable("TEREDITOR_PORT", Env("TEREDITOR_PORT") ?? "54123");
    Environment.SetEnvironmentVariable("TEREDITOR_TOKEN", Env("TEREDITOR_TOKEN") ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
    // Debug on Windows and Release elsewhere, as the two scripts this replaced chose.
    var configuration = Env("TEREDITOR_CONFIGURATION") ?? (OperatingSystem.IsWindows() ? "Debug" : "Release");
    Check(Dotnet, ["build", "src/Tereditor.Host.Remote", "-c", configuration]);
    Check(Dotnet, ["run", "--file", "scripts/host-link.cs"]);
    return Run(Dotnet, [At("src", "Tereditor.Host.Remote", "bin", configuration, "net10.0", "Tereditor.Host.Remote.dll"), .. args]);
});
