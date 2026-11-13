#!/usr/bin/env dotnet
// `scripts/mise run package:macos [osx-arm64|osx-x64]`: publishes the app and host (NativeAOT, linked by Xcode's clang) and the
// checks, and builds artifacts/tereditor.app.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    var rid = args.Length > 0 && args[0].Length > 0 ? args[0] : "osx-arm64";
    // Publish copies only newer files; stale outputs (for example a native library from a
    // downgraded timestamp) would otherwise survive into the package.
    DeleteAll("artifacts/ui", "artifacts/host", "artifacts/checks", "artifacts/tereditor.app");
    var stamp = BuildStamp();
    // One locked restore (packages.lock.json lists every shipped RID) that also fetches the runtime, ILCompiler and Crossgen
    // packs; the publishes below reuse it, since a `-r` restore would not match the lock's RuntimeIdentifiers.
    Check(Dotnet, ["restore", "Tereditor.slnx", "-p:SelfContained=true", "-p:PublishReadyToRun=true"]);
    Check(Dotnet, ["publish", "src/Tereditor.UI", "-c", "Release", "-r", rid, "--no-restore", $"-p:TereditorBuildStamp={stamp}", "-o", "artifacts/ui"]);
    Check(Dotnet, ["publish", "src/Tereditor.Host.Remote", "-c", "Release", "-r", rid, "--no-restore", "-o", "artifacts/host"]);
    RenameExecutable("artifacts/host", "Tereditor.Host.Remote", HostExecutable);
    // The checks stay a JIT program (ReadyToRun); the packaged gate runs them beside the bundle's native libraries.
    Check(Dotnet, ["publish", "tests/Tereditor.Checks", "-c", "Release", "-r", rid, "--self-contained", "true", "--no-restore", "-p:PublishReadyToRun=true", "-o", "artifacts/checks"]);
    if (rid is not ("osx-arm64" or "osx-x64")) return 0;

    var bundle = "artifacts/tereditor.app";
    var macOS = Path.Combine(bundle, "Contents", "MacOS");
    var resources = Path.Combine(bundle, "Contents", "Resources");
    Directory.CreateDirectory(macOS);
    Directory.CreateDirectory(resources);
    // The shell glob this replaced skipped names that start with a dot.
    foreach (var item in Directory.EnumerateFileSystemEntries("artifacts/ui").Where(path => !Path.GetFileName(path).StartsWith('.')))
    {
        var name = Path.GetFileName(item);
        if (name is "terminfo" or "plugins")
        {
            Copy(item, Path.Combine(resources, name));
            if (name == "plugins") File.CreateSymbolicLink(Path.Combine(macOS, "plugins"), "../Resources/plugins");
        }
        else CopyInto(item, macOS);
    }
    foreach (var file in new[] { "LICENSE.md", "NOTICE", "THIRD-PARTY-NOTICES.md" }) File.Copy(file, Path.Combine(resources, file), true);
    CopyInto("licenses", resources);
    foreach (var file in new[] { "Geist-OFL.txt", "JetBrainsMono-OFL.txt" }) File.Copy(Path.Combine("src", "Tereditor.Plugins.UI.Kit", "Assets", "Fonts", file), Path.Combine(resources, file), true);
    // The icon: an iconset scaled from the source image (686 px, so no 512@2x, which would only be upscaled), packed by iconutil.
    var iconset = Path.Combine("artifacts", "tereditor.iconset");
    DeleteAll(iconset);
    Directory.CreateDirectory(iconset);
    foreach (var (name, size) in new[] { ("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                 ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512) })
        if (Capture("sips", ["-z", $"{size}", $"{size}", "src/Tereditor.UI/Assets/AppIcon/macos.png", "--out", Path.Combine(iconset, $"icon_{name}.png")]).Code != 0)
            throw new TaskFailure("sips could not scale the app icon");
    Check("iconutil", ["--convert", "icns", "--output", Path.Combine(resources, "tereditor.icns"), iconset]);
    DeleteAll(iconset);
    WriteLf(Path.Combine(bundle, "Contents", "Info.plist"), $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>CFBundleExecutable</key><string>{AppExecutable}</string>
        <key>CFBundleIdentifier</key><string>dev.thinkrail.tereditor</string>
        <key>CFBundleName</key><string>tereditor</string>
        <key>CFBundleVersion</key><string>1</string>
        <key>CFBundleShortVersionString</key><string>0.2.0</string>
        <key>CFBundlePackageType</key><string>APPL</string>
        <key>CFBundleIconFile</key><string>tereditor</string>
        <key>NSHighResolutionCapable</key><true/>
        </dict></plist>

        """);
    // Tahoe rounds windows by the SDK the executable was linked against: a 15.x stamp keeps the old small corner
    // radius, so the packaged executable is restamped as built with SDK 26 (the signature is redone below).
    var exe = RenameExecutable(macOS, "Tereditor.UI", AppExecutable);
    var (_, show) = Capture("xcrun", ["vtool", "-show-build", exe]);
    var minos = show.Split('\n').Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        .FirstOrDefault(fields => fields.Any(field => field.Contains("minos")) && fields.Length > 1)?[1] ?? "";
    if (Capture("xcrun", ["vtool", "-set-build-version", "macos", minos, "26.0", "-replace", "-output", exe + ".stamped", exe], discardError: true).Code != 0)
        throw new TaskFailure("xcrun vtool -set-build-version failed");
    File.Move(exe + ".stamped", exe, true);
    MakeExecutable(exe);
    Check("codesign", ["--force", "--deep", "--sign", "-", bundle]);
    return 0;
});
