#!/usr/bin/env dotnet
// Builds the macOS User Notifications bridge the app posts desktop notifications through (nothing elsewhere).
// Usage: dotnet run scripts/build-notifications.cs -- <output dylib>. The UI project's BuildNotifications target decides when it runs.
#:include lib/Tasks.cs
using System.Runtime.InteropServices;
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsMacOS()) return 0;
    if (args.Length != 1) throw new TaskFailure("usage: build-notifications.cs <output dylib>");
    var output = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
    Check("clang", ["-dynamiclib", "-arch", arch, "-fobjc-arc", "-mmacosx-version-min=13.0", "-O2", "-Wall", "-Wextra", "-Werror", "-Wno-unused-parameter",
        "src/SharpRail.UI/Native/Notifications.m", "-framework", "AppKit", "-framework", "Foundation", "-framework", "UserNotifications",
        "-install_name", "@rpath/libSharpRailNotifications.dylib", "-o", output + ".partial"], Root);
    File.Move(output + ".partial", output, true);
    return 0;
});
