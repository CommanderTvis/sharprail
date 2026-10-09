namespace SharpRail.UI.State;

public enum ToastVariant { Info, Success, Error }

public sealed record ToastAction(string Label, Action Invoke);

/// <summary>A null <see cref="Duration"/> takes the variant's default: errors stay until dismissed.</summary>
public sealed record Toast(long Id, ToastVariant Variant, string Message, string? Title = null, TimeSpan? Duration = null, ToastAction? Action = null)
{
    public TimeSpan? Lifetime => Duration ?? (Variant == ToastVariant.Error ? null : ToastQueue.DefaultDuration);
}

/// <summary>One window's transient notifications, oldest first.</summary>
public sealed class ToastQueue
{
    public const int Capacity = 5;
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(5);

    private readonly List<Toast> items = [];
    private long next;

    public IReadOnlyList<Toast> Items => items;
    public event Action? Changed;

    /// <summary>
    /// Returns the id of the shown toast, which is an identical visible one when there is no action: actions name
    /// different inverses, so actionable toasts are never coalesced and the cap evicts only actionless ones.
    /// </summary>
    public long Push(ToastVariant variant, string message, string? title = null, TimeSpan? duration = null, ToastAction? action = null)
    {
        if (action is null && items.Find(item => item.Action is null && item.Variant == variant && item.Title == title &&
            item.Message == message && item.Duration == duration) is { } twin) return twin.Id;
        var toast = new Toast(++next, variant, message, title, duration, action);
        items.Add(toast);
        for (var index = 0; items.Count > Capacity && index < items.Count;)
            if (items[index].Action is null) items.RemoveAt(index); else index++;
        Changed?.Invoke();
        return toast.Id;
    }

    public void Dismiss(long id)
    {
        if (items.RemoveAll(item => item.Id == id) > 0) Changed?.Invoke();
    }
}