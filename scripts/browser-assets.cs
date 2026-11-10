// Stages the browser client's vendored assets (renderer, fonts, licences, the tab's Pi agent and CSS language server) into
// src/SharpRail.Browser/wwwroot/vendor. Run by the browser project's StageBrowserAssets target (under the browser mise
// environment, so node, pnpm and the PDFium pin are on PATH); from anywhere inside the checkout:
//   MISE_ENV=browser scripts/mise x -- dotnet run --file scripts/browser-assets.cs
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var checkout = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(checkout, "SharpRail.slnx")))
    checkout = Path.GetDirectoryName(checkout) ?? throw new InvalidOperationException("Run inside the SharpRail checkout.");
var root = checkout;
var project = Path.Combine(root, "src/SharpRail.Browser");
var vendor = Path.Combine(project, "wwwroot/vendor");
var utf8 = new UTF8Encoding(false);
Directory.CreateDirectory(vendor);
CopyDirectory(Path.Combine(root, "licenses"), Path.Combine(vendor, "licenses"));
foreach (var file in new[] { "LICENSE.md", "NOTICE", "THIRD-PARTY-NOTICES.md" })
    Copy(Path.Combine(root, file), Path.Combine(vendor, file));
foreach (var dependency in new[] { "Ghostty.Avalonia", "SharpRail.Scintilla" })
    CopyDirectory(Path.Combine(root, "src", dependency, "licenses"), Path.Combine(vendor, "licenses", dependency));
foreach (var font in new[] { "Geist-OFL.txt", "JetBrainsMono-OFL.txt" })
    Copy(Path.Combine(root, "src/SharpRail.Plugins.UI.Kit/Assets/Fonts", font), Path.Combine(vendor, "licenses", font));
foreach (var directory in Directory.GetDirectories(Path.Combine(root, "src")))
{
    var name = Path.GetFileName(directory);
    if (!name.StartsWith("SharpRail.Plugins.", StringComparison.Ordinal)) continue;
    var notice = Path.Combine(directory, "NOTICE.md");
    if (File.Exists(notice)) Copy(notice, Path.Combine(vendor, "licenses", $"{name}-NOTICE.md"));
}
string Modules(string path) => Path.Combine(project, "node_modules", path);
if (!File.Exists(Modules("@mermanjs/web/pkg/merman_wasm.js")) || !File.Exists(Modules("restty/dist/internal/runtime.js")))
    Run(project, null, OperatingSystem.IsWindows() ? "pnpm.exe" : "pnpm", "install", "--frozen-lockfile", "--ignore-scripts", "--dir", project);
foreach (var name in new[] { "merman_wasm.js", "merman_wasm_bg.wasm" })
    Copy(Modules("@mermanjs/web/pkg/" + name), Path.Combine(vendor, name));

Copy(Modules("@mermanjs/web/LICENSE"), Path.Combine(vendor, "Merman-LICENSE"));
// The tab host's Git: isomorphic-git's self-contained UMD bundle and jsdiff's ES modules.
Copy(Modules("isomorphic-git/index.umd.min.js"), Path.Combine(vendor, "isomorphic-git.js"));
var esbuild = Modules(".bin/" + (OperatingSystem.IsWindows() ? "esbuild.cmd" : "esbuild"));
// isomorphic-git expects a Buffer global in the page; the `buffer` package is bundled into one module that sets it.
Run(project, "import { Buffer } from 'buffer';\nglobalThis.Buffer ??= Buffer;\n", esbuild, "--bundle", "--format=esm", "--minify", $"--outfile={Path.Combine(vendor, "buffer.js")}", "--log-level=warning");
Copy(Modules("buffer/LICENSE"), Path.Combine(vendor, "Buffer-LICENSE"));
// The tab host's Pi agent: pi-agent-core and pi-ai bundled with the tab's file and Git tools (pi-ai carries every provider
// SDK; the tab only speaks HTTP to them, so the Mistral SDK is stubbed) and just-bash, its shell (gzip is stubbed: no node:zlib).
var piVersion = JsonDocument.Parse(File.ReadAllBytes(Modules("@mariozechner/pi-agent-core/package.json"))).RootElement.GetProperty("version").GetString()!;
Run(project, null, esbuild, "tabpi/agent.mjs", "--bundle", "--format=esm", "--platform=browser", "--minify",
    "--alias:@mistralai/mistralai=./tabpi/mistral-stub.mjs", "--alias:node:zlib=./tabpi/zlib-stub.mjs", "--define:process.env.NODE_ENV=\"production\"",
    $"--define:PI_VERSION={JsonSerializer.Serialize(JsonSerializer.Serialize(piVersion))}", $"--outfile={Path.Combine(vendor, "tabpi.js")}", "--log-level=warning");
// The version alone, so asking for it (Pi's status, on every start) does not fetch the agent bundle above.
File.WriteAllText(Path.Combine(vendor, "tabpi-version.js"), $"export const version = {JsonSerializer.Serialize(piVersion)};\n", utf8);
CopyIfPresent(Modules("just-bash/LICENSE"), Path.Combine(vendor, "JustBash-LICENSE"));
// The tab host's CSS language server runs in a Web Worker: vscode-css-languageservice behind the LSP, as one classic script.
Run(project, null, esbuild, "tabpi/csslsp.mjs", "--bundle", "--format=iife", "--platform=browser", "--minify", $"--outfile={Path.Combine(vendor, "csslsp.js")}", "--log-level=warning");
foreach (var (name, license) in new[] { ("VscodeCssLanguageservice", "vscode-css-languageservice"), ("VscodeLanguageserver", "vscode-languageserver"), ("VscodeLanguageserverTextdocument", "vscode-languageserver-textdocument") })
    CopyIfPresent(Modules(license + "/LICENSE.md"), Path.Combine(vendor, $"{name}-LICENSE"));
CopyIfPresent(Modules("@mariozechner/pi-agent-core/LICENSE"), Path.Combine(vendor, "PiAgentCore-LICENSE"));
Copy(Modules("isomorphic-git/http/web/index.js"), Path.Combine(vendor, "isomorphic-git-http.js"));
Copy(Modules("isomorphic-git/LICENSE.md"), Path.Combine(vendor, "IsomorphicGit-LICENSE"));
CopyDirectory(Modules("diff/libesm"), Path.Combine(vendor, "diff"));
Copy(Modules("diff/LICENSE"), Path.Combine(vendor, "Diff-LICENSE"));
CopyDirectory(Modules("restty/dist"), Path.Combine(vendor, "restty"));
// Restty 0.3.0 shrinks a glyph (both ways, so the bottom bar of "E" falls off) whenever the font's advance exceeds the cell
// it rounded to a whole pixel, e.g. 8.4 px in an 8 px cell at 14 px. Rounding is below half a pixel, so only shrink for more.
var chunk = Path.Combine(vendor, "restty/chunk-c0rcpsc5.js");
const string Shrink = "advancePx > maxWidth && advancePx > 0";
var source = Encoding.UTF8.GetString(File.ReadAllBytes(chunk));
if (source.Split(Shrink).Length != 3) throw new InvalidOperationException("Restty changed its glyph fit: update the patch in scripts/browser-assets.cs");
File.WriteAllBytes(chunk, utf8.GetBytes(source.Replace(Shrink, "advancePx > maxWidth + 0.5 && advancePx > 0")));
Copy(Modules("restty/LICENSE"), Path.Combine(vendor, "Restty-LICENSE"));
// pnpm keeps restty's dependencies beside its real directory, not at the top of node_modules.
Copy(Path.Combine(new DirectoryInfo(Modules("restty")).ResolveLinkTarget(true)?.FullName ?? Modules("restty"), "../text-shaper/LICENSE"), Path.Combine(vendor, "TextShaper-LICENSE"));
Copy(Path.Combine(root, "src/SharpRail.Plugins.UI.Kit/Assets/Fonts/JetBrainsMono-Regular.ttf"), Path.Combine(vendor, "JetBrainsMono-Regular.ttf"));
// PDFium's WASM build is pinned and checksummed in mise.browser.toml; `scripts/tool pdfium-wasm` prints where mise keeps it.
var cache = Run(root, null, "sh", Path.Combine(root, "scripts/tool"), "pdfium-wasm").Trim();
Copy(Path.Combine(cache, "lib/pdfium.wasm"), Path.Combine(vendor, "pdfium.wasm"));
var glue = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(cache, "lib/pdfium.js")));
File.WriteAllBytes(Path.Combine(vendor, "pdfium.js"), utf8.GetBytes($$"""
    export default function loadPdfium(options) {
    return new Promise((resolve, reject) => {
    var Module = { ...options, onRuntimeInitialized: () => resolve(Module), onAbort: reject };

    """ + glue + "\n});\n}\n"));
Copy(Path.Combine(cache, "LICENSE"), Path.Combine(vendor, "PDFium-LICENSE"));
return 0;

static void Copy(string from, string to) => File.Copy(from, to, overwrite: true);

static void CopyIfPresent(string from, string to)
{
    if (File.Exists(from)) Copy(from, to);
}

static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from)) Copy(file, Path.Combine(to, Path.GetFileName(file)));
    foreach (var directory in Directory.GetDirectories(from)) CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
}

// Runs a program with an argument list; its standard error and, unless captured by the caller's need, output pass through.
static string Run(string workingDirectory, string? input, string file, params string[] arguments)
{
    var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardInput = input is not null, RedirectStandardOutput = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    if (input is not null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{file} failed: {process.ExitCode}");
    if (file.StartsWith("pnpm", StringComparison.Ordinal)) Console.Write(output);
    return output;
}
