using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// Values the Settings composer offers for settings.json keys whose documented value set is closed. They are
/// suggestions, never a restriction: a newer Claude Code may accept more, so the field still takes any string.
/// Sources: S = https://json.schemastore.org/claude-code-settings.json (the schema Claude Code's settings reference
/// links), M = https://code.claude.com/docs/en/model-config (model aliases, per-model effort levels),
/// H = <c>claude --help</c> 2.1.310 (<c>--permission-mode</c> choices).
/// </summary>
internal static class ClaudeSettingSuggestions
{
    // A '*' segment matches exactly one key segment, such as a model id under modelSettings.
    private static readonly (string Pattern, string[] Values)[] Strings =
    [
        ("permissions.defaultMode", ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions", "manual", "delegate"]), // S, H
        ("model", ["default", "best", "fable", "opus", "sonnet", "haiku", "opus[1m]", "sonnet[1m]", "opusplan"]), // M
        ("advisorModel", ["opus", "sonnet", "fable"]), // S
        ("effortLevel", ["low", "medium", "high", "xhigh"]), // S, M (max is session-only)
        ("modelSettings.*.effortLevel", ["low", "medium", "high", "xhigh"]), // M
        ("theme", ["auto", "dark", "light", "dark-daltonized", "light-daltonized", "dark-ansi", "light-ansi"]), // S
        ("tui", ["default", "fullscreen"]), // S
        ("outputStyle", ["default", "Proactive", "Explanatory", "Learning"]), // S (built-ins; custom styles exist)
        ("forceLoginMethod", ["claudeai", "console", "gateway"]), // S
        ("autoUpdatesChannel", ["stable", "latest"]), // S
        ("editorMode", ["normal", "vim"]), // S
        ("viewMode", ["default", "verbose", "focus"]), // S
        ("teammateMode", ["auto", "in-process", "tmux", "iterm2"]), // S
        ("defaultShell", ["bash", "powershell"]), // S
        ("diffTool", ["auto", "terminal"]), // S
        ("parentSettingsBehavior", ["first-wins", "merge"]), // S
        ("preferredNotifChannel", ["auto", "terminal_bell", "iterm2", "iterm2_with_bell", "kitty", "ghostty", "notifications_disabled"]), // S
        ("askUserQuestionTimeout", ["60s", "5m", "10m", "never"]), // S
        ("workflowSizeGuideline", ["unrestricted", "small", "medium", "large"]), // S
        ("spinnerVerbs.mode", ["append", "replace"]), // S
        ("skillOverrides.*", ["on", "name-only", "user-invocable-only", "off"]), // S
        ("voice.mode", ["hold", "tap"]), // S
        ("worktree.baseRef", ["fresh", "head"]), // S
        ("worktree.bgIsolation", ["worktree", "none"]), // S
        ("permissions.disableBypassPermissionsMode", ["disable"]), // S
        ("disableAutoMode", ["disable"]) // S
    ];

    // Keys the schema (S) types as boolean; composed as on/off so they are written unquoted.
    private static readonly HashSet<string> Booleans = new(StringComparer.Ordinal)
    {
        "alwaysThinkingEnabled", "autoCompactEnabled", "autoConnectIde", "autoMemoryEnabled", "autoScrollEnabled",
        "awaySummaryEnabled", "disableAgentView", "disableAllHooks", "enableAllProjectMcpServers", "fastMode",
        "fileCheckpointingEnabled", "includeCoAuthoredBy", "includeGitInstructions", "prefersReducedMotion",
        "respectGitignore", "showThinkingSummaries", "showTurnDuration", "skipDangerousModePermissionPrompt",
        "spinnerTipsEnabled", "terminalProgressBarEnabled", "useAutoModeDuringPlan", "verbose", "voiceEnabled",
        "attribution.sessionUrl", "sandbox.enabled", "sandbox.allowUnsandboxedCommands", "sandbox.autoAllowBashIfSandboxed",
        "sandbox.failIfUnavailable", "voice.enabled", "voice.autoSubmit"
    };

    private static bool Matches(string pattern, string key)
    {
        var want = pattern.Split('.');
        var have = key.Split('.');
        return want.Length == have.Length && want.Zip(have).All(pair => pair.First == "*" ? pair.Second.Length > 0 : pair.First == pair.Second);
    }

    /// <summary>The known string values for <paramref name="key"/>, or null when its set is open or unknown.</summary>
    public static IReadOnlyList<string>? For(string key) =>
        Strings.FirstOrDefault(entry => Matches(entry.Pattern, key)).Values;

    /// <summary>The shape a key is documented with: on/off for booleans, text for keys with suggestions.</summary>
    public static ValueShape? ShapeFor(string key) =>
        Booleans.Contains(key) ? ValueShape.Switch : For(key) is not null ? ValueShape.Text : null;
}