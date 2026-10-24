namespace SharpRail.UI.State;

public enum ToastVariant { Info, Success, Error }

/// <summary>How a toast's action button looks: an accent text link, a filled accent button or a plain button.</summary>
public enum ToastActionStyle { Link, Primary, Plain }

/// <summary>
/// One toast choice. A plain action dismisses its toast and runs. An <see cref="Async"/> one disables the toast's
/// buttons while it runs, shows its failure inline, and dismisses the toast on success unless it does not
/// <see cref="Closes"/>.
/// </summary>
public sealed record ToastAction(string Label, Action Invoke, ToastActionStyle Style = ToastActionStyle.Link)
{
    public Func<Task>? RunAsync { get; init; }
    public bool Closes { get; init; } = true;

    public static ToastAction Async(string label, Func<Task> run, ToastActionStyle style = ToastActionStyle.Link, bool closes = true) =>
        new(label, () => _ = run(), style) { RunAsync = run, Closes = closes };
}

/// <summary>
/// A null <see cref="Duration"/> takes the variant's default: errors stay until dismissed, and a
/// <see cref="Sticky"/> toast stays until dismissed or taken down by its owner. One link action sits beside the
/// message; more actions, or any button-styled one, share a row under it. <see cref="Code"/> is shown in the code
/// font under the message. <see cref="Countdown"/> shows the seconds left of its lifetime beside the message.
/// </summary>
public sealed record Toast(long Id, ToastVariant Variant, string Message, string? Title = null, TimeSpan? Duration = null, IReadOnlyList<ToastAction>? Actions = null,
    bool Countdown = false, string? Code = null, bool Sticky = false)
{
    public IReadOnlyList<ToastAction> Choices => Actions ?? [];
    public ToastAction? Action => Choices.Count > 0 ? Choices[0] : null;
    public ToastAction? Secondary => Choices.Count > 1 ? Choices[1] : null;
    public TimeSpan? Lifetime => Sticky ? null : Duration ?? (Variant == ToastVariant.Error ? null : ToastQueue.DefaultDuration);
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
    public long Push(ToastVariant variant, string message, string? title = null, TimeSpan? duration = null, ToastAction? action = null, ToastAction? secondary = null,
        bool countdown = false, IReadOnlyList<ToastAction>? actions = null, string? code = null, bool sticky = false)
    {
        actions ??= new[] { action, secondary }.OfType<ToastAction>().ToArray();
        if (actions.Count == 0 && code is null && !sticky && items.Find(item => item.Action is null && item is { Code: null, Sticky: false } && item.Variant == variant && item.Title == title &&
            item.Message == message && item.Duration == duration) is { } twin) return twin.Id;
        var toast = new Toast(++next, variant, message, title, duration, actions, countdown, code, sticky);
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