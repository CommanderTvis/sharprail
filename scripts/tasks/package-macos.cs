#!/usr/bin/env dotnet
// `scripts/mise run package:macos [osx-arm64|osx-x64]`: publishes the app and host (NativeAOT, linked by Xcode's clang) and the
// checks, and builds artifacts/tereditor.app.
#:include ../lib/Tasks.cs
using static Tasks;

// `scripts/mise run icon:macos`: compiles the Icon Composer document with actool (Xcode 26 or newer; not run in CI, whose
// macOS 15 runner cannot run Xcode 26's actool) into the checked-in outputs.
static int CompileIcon()
{
    var (versionCode, version) = Capture("xcrun", ["actool", "--version"], discardError: true);
    var actoolMajor = System.Text.RegularExpressions.Regex.Match(version, @"short-bundle-version</key>\s*<string>(\d+)").Groups[1].Value;
    if (versionCode != 0 || !int.TryParse(actoolMajor, out var major) || major < 26)
        throw new TaskFailure($"The app icon needs actool from Xcode 26 or newer to compile {IconDocument}; xcrun found {(versionCode != 0 ? "no actool" : $"actool {(actoolMajor.Length > 0 ? actoolMajor : "of an unknown version")}")} (select an Xcode with xcode-select or DEVELOPER_DIR).");
    DeleteAll(IconOutput);
    Directory.CreateDirectory(IconOutput);
    var partialPlist = Path.Combine("artifacts", "tereditor-icon.plist");
    Directory.CreateDirectory("artifacts");
    Check("xcrun", ["actool", IconDocument, "--compile", IconOutput, "--app-icon", "tereditor", "--platform", "macosx",
        "--minimum-deployment-target", "12.0", "--standalone-icon-behavior", "all", "--enable-on-demand-resources", "NO",
        "--output-partial-info-plist", partialPlist, "--output-format", "human-readable-text", "--errors", "--warnings"]);
    DeleteAll(partialPlist);
    if (!File.Exists(Path.Combine(IconOutput, "Assets.car")) || !File.Exists(Path.Combine(IconOutput, "tereditor.icns")))
        throw new TaskFailure("actool did not write Assets.car and tereditor.icns");
    File.WriteAllText(IconHashFile, IconHash() + "\n");
    return 0;
}

return Guard(() =>
{
    if (args.Length > 0 && args[0] == "--icon") return CompileIcon();
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
    var resources = LayOutMacBundle("artifacts/ui", bundle);
    foreach (var file in new[] { "LICENSE.md", "NOTICE", "THIRD-PARTY-NOTICES.md" }) File.Copy(file, Path.Combine(resources, file), true);
    CopyInto("licenses", resources);
    foreach (var file in new[] { "Geist-OFL.txt", "JetBrainsMono-OFL.txt" }) File.Copy(Path.Combine("src", "Tereditor.Plugins.UI.Kit", "Assets", "Fonts", file), Path.Combine(resources, file), true);
    Check("codesign", ["--force", "--deep", "--sign", "-", bundle]);
    return 0;
});
