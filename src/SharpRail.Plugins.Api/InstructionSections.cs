namespace SharpRail.Plugins.Api;

/// <summary>
/// One section of the instructions SharpRail gives agent sessions (H20), as the instructions view lists it: the host's
/// own sections (<c>core.*</c>) and each enabled plugin's (<c>plugin.&lt;plugin id&gt;.&lt;contribution id&gt;</c>).
/// </summary>
public sealed class InstructionSection
{
    /// <summary>The stable identifier the project's <c>.sharprail/instructions.toml</c> names to turn the section off.</summary>
    public required string Id { get; init; }

    /// <summary><c>core</c> for the host's sections, else the contributing plugin's id.</summary>
    public required string Source { get; init; }

    /// <summary>The source's display name: <c>SharpRail</c> or the plugin's label.</summary>
    public required string SourceName { get; init; }

    /// <summary>A short human title.</summary>
    public required string Title { get; init; }

    /// <summary>One line on what the section tells the agent; may be empty.</summary>
    public string Description { get; init; } = "";

    /// <summary>The text as the next session in the workspace would get it, read-only.</summary>
    public required string Text { get; init; }

    /// <summary>Whether the project leaves the section on.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>The sections an agent's sessions in one workspace get, and the project file that turns sections off.</summary>
public sealed class InstructionSectionList
{
    /// <summary>The absolute path of the project's <c>.sharprail/instructions.toml</c>, which may not exist yet.</summary>
    public string Path { get; init; } = "";

    /// <summary>The sections, the host's first, then each plugin's in composition order.</summary>
    public IReadOnlyList<InstructionSection> Sections { get; init; } = [];

    /// <summary>Disabled identifiers no current section has (a plugin that is off or gone); kept in the file, ignored.</summary>
    public IReadOnlyList<string> UnknownDisabled { get; init; } = [];

    /// <summary>Problems reading the file; a broken file turns nothing off and refuses changes until fixed.</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>
    /// Where a copy of the user's earlier hand edits to the generated instructions was kept, once SharpRail stopped
    /// using them; null when there were none.
    /// </summary>
    public string? IgnoredEditsPath { get; init; }
}

/// <summary>Which workspace's instruction sections to list.</summary>
public sealed class InstructionSectionQuery
{
    /// <summary>The workspace's absolute path.</summary>
    public required string WorkspaceId { get; init; }
}

/// <summary>Turns one instruction section on or off for the workspace's project.</summary>
public sealed class InstructionSectionToggle
{
    /// <summary>The workspace's absolute path; the change applies to its whole project.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>The section's identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the section is on.</summary>
    public bool Enabled { get; init; }
}

/// <summary>A project's <c>.sharprail/instructions.toml</c> changed.</summary>
public sealed class InstructionSectionsChanged
{
    /// <summary>The project root whose file changed.</summary>
    public required string ProjectRoot { get; init; }
}
