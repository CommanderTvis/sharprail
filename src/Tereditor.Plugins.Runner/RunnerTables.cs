namespace Tereditor.Plugins.Runner;

/// <summary>The member tables this runner was generated with, which the host compares in the handshake.</summary>
internal static class RunnerTables
{
    /// <summary>The host context's table hash, a dot, and the UI context's.</summary>
    public const string Hash = PluginHostContextMembers.Hash + "." + PluginUIContextMembers.Hash;
}