---
name: add-highlighting
description: "Use when preparing custom TextMate JSON syntax highlighting for SharpRail file editors and diffs, including filename patterns for the Highlighting settings page."
---

# Add highlighting to SharpRail

Establish the language or development-file format, filename patterns, and a representative sample. If the request does not identify them, ask before choosing a grammar.

SharpRail Settings > Highlighting accepts a TextMate `.tmLanguage.json` grammar and comma-separated filename patterns (`*.foo, Foofile`). Patterns match the filename, ignoring case; directory patterns are unsupported. Adding the same scope replaces its existing custom grammar. Custom definitions override bundled filename detection and are shared through the connected host.

Prefer a maintained grammar from its original repository. Record its source revision and license beside the delivered artifact. Obtain any externally included grammars too, or make the grammar self-contained; SharpRail supports ordinary grammar includes but no cross-grammar injections. Do not execute or install a VS Code extension to extract its grammar.

If no suitable grammar exists, write a small self-contained grammar using standard TextMate scopes: `comment`, `keyword`, `storage`, `string`, `constant.numeric`, `entity.name.function`, `entity.name.type`, `variable`, and `punctuation`. Include a `scopeName`, a name, and patterns. Use begin/end rules for multiline constructs. Avoid regexes that can grow catastrophically on long input.

Deliver the JSON file within the authorized workspace and the exact filename patterns to enter. A grammar is limited to 512 Ki UTF-16 characters; up to 32 filename patterns are accepted, with 200 characters per pattern. Settings validates the grammar before saving it; all custom definitions together are limited to 2 Mi characters. Never edit SharpRail's state file directly. Tell the user to choose the artifact in Settings > Highlighting, enter its patterns, and select Add highlighting.

Verify comments, strings, keywords and representative edge cases using an available TextMate tokenizer. When that is unavailable, distinguish JSON validation from tokenization verification. Check the result in a file editor and an inline or split diff; diff hunk boundaries reset multiline state. SharpRail skips documents above 1 Mi characters and lines above 16 Ki units. Do not change application source or restart the app unless the user requests that work.
