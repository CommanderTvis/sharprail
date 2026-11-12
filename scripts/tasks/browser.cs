#!/usr/bin/env dotnet
// `scripts/mise run browser [build|run|publish|check]`: builds, publishes, checks or runs the browser client.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    EnsureWorkload("wasm-experimental", "Microsoft.NETCore.App.Runtime.Mono.multithread.browser-wasm", "--skip-manifest-update");
    var configuration = Env("TEREDITOR_CONFIGURATION") ?? "Release";
    void Dotnet_(params string[] arguments) => Check(Dotnet, arguments);
    // The Playwright tools (node, pnpm) are the browser mise environment's pins, not on PATH on a plain runner.
    void MiseBrowser(string directory, params string[] command) =>
        Check("sh", [At("scripts", "mise"), "x", "--", .. command], directory, new Dictionary<string, string> { ["MISE_ENV"] = "browser" });
    switch (args.Length > 0 ? args[0] : "build")
    {
        case "build":
            Dotnet_("build", "src/Tereditor.Browser", "-c", configuration, "-m:2");
            return 0;
        case "publish":
            Dotnet_("publish", "src/Tereditor.Browser", "-c", configuration, "-m:2", $"-p:WasmAppDir={At("artifacts", "browser")}");
            return 0;
        case "check" when OperatingSystem.IsWindows():
            Dotnet_("build", "src/Tereditor.Host.Remote", "-c", configuration, "-m:2");
            Dotnet_("build", "src/Tereditor.Browser", "-c", configuration, "-m:2", "-p:BrowserChecks=true");
            Check("pnpm", ["exec", "playwright", "install", "chromium"], "src/Tereditor.Browser");
            Check("node", [Path.Combine("tests", "Tereditor.Browser.Checks", "check.mjs")]);
            return 0;
        case "check":
            Check("node", ["tests/Tereditor.Browser.Checks/compression.mjs"]);
            Check("node", ["tests/Tereditor.Browser.Checks/tailscale.mjs"]);
            Dotnet_("build", "tests/Tereditor.Checks", "-c", configuration, "-m:2");
            Dotnet_("build", "src/Tereditor.Browser", "-c", configuration, "-m:2", "-p:BrowserChecks=true");
            // pnpm is the mise pin (not on PATH on a plain runner); Linux also needs Chromium's system libraries (apt, via sudo).
            MiseBrowser("src/Tereditor.Browser", ["pnpm", "exec", "playwright", "install", "chromium", .. OperatingSystem.IsLinux() ? new[] { "--with-deps" } : []]);
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/selection.mjs");
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/check.mjs");
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/local-approval.mjs");
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/phone.mjs");
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/restty.mjs", "--hover");
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/restty.mjs", "--stream");
            MiseBrowser(Root, "node", "tests/Tereditor.Browser.Checks/terminal-glyphs.mjs");
            return 0;
        case "run":
            if (Env("TEREDITOR_TOKEN") is null) throw new TaskFailure("Set TEREDITOR_TOKEN to the host session token.");
            Dotnet_("build", "src/Tereditor.Browser", "-c", configuration, "-m:2");
            Dotnet_("build", "src/Tereditor.Host.Remote", "-c", configuration, "-m:2");
            Environment.SetEnvironmentVariable("TEREDITOR_PORT", Env("TEREDITOR_PORT") ?? "54123");
            Environment.SetEnvironmentVariable("TEREDITOR_BROWSER_ROOT", At("src", "Tereditor.Browser", "bin", configuration, "net10.0-browser", "AppBundle"));
            return Run(Dotnet, ["run", "--no-build", "--project", "src/Tereditor.Host.Remote", "-c", configuration]);
        default:
            throw new TaskFailure("Usage: scripts/mise run browser [build|run|publish|check]");
    }
});
