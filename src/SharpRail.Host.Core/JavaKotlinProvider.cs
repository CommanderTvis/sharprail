using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    /// <summary>Whether a provider choice names <c>standard</c> or a server that replaces others (H21).</summary>
    internal static bool IsProviderChoice(string value) =>
        value == "standard" || Language.LanguageServers.Known(value)?.Spec.Replaces is { Count: > 0 };

    /// <summary>The host-wide choice from older per-workspace ones: a replacing server chosen in every workspace that configured it.</summary>
    private void MigrateJavaKotlinProvider()
    {
        if (state.Settings.JavaKotlinProvider.Length > 0) return;
        var chosen = "standard";
        foreach (var replacing in Language.LanguageServers.Catalog.Where(server => server.Spec.Replaces.Count > 0))
        {
            var choices = new HashSet<bool>();
            foreach (var configuration in state.WorkspaceLanguages.Values)
            {
                try
                {
                    using var json = JsonDocument.Parse(configuration);
                    if (json.RootElement.TryGetProperty(replacing.Id, out var server) &&
                        server.TryGetProperty("useForJavaAndKotlin", out var choice) && choice.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        choices.Add(choice.GetBoolean());
                }
                catch (JsonException error) { choices.Add(false); LastError ??= error.Message; }
            }
            if (choices.SetEquals([true])) { chosen = replacing.Id; break; }
        }
        state = state with { Settings = state.Settings with { JavaKotlinProvider = chosen } };
    }

    internal async ValueTask<LanguageResult> JavaKotlinProviderAsync(LanguageRequest request, CancellationToken token)
    {
        if (request.Operation == "setJavaKotlinProvider")
        {
            if (request.Configuration is not { } choice || !IsProviderChoice(choice)) throw new ArgumentException("Choose standard or a server that replaces others.");
            await ChangeAsync([HostStateChange.Setting("java-kotlin-provider", request.Configuration)], token);
        }
        return new("ready", "Language provider choice for every project on this host.") { Configuration = Current.Settings.JavaKotlinProvider };
    }
}