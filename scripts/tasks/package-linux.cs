#!/usr/bin/env dotnet
// `scripts/mise run package:linux`: publishes the Linux client (NativeAOT) and checks (self-contained, ReadyToRun) and packs the client into
// artifacts/SharpRail-linux-x64.AppImage (or, given `linux-arm64`, artifacts/SharpRail-linux-arm64.AppImage) with the pinned appimagetool and type 2 runtime. The native libraries
// cross-compile with Zig during the build and the client links with Zig against glibc 2.31 (scripts/aot-linux-cc.sh), so the publish also
// works on macOS; appimagetool runs on Linux only. Set SHARPRAIL_APPDIR_ONLY=1
// elsewhere to stop after assembling artifacts/linux/AppDir. With SHARPRAIL_GPG_KEY_ID set (a key in the GPG keyring), the image
// carries an embedded signature that AppImageLauncher and appimaged verify.
#:include ../lib/Tasks.cs
using System.Runtime.InteropServices;
using static Tasks;

return Guard(() =>
{
    var rid = args.Length > 0 && args[0].Length > 0 ? args[0] : "linux-x64";
    var appImageArchitecture = rid switch
    {
        "linux-x64" => "x86_64",
        "linux-arm64" => "aarch64",
        _ => throw new TaskFailure($"Usage: package-linux [linux-x64|linux-arm64] (got '{rid}')"),
    };
    var output = "artifacts/linux";
    var image = $"artifacts/SharpRail-{rid}.AppImage";
    var appDirOnly = Env("SHARPRAIL_APPDIR_ONLY") == "1";
    // appimagetool and its runtime are per-architecture binaries, so the image is built on a host of its own architecture.
    if (OperatingSystem.IsLinux() && !appDirOnly)
    {
        var host = RuntimeInformation.OSArchitecture switch { Architecture.X64 => "linux-x64", Architecture.Arm64 => "linux-arm64", var other => other.ToString().ToLowerInvariant() };
        if (host != rid) throw new TaskFailure($"Building {rid}.AppImage needs a {rid} host; this one is {host}. SHARPRAIL_APPDIR_ONLY=1 assembles the AppDir only.");
    }
    if (!OperatingSystem.IsLinux() && !appDirOnly) throw new TaskFailure("appimagetool runs on Linux; set SHARPRAIL_APPDIR_ONLY=1 to assemble artifacts/linux/AppDir here.");
    // Publish copies only newer files; stale outputs would otherwise survive into the package.
    DeleteAll(output, image);
    // The compiler gate, the natives and the fixture plugin the checks install.
    Check(Dotnet, ["build", "SharpRail.slnx", "-c", "Release"]);
    var stamp = BuildStamp();
    // One locked restore (packages.lock.json lists every shipped RID) that also fetches the runtime, ILCompiler and Crossgen
    // packs; the publishes below reuse it, since a `-r` restore would not match the lock's RuntimeIdentifiers.
    Check(Dotnet, ["restore", "SharpRail.slnx", "-p:SelfContained=true", "-p:PublishReadyToRun=true"]);
    Check(Dotnet, ["publish", "src/SharpRail.UI", "-c", "Release", "-r", rid, "--no-restore", $"-p:SharpRailBuildStamp={stamp}", "-o", $"{output}/client"]);
    // The checks stay a JIT program (ReadyToRun); the packaged gate runs them beside the client's native libraries.
    Check(Dotnet, ["publish", "tests/SharpRail.Checks", "-c", "Release", "-r", rid, "--self-contained", "true", "--no-restore", "-p:PublishReadyToRun=true", "-o", $"{output}/checks"]);

    var app = $"{output}/AppDir";
    var payload = $"{app}/usr/lib/sharprail";
    var doc = $"{app}/usr/share/doc/sharprail";
    Directory.CreateDirectory(payload);
    Directory.CreateDirectory(doc);
    Directory.CreateDirectory($"{app}/usr/share/icons/hicolor/96x96/apps");
    Copy($"{output}/client", payload);
    // Debug symbols and API documentation do not belong in an image users download.
    foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Where(path => path.EndsWith(".pdb", StringComparison.Ordinal) || path.EndsWith(".xml", StringComparison.Ordinal)))
        File.Delete(file);
    MakeExecutable($"{payload}/SharpRail.UI");
    foreach (var file in new[] { "LICENSE.md", "NOTICE", "THIRD-PARTY-NOTICES.md" }) File.Copy(file, $"{doc}/{file}", true);
    CopyInto("licenses", doc);
    File.Copy("src/SharpRail.Plugins.UI.Kit/Assets/Icons/brand.png", $"{app}/sharprail.png", true);
    File.Copy($"{app}/sharprail.png", $"{app}/.DirIcon", true);
    File.Copy($"{app}/sharprail.png", $"{app}/usr/share/icons/hicolor/96x96/apps/sharprail.png", true);
    WriteLf($"{app}/sharprail.desktop", """
        [Desktop Entry]
        Type=Application
        Name=SharpRail
        Comment=Workbench for projects, Git worktrees, specs and agent terminals
        Exec=SharpRail.UI
        Icon=sharprail
        Terminal=false
        Categories=Development;IDE;
        StartupWMClass=SharpRail.UI

        """);
    WriteLf($"{app}/AppRun", """
        #!/bin/sh
        here="$(dirname "$(readlink -f "$0")")"
        # .NET aborts at startup without ICU unless globalization is invariant; minimal images lack it, so fall back to invariant mode.
        if [ -z "${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-}" ] && ! PATH="$PATH:/sbin:/usr/sbin" ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
          export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
        fi
        exec "$here/usr/lib/sharprail/SharpRail.UI" "$@"

        """);
    MakeExecutable($"{app}/AppRun");

    if (!OperatingSystem.IsLinux())
    {
        Console.WriteLine($"Assembled {app}; run this task on Linux to build the AppImage.");
        return 0;
    }
    // These tools exist for Linux only; scripts/tool installs them on first use.
    var tool = Path.Combine(Tool("appimagetool"), "appimagetool.AppImage");
    var runtime = Path.Combine(Tool("appimage-runtime"), "runtime");
    if (!File.Exists(tool) || !File.Exists(runtime)) throw new TaskFailure("scripts/tool did not install appimagetool.");
    // The store is read-only and appimagetool needs neither FUSE nor a writable home: it unpacks itself and writes only the image.
    // The task argument (the runtime identifier) is not an appimagetool argument.
    List<string> sign = Env("SHARPRAIL_GPG_KEY_ID") is { } key ? ["--sign", "--sign-key", key] : [];
    Check(tool, [.. sign, "--no-appstream", "--runtime-file", runtime, "--comp", "zstd", "--mksquashfs-opt", "-Xcompression-level", "--mksquashfs-opt", "19", app, image],
        env: new Dictionary<string, string> { ["ARCH"] = appImageArchitecture, ["APPIMAGE_EXTRACT_AND_RUN"] = "1" });
    MakeExecutable(image);
    Console.WriteLine($"Built {image}");
    return 0;
});
