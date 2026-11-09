namespace SharpRail.UI.Panels;

/// <summary>What the Settings pane knows about a server's license terms; only a known state may offer Review and accept terms.</summary>
internal enum TermsKnowledge { Checking, NotInstalled, NotAccepted, Accepted, Unavailable }

/// <summary>How a terms state is shown: the status line, whether it is an error, and the review button.</summary>
internal readonly record struct TermsStatusView(string Text, bool Failed, bool ShowReview, bool ReviewEnabled)
{
    internal static TermsStatusView Of(TermsKnowledge knowledge, string acceptedAtUtc = "", string version = "", string detail = "") => knowledge switch
    {
        TermsKnowledge.Accepted => new($"Terms accepted on {acceptedAtUtc}" + (version.Length > 0 ? $" (version {version})." : "."), false, true, false),
        TermsKnowledge.NotAccepted => new("Terms not accepted. Choose Review and accept terms. Nothing starts until you accept.", false, true, true),
        TermsKnowledge.NotInstalled => new("Terms not accepted. Reading them downloads the pinned managed server first.", false, true, true),
        TermsKnowledge.Unavailable => new("Terms unavailable: " + detail, true, true, true),
        _ => new("Checking license terms…", false, false, false),
    };
}

/// <summary>The last known acceptance per server, so reopening the pane shows it at once while the host is read again; a new server version drops it.</summary>
internal static class TermsAcceptanceCache
{
    private static readonly Dictionary<string, (string ServerVersion, string AcceptedAtUtc, string Version)> entries = [];

    internal static void Store(string server, string serverVersion, string acceptedAtUtc, string version)
    {
        lock (entries) entries[server] = (serverVersion, acceptedAtUtc, version);
    }

    internal static void Forget(string server)
    {
        lock (entries) entries.Remove(server);
    }

    internal static bool TryGet(string server, string serverVersion, out string acceptedAtUtc, out string version)
    {
        lock (entries)
        {
            if (entries.TryGetValue(server, out var entry))
            {
                if (entry.ServerVersion == serverVersion) { acceptedAtUtc = entry.AcceptedAtUtc; version = entry.Version; return true; }
                entries.Remove(server);
            }
        }
        acceptedAtUtc = version = "";
        return false;
    }
}