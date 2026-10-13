using System.Security.Cryptography;
using System.Text;

namespace SharpRail.Plugins.Tailscale.Host;

internal sealed record ApprovedDevice(string Hash, string NodeId, string Name, string User);
internal sealed record PendingDevice(ApprovedDevice Device, DateTimeOffset Expires, bool Rejected = false);

internal sealed class PairingRegistry(IReadOnlyList<ApprovedDevice> restored, Action<IReadOnlyList<ApprovedDevice>> save, Func<DateTimeOffset>? clock = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, ApprovedDevice> approved = restored.GroupBy(device => device.Hash).ToDictionary(group => group.Key, group => group.First());
    private readonly Dictionary<string, PendingDevice> pending = [];
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    internal static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    internal static bool ValidToken(string token) => token.Length == 64 && token.All(char.IsAsciiHexDigit);

    internal string Request(string token, string nodeId, string name, string user)
    {
        if (!ValidToken(token)) return "invalid";
        lock (gate)
        {
            Expire();
            var hash = Hash(token);
            if (approved.TryGetValue(hash, out var known)) return known.NodeId == nodeId ? "approved" : "denied";
            if (pending.TryGetValue(hash, out var waiting))
                return waiting.Device.NodeId != nodeId || waiting.Rejected ? "denied" : "pending";
            if (pending.Count >= 32) return "busy";
            pending.Add(hash, new(new(hash, nodeId, name, user), Now.AddMinutes(2)));
            return "pending";
        }
    }

    internal bool Authenticate(string token)
    {
        if (!ValidToken(token)) return false;
        lock (gate) return approved.ContainsKey(Hash(token));
    }

    internal void Decide(string id, bool allow)
    {
        lock (gate)
        {
            Expire();
            if (!pending.TryGetValue(id, out var entry) || entry.Rejected) throw new InvalidOperationException("This pairing request has expired.");
            if (allow)
            {
                if (approved.Count >= 128) throw new InvalidOperationException("Remove a remembered device before approving another.");
                var next = approved.Values.Append(entry.Device).ToArray();
                save(next);
                approved.Add(id, entry.Device);
                pending.Remove(id);
            }
            else pending[id] = entry with { Rejected = true };
        }
    }

    internal void Revoke(string id)
    {
        lock (gate)
        {
            if (!approved.TryGetValue(id, out var revoked)) return;
            var next = approved.Values.Where(device => device.NodeId != revoked.NodeId).ToArray();
            save(next);
            foreach (var key in approved.Values.Where(device => device.NodeId == revoked.NodeId).Select(device => device.Hash).ToArray()) approved.Remove(key);
        }
    }

    internal TailscaleStatus Status(string detail, string? hostName)
    {
        lock (gate)
        {
            Expire();
            return new(detail, hostName, pending.Values.Where(entry => !entry.Rejected).Select(entry => Display(entry.Device)).ToArray(), approved.Values.GroupBy(device => device.NodeId).Select(group => Display(group.First())).ToArray());
        }
    }

    private static TailscaleDevice Display(ApprovedDevice device) => new(device.Hash, device.Name, device.User, device.Hash[..8].ToUpperInvariant());
    private void Expire()
    {
        foreach (var key in pending.Where(entry => entry.Value.Expires <= Now).Select(entry => entry.Key).ToArray()) pending.Remove(key);
    }
}