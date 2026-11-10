#!/usr/bin/env dotnet
// Installs a .NET workload into the SDK once: workload.cs -- <workload> <pack directory that marks it installed> [dotnet options].
// The SDK is shared by every checkout (see mise.toml), so installs are serialised with a lock directory beside it.
// The android and browser tasks do this themselves; this is the same step by hand.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    if (args.Length < 2) throw new TaskFailure("Usage: dotnet run --file scripts/tasks/workload.cs -- <workload> <pack directory> [dotnet options]");
    EnsureWorkload(args[0], args[1], args[2..]);
    return 0;
});
