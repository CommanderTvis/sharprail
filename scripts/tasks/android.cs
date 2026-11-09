#!/usr/bin/env dotnet
// `scripts/mise run android [build|run|apk|install]` builds the Android client (src/SharpRail.Android). `run` also installs it on the connected device or
// emulator and starts it; `apk` builds the signed Release package into artifacts/android; `install` puts that package on the
// device when exactly one is connected, rebuilding it first (MSBuild skips what is unchanged).
// Needs the Android SDK (ANDROID_HOME, default ~/Library/Android/sdk). The JDK is the pinned one that
// `scripts/tool jdk` prints; JAVA_HOME, when set, names another JDK 17 or 21 instead.
// With SHARPRAIL_ANDROID_KEYSTORE (a keystore file), SHARPRAIL_ANDROID_KEY_ALIAS and SHARPRAIL_ANDROID_KEY_PASSWORD
// set, the package is signed with that key instead of the machine's debug key.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    Environment.SetEnvironmentVariable("DOTNET_ROOT", Tool("dotnet"));
    EnsureWorkload("android", "android");
    var sdk = Env("ANDROID_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Android", "sdk");
    if (!Directory.Exists(Path.Combine(sdk, "platforms"))) throw new TaskFailure($"No Android SDK at {sdk}; set ANDROID_HOME.");
    var java = Env("JAVA_HOME") ?? Tool("jdk");
    if (!File.Exists(Path.Combine(java, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac")))
        throw new TaskFailure($"No JDK at {java}; run scripts/mise install, or set JAVA_HOME to a JDK 17 or 21.");

    void Build(params string[] options)
    {
        List<string> signing = [];
        if (Env("SHARPRAIL_ANDROID_KEYSTORE") is { } keystore)
            // The passwords reach the build by name, so they appear in no command line or log.
            signing = ["-p:AndroidKeyStore=true", $"-p:AndroidSigningKeyStore={keystore}", $"-p:AndroidSigningKeyAlias={Environment.GetEnvironmentVariable("SHARPRAIL_ANDROID_KEY_ALIAS")}",
                "-p:AndroidSigningKeyPass=env:SHARPRAIL_ANDROID_KEY_PASSWORD", "-p:AndroidSigningStorePass=env:SHARPRAIL_ANDROID_KEY_PASSWORD"];
        Check(Dotnet, ["build", "src/SharpRail.Android", $"-p:AndroidSdkDirectory={sdk}", $"-p:JavaSdkDirectory={java}", .. options, .. signing]);
    }

    var adb = Path.Combine(sdk, "platform-tools", "adb");
    var apk = "artifacts/android/SharpRail.apk";
    void Package()
    {
        Build("-c", "Release");
        Directory.CreateDirectory("artifacts/android");
        File.Copy("src/SharpRail.Android/bin/Release/net10.0-android/dev.thinkrail.sharprail-Signed.apk", apk, true);
        Console.WriteLine(apk);
    }

    var configuration = Env("SHARPRAIL_CONFIGURATION") ?? "Debug";
    switch (args.Length > 0 ? args[0] : "build")
    {
        case "build":
            Build("-c", configuration);
            return 0;
        case "run":
            Build("-c", configuration, "-t:Install");
            if (Capture(adb, ["shell", "monkey", "-p", "dev.thinkrail.sharprail", "-c", "android.intent.category.LAUNCHER", "1"]).Code != 0) throw new TaskFailure("adb shell monkey failed");
            return 0;
        case "apk":
            Package();
            return 0;
        case "install":
            var devices = Capture(adb, ["devices"]).Output.Split('\n').Skip(1)
                .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
                .Where(fields => fields.Length > 1 && fields[1].TrimEnd('\r') == "device").Select(fields => fields[0]).ToList();
            if (devices.Count == 0) throw new TaskFailure("No device connected.");
            if (devices.Count > 1) throw new TaskFailure("More than one device connected; unplug the others:\n" + string.Join('\n', devices));
            Package();
            Check(adb, ["-s", devices[0], "install", "-r", apk]);
            return 0;
        default:
            throw new TaskFailure("Usage: scripts/mise run android [build|run|apk|install]");
    }
});
