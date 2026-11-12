namespace SharpRail.Plugins.Chat.UI;

/// <summary>Elapsed seconds as the chat words them: <c>45s</c>, <c>10m 27s</c>, <c>1h 02m 05s</c>.</summary>
internal static class ChatDuration
{
    public static string Format(long seconds)
    {
        seconds = Math.Max(0, seconds);
        var (hours, minutes, rest) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        return hours > 0 ? $"{hours}h {minutes:00}m {rest:00}s" : minutes > 0 ? $"{minutes}m {rest:00}s" : $"{rest}s";
    }
}