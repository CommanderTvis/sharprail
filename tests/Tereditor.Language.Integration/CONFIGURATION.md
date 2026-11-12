# Provider configuration controls

The host catalog owns versioned schemas; local and remote UI clients render the
same fields and save into existing workspace configuration. Controls expose a
curated subset, not every upstream preference. Advanced JSON remains available.
Unset fields retain defaults, and unknown JSON properties survive named-field edits.
The UI labels catalog versions and warns that custom executables and SDK-bundled
servers can differ. A PATH executable is not verified as the catalog build.

Public protocol/configuration references used for the controls:

- TypeScript Language Server 6.0.2, with managed TypeScript 5.9.3:
  https://github.com/typescript-language-server/typescript-language-server/blob/master/docs/configuration.md
- Pyright 1.1.414: https://github.com/microsoft/pyright/blob/main/docs/settings.md
- Roslyn 5.13.0-1.26507.4: https://github.com/dotnet/roslyn/tree/main/src/LanguageServer
- Eclipse JDT LS 1.61.0: https://github.com/eclipse-jdtls/eclipse.jdt.ls
- JetBrains Kotlin and IntelliJ 263.6379.0:
  https://github.com/Kotlin/kotlin-lsp/blob/main/vscode-extension-core/src/initializationSettings.ts
  and https://github.com/JetBrains/intellij-lsp.nvim/blob/main/lua/intellij-lsp/client.lua
- gopls v0.23.0: https://github.com/golang/tools/blob/master/gopls/doc/settings.md
- rust-analyzer 2026-10-12: https://rust-analyzer.github.io/book/configuration.html
- Intelephense 1.18.5: https://github.com/bmewburn/vscode-intelephense/blob/master/package.json
- Ruby LSP 0.26.11: https://shopify.github.io/ruby-lsp/editors.html
- Metals 1.6.9: https://scalameta.org/metals/docs/editors/user-configuration/
- Bash Language Server 5.8.1: https://github.com/bash-lsp/bash-language-server/blob/main/server/src/config.ts
- CSS/SCSS/Less packaging 4.10.0: https://github.com/microsoft/vscode-css-languageservice
- PowerShell Editor Services 4.7.0: https://github.com/PowerShell/PowerShellEditorServices
- R languageserver 0.3.17: https://github.com/REditorSupport/languageserver
- SourceKit-LSP, host SDK: https://github.com/swiftlang/sourcekit-lsp/blob/main/Documentation/Configuration%20File.md
- Dart, host SDK (validation used 3.13.5): https://github.com/dart-lang/sdk/blob/main/pkg/analysis_server/tool/lsp_spec/README.md

These links are upstream references, not immutable schema snapshots. The tested
catalog pins are immutable version selections, but exhaustive compatibility of
every preference is not established by the real-project matrix. Its reports
establish the exercised operations only. SourceKit-LSP and Dart remain explicitly
SDK-dependent; no independent binary/version pin or SDK installation is claimed.
IntelliJ's proprietary terms and user acknowledgement remain separate from its
Apache-licensed client sources. No configuration field grants a license.
