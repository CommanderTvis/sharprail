using System.Globalization;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

/// <summary>Why a workspace sits on the Settled shelf.</summary>
public enum SettledReason
{
    /// <summary>The user parked it.</summary>
    ByYou,
    /// <summary>It stayed idle longer than the host's idle window.</summary>
    Idle
}

public sealed record SettledRow(WorkspaceRecord Workspace, SettledReason Reason);

/// <summary>A project's rows split into live ones and the Settled shelf, each in display order.</summary>
public sealed record WorkspacePartition(IReadOnlyList<WorkspaceRecord> Live, IReadOnlyList<SettledRow> Settled);

public enum WorkspaceOrder
{
    /// <summary>Newest created first: the rail's order, where a row never moves while you work.</summary>
    Created,
    /// <summary>Most recently active first: the switcher's order.</summary>
    Recent
}

/// <summary>
/// The one derivation of live and settled rows, shared by the rail, the header switcher and its caption so they never
/// disagree. It is pure: the caller supplies the clock, the host's idle window and the active-selection latch.
/// </summary>
public static class WorkspaceSettling
{
    /// <summary>How often clients re-evaluate the partition, so idle rows cross the window without a click.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    public const int ShelfPage = 10;
    public const int ShelfMore = 25;
    private const long Day = 24L * 60 * 60 * 1000;

    /// <summary>
    /// Why a workspace is settled, or null when it is live. Default never settles; the user's override beats the latch,
    /// so settling the workspace you are in moves it; the latched active workspace stays live; otherwise a row idle
    /// longer than <paramref name="idleDays"/> settles.
    /// </summary>
    public static SettledReason? Reason(WorkspaceRecord workspace, int? idleDays, long now, bool latchedLive = false)
    {
        if (workspace.Kind == WorkspaceKinds.Default) return null;
        if (workspace.SettledOverride == SettledOverrides.Settled) return SettledReason.ByYou;
        if (workspace.SettledOverride == SettledOverrides.Active || latchedLive) return null;
        return idleDays is { } days && workspace.LastActiveAt is { } last && now - last > days * Day ? SettledReason.Idle : null;
    }

    /// <summary>
    /// Splits <paramref name="rows"/>, the host's registry order (oldest created first, Default leading). Below the
    /// settling protocol every row is live in the host's own order, whatever stale fields they carry.
    /// </summary>
    public static WorkspacePartition Partition(IReadOnlyList<WorkspaceRecord> rows, bool supported, int? idleDays, long now,
        string? latchedLive = null, WorkspaceOrder order = WorkspaceOrder.Created)
    {
        if (!supported) return new(rows, []);
        var newest = rows.Where(row => row.Kind != WorkspaceKinds.Default).Reverse().ToArray();
        IEnumerable<WorkspaceRecord> ordered = order == WorkspaceOrder.Recent
            ? newest.OrderByDescending(row => row.LastActiveAt ?? 0)
            : newest;
        var live = rows.Where(row => row.Kind == WorkspaceKinds.Default).ToList();
        var settled = new List<SettledRow>();
        foreach (var row in ordered)
        {
            if (Reason(row, idleDays, now, row.Path == latchedLive) is { } reason) settled.Add(new(row, reason));
            else live.Add(row);
        }
        return new(live, settled);
    }

    /// <summary>The short reason chip of a settled row.</summary>
    public static string Chip(SettledRow row, long now) => row.Reason == SettledReason.ByYou ? "by you"
        : "idle " + Age(now - (row.Workspace.LastActiveAt ?? now));

    /// <summary>The chip's long form, for its tooltip.</summary>
    public static string Explanation(SettledRow row, long now) => row.Reason == SettledReason.ByYou ? "You settled this workspace."
        : $"No terminal input or commits for {Age(now - (row.Workspace.LastActiveAt ?? now))}.";

    private static string Age(long milliseconds)
    {
        var days = Math.Max(1, milliseconds / Day);
        return days >= 60 ? (days / 30).ToString(CultureInfo.InvariantCulture) + "mo"
            : days >= 14 ? (days / 7).ToString(CultureInfo.InvariantCulture) + "w"
            : days.ToString(CultureInfo.InvariantCulture) + "d";
    }
}