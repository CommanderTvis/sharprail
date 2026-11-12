using System.Collections.Concurrent;
using System.Globalization;

using Avalonia.Data.Converters;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

internal sealed class LanguageServerNameConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, LanguageServerPresentation> Presentations = new(StringComparer.Ordinal);

    /// <summary>Keeps what the providing plugins say to show for each listed server (H21), so a server named only by id shows the same way.</summary>
    internal static void Remember(IEnumerable<LanguageServerState> servers)
    {
        foreach (var server in servers)
            if (server.Presentation is { } presentation) Presentations[server.Id] = presentation;
    }

    internal static LanguageServerPresentation? Presentation(string id) => Presentations.GetValueOrDefault(id);

    internal static string Display(string id) => Presentation(id) is { Name.Length: > 0 } presentation ? presentation.Name : id;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string id ? Display(id) : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}