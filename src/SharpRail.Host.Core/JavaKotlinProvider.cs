using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private void MigrateJavaKotlinProvider()
    {
        if (state.Settings.JavaKotlinProvider.Length > 0) return;
        var choices = new HashSet<bool>();
        foreach (var configuration in state.WorkspaceLanguages.Values)
        {
            try
            {
                using var json = JsonDocument.Parse(configuration);
                if (json.RootElement.TryGetProperty("intellij", out var server) &&
                    server.TryGetProperty("useForJavaAndKotlin", out var choice) && choice.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    choices.Add(choice.GetBoolean());
            }
            catch (JsonException error) { choices.Add(false); LastError ??= error.Message; }
        }
        state = state with { Settings = state.Settings with { JavaKotlinProvider = choices.SetEquals([true]) ? "intellij" : "standard" } };
    }

    internal async ValueTask<LanguageResult> JavaKotlinProviderAsync(LanguageRequest request, CancellationToken token)
    {
        if (request.Operation == "setJavaKotlinProvider")
        {
            if (request.Configuration is not ("standard" or "intellij")) throw new ArgumentException("Choose standard or intellij.");
            await ChangeAsync([HostStateChange.Setting("java-kotlin-provider", request.Configuration)], token);
        }
        return new("ready", "Java / Kotlin provider for every project on this host.") { Configuration = Current.Settings.JavaKotlinProvider };
    }
}