# Recorded real-project validation

Run on macOS ARM64, 2026-10-16, using the production headless MCP host and provisioners.
17 providers passed; IntelliJ remained blocked pending actual-user acceptance.
Full immutable project revisions and executable calls are in corpus.json. PASS requires
actual symbols and diagnostic publication, plus an external-file reference where listed.
A protocol-only hover does not establish semantic hover contents.

| Provider | Project | Source files | Result | Checked coverage |
| --- | --- | ---: | --- | --- |
| typescript | microsoft/TypeScript | 20803 | passed | Cross-file references, symbols, diagnostics |
| python | pallets/flask | 83 | passed | Cross-file references, symbols, diagnostics |
| csharp | Humanizr/Humanizer | 534 | passed | Cross-file references, symbols, diagnostics |
| clangd | libuv/libuv | 321 | passed | Cross-file references, symbols, diagnostics |
| java | google/guava | 3275 | passed | Cross-file references, symbols, diagnostics |
| kotlin | detekt/detekt | 1077 | passed | Cross-file references, symbols, diagnostics |
| intellij | square/okhttp | 573 | blocked | Cross-file references, symbols, diagnostics |
| go | spf13/cobra | 36 | passed | Cross-file references, symbols, diagnostics |
| rust | BurntSushi/ripgrep | 110 | passed | Cross-file references, symbols, diagnostics |
| ruby | rubocop/rubocop | 1791 | passed | Cross-file references, symbols, diagnostics |
| scala | scalameta/scalameta | 726 | passed | Cross-file references, symbols, diagnostics |
| php | laravel/framework | 3167 | passed | Cross-file references, symbols, diagnostics |
| r | tidyverse/dplyr | 195 | passed | Document symbols and diagnostics |
| powershell | PowerShell/PowerShellGet | 28 | passed | Document symbols and diagnostics |
| shell | git/git | 1238 | passed | Document symbols and diagnostics |
| css | twbs/bootstrap | 171 | passed | Document symbols and diagnostics |
| swift | apple/swift-nio | 555 | passed | Cross-file references, symbols, diagnostics |
| dart | dart-lang/http | 340 | passed | Cross-file references, symbols, diagnostics |

## Limits and prerequisites

IntelliJ was blocked at the actual-user EULA gate in isolated state. No real JetBrains
terms were accepted by this harness, and Kotlin passing does not prove IntelliJ compatibility.
The IntelliJ projects-array and importer-JDK changes have fixture coverage only for that provider.

The managed catalog pins server releases. Swift uses the installed Apple toolchain's
SourceKit-LSP; Dart used SDK 3.13.5. Neither has an independently pinned server binary.
Java 21 was selected for the real JVM corpus (then from SDKMAN; the runner now uses
the pinned `.tools/jdk`); detekt's Gradle 8 build rejected
Java 25. The server's bundled JVM and the project's importer JDK are different selections.
R required a usable CRAN runtime and native package build toolchain; no system installation
or Homebrew JDK was used. Named configuration fields are curated public settings, not
an exhaustive exact-release schema certification; see CONFIGURATION.md.

Scala and Swift require completed build/index work before cross-file results appear.
The runner polls up to 15 minutes rather than accepting empty references. The Scala
probe was exercised against a compiled multi-module Scalameta checkout. C#/Kotlin use
released .NET 10 and JVM Gradle projects respectively; no .NET 11 preview or Kotlin
Multiplatform compatibility is inferred. Diagnostic probes restore fixture bytes on exit.

## Fast verification

Release build passed with zero warnings/errors. Focused language and language UI checks,
manifest rejection checks, JavaScript syntax checks and touched-file whitespace checks passed.
All 82 changed C# files were scanned with the absolute workspace path; three subsequently
edited files were rescanned on the final build. Coverage was current, with no errors or
warnings. XAML is compiler-covered; these LSP scans do not cover Markdown or JSON documents.

The full four-lane gate did not pass: MarkdownDocumentE2E timed out, and its isolated
resume also timed out while opening LARGE.md. Other completed lanes passed. This unrelated
UI failure remains unresolved; the final R provisioning guard was verified by a subsequent
Release build and focused language run, not another full gate. Upstream Git scenarios remain
conditional on SHARPRAIL_TEST_GIT_SOURCE. No expensive corpus case is registered in the gate.


Follow-up, 2026-10-17: after rebasing onto main a8abf30f, Release and focused LSP/UI
checks passed. The full gate passed in aggregate after correcting a settings protobuf
field collision and one off-scale provider-form spacing value. The earlier Markdown
timeout did not recur. This follow-up did not rerun the expensive real-provider corpus.
