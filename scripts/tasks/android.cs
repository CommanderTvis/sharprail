#!/usr/bin/env dotnet
// `scripts/mise run android [build|run|apk|install]` builds the Android client (src/Tereditor.Android). `run` also installs it on the connected device or
// emulator and starts it; `apk` builds the signed Release package into artifacts/android; `install` puts that package on the
// device when exactly one is connected, rebuilding it first (MSBuild skips what is unchanged).
// The Android SDK is the pinned one that `scripts/tool android-sdk` prints (mise.android.toml); the task installs the
// packages below into it when they are missing. ANDROID_HOME, when set, names another SDK instead, used as it is. The JDK
// is the pinned one that `scripts/tool jdk` prints; JAVA_HOME, when set, names another JDK 17 or 21 instead.
// With TEREDITOR_ANDROID_KEYSTORE (a keystore file), TEREDITOR_ANDROID_KEY_ALIAS and TEREDITOR_ANDROID_KEY_PASSWORD
// set, the package is signed with that key instead of the machine's debug key.
#:include ../lib/Tasks.cs
using static Tasks;

// The SDK packages the build needs. platform-tools has no versioned path, so sdkmanager installs its latest release.
string[] packages = ["platforms;android-36", "build-tools;36.0.0", "platform-tools"];
// sdkmanager records an accepted licence as the SHA-1 of its text in licenses/<name>. The user has accepted the Android
// Software Development Kit License Agreement, which all three packages require, so the task writes its hash and
// sdkmanager installs without asking. Google changes the hash when it changes the text; then sdkmanager asks again.
const string LicenseName = "android-sdk-license";
const string LicenseHash = "24333f8a63b6825ea9c5514f83c2829b004d1fee";

return Guard(() =>
{
    Environment.SetEnvironmentVariable("DOTNET_ROOT", Tool("dotnet"));
    EnsureWorkload("android", "android");
    var java = Env("JAVA_HOME") ?? Tool("jdk");
    if (!File.Exists(Path.Combine(java, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac")))
        throw new TaskFailure($"No JDK at {java}; run scripts/mise install, or set JAVA_HOME to a JDK 17 or 21.");
    var sdk = Env("ANDROID_HOME") ?? PinnedSdk();
    if (!Directory.Exists(Path.Combine(sdk, "platforms"))) throw new TaskFailure($"No Android SDK at {sdk}; unset ANDROID_HOME to use the pinned one.");

    // The pinned SDK is shared by every checkout, so installs are serialised with a lock beside it.
    string PinnedSdk()
    {
        var root = Tool("android-sdk");
        bool Missing(string package) => !File.Exists(Path.Combine([root, .. package.Split(';'), "package.xml"]));
        if (!packages.Any(Missing)) return root;
        using var _ = AcquireLock(Path.Combine(root, ".packages.lock"));
        var missing = packages.Where(Missing).ToArray();
        if (missing.Length == 0) return root;
        var sdkmanager = Directory.GetDirectories(Path.Combine(root, "cmdline-tools")).Select(tools => Path.Combine(tools, "bin", "sdkmanager"))
            .FirstOrDefault(File.Exists) ?? throw new TaskFailure($"No sdkmanager under {root}/cmdline-tools.");
        var licenses = Path.Combine(root, "licenses");
        Directory.CreateDirectory(licenses);
        var license = Path.Combine(licenses, LicenseName);
        if (!File.Exists(license) || !File.ReadAllLines(license).Contains(LicenseHash)) File.AppendAllText(license, "\n" + LicenseHash);
        Check(sdkmanager, [$"--sdk_root={root}", .. missing], env: new Dictionary<string, string> { ["JAVA_HOME"] = java });
        return root;
    }

    void Build(params string[] options)
    {
        List<string> signing = [];
        if (Env("TEREDITOR_ANDROID_KEYSTORE") is { } keystore)
            // The passwords reach the build by name, so they appear in no command line or log.
            signing = ["-p:AndroidKeyStore=true", $"-p:AndroidSigningKeyStore={keystore}", $"-p:AndroidSigningKeyAlias={Environment.GetEnvironmentVariable("TEREDITOR_ANDROID_KEY_ALIAS")}",
                "-p:AndroidSigningKeyPass=env:TEREDITOR_ANDROID_KEY_PASSWORD", "-p:AndroidSigningStorePass=env:TEREDITOR_ANDROID_KEY_PASSWORD"];
        Check(Dotnet, ["build", "src/Tereditor.Android", $"-p:AndroidSdkDirectory={sdk}", $"-p:JavaSdkDirectory={java}", .. options, .. signing]);
    }

    var adb = Path.Combine(sdk, "platform-tools", "adb");
    var apk = "artifacts/android/tereditor.apk";
    void Package()
    {
        Build("-c", "Release");
        Directory.CreateDirectory("artifacts/android");
        File.Copy("src/Tereditor.Android/bin/Release/net10.0-android/dev.thinkrail.tereditor-Signed.apk", apk, true);
        Console.WriteLine(apk);
    }

    var configuration = Env("TEREDITOR_CONFIGURATION") ?? "Debug";
    switch (args.Length > 0 ? args[0] : "build")
    {
        case "build":
            Build("-c", configuration);
            return 0;
        case "run":
            Build("-c", configuration, "-t:Install");
            if (Capture(adb, ["shell", "monkey", "-p", "dev.thinkrail.tereditor", "-c", "android.intent.category.LAUNCHER", "1"]).Code != 0) throw new TaskFailure("adb shell monkey failed");
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
