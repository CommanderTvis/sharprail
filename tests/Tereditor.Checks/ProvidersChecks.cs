using System.Text.Json;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;

namespace Tereditor.Checks;

// The Model providers plugin: keys for Pi (auth.json) and omp (the tereditor profile's .env) are written without disturbing
// what else the files hold, are never returned, and a Claude subscription sign-in is never offered.
internal static class ProvidersChecks
{
    private const string Secret = "sk-fixture-9f3a7c2b";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("providers: " + message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("providers: " + message);
    }

    internal static void Run(string root)
    {
        var manifest = ProvidersManifest.Manifest;
        Require(manifest.UnavailableOn.Count == 0, "The keys are files of the host's disk, which a browser tab has too.");

        var directory = Path.Combine(root, "providers");
        Directory.CreateDirectory(directory);
        var auth = Path.Combine(directory, "pi", "auth.json");
        var env = Path.Combine(directory, "omp", ".env");
        var remembered = new List<CustomProvider>();
        var environment = new Dictionary<string, string?> { ["GROQ_API_KEY"] = "from-environment" };
        var service = new ProvidersService(new ProviderFiles(() => auth, () => env, () => remembered, custom => { remembered = [.. custom]; }, name => environment.GetValueOrDefault(name)));

        // Pi: a sign-in Pi stored stays untouched, a key lands beside it, a Claude subscription is called out.
        Directory.CreateDirectory(Path.GetDirectoryName(auth)!);
        File.WriteAllText(auth, """{"openai-codex":{"type":"oauth","access":"a","refresh":"r"},"anthropic":{"type":"oauth","access":"a","refresh":"r"}}""");
        var report = service.List(ProviderAgent.Pi);
        Require(report.Providers.Single(row => row.Id == "anthropic") is { Source: ProviderSource.SignIn, Note: { } warn } && warn.Contains("not supported", StringComparison.Ordinal), "A Claude subscription sign-in is flagged as unsupported.");
        Require(report.Providers.Single(row => row.Id == "openai-codex") is { Source: ProviderSource.SignIn, Custom: true }, "A sign-in the catalog does not list is still shown.");
        Require(report.Providers.Single(row => row.Id == "groq").Source == ProviderSource.Environment, "A key in the host's environment is shown as such.");

        report = service.SetKey(new(ProviderAgent.Pi, "openai", Secret));
        Require(report.Providers.Single(row => row.Id == "openai").Source == ProviderSource.Key, "A stored key makes the provider connected.");
        Require(!JsonSerializer.Serialize(report).Contains(Secret, StringComparison.Ordinal), "A report never carries a key.");
        var written = JsonDocument.Parse(File.ReadAllText(auth)).RootElement;
        Require(written.GetProperty("openai").GetProperty("key").GetString() == Secret && written.GetProperty("openai").GetProperty("type").GetString() == "api_key", "Pi's file holds the key as Pi reads it.");
        Require(written.GetProperty("openai-codex").GetProperty("type").GetString() == "oauth", "The sign-in beside the key is untouched.");
        if (!OperatingSystem.IsWindows()) Require(File.GetUnixFileMode(auth) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "The file is readable by its owner only.");

        report = service.SetKey(new(ProviderAgent.Pi, "anthropic", Secret));
        Require(report.Providers.Single(row => row.Id == "anthropic") is { Source: ProviderSource.Key }, "An API key replaces a subscription sign-in when the user chooses so.");
        report = service.RemoveKey(new(ProviderAgent.Pi, "openai"));
        Require(report.Providers.Single(row => row.Id == "openai").Source == ProviderSource.None && !File.ReadAllText(auth).Contains("\"openai\"", StringComparison.Ordinal), "Removing a key takes its entry out.");

        report = service.SetKey(new(ProviderAgent.Pi, "my-gateway", Secret) { Name = "My gateway" });
        Require(report.Providers.Single(row => row.Id == "my-gateway") is { Custom: true, Name: "My gateway", Source: ProviderSource.Key }, "A provider the lists do not name can be added for Pi.");
        service.RemoveKey(new(ProviderAgent.Pi, "my-gateway"));
        Require(remembered.Count == 0 && service.List(ProviderAgent.Pi).Providers.All(row => row.Id != "my-gateway"), "Removing a custom provider forgets it.");

        Throws(() => service.SetKey(new(ProviderAgent.Pi, "Bad Id", Secret)), "A provider id is validated.");
        Throws(() => service.SetKey(new(ProviderAgent.Pi, "openai", "  ")), "An empty key is refused.");
        Throws(() => service.SetKey(new(ProviderAgent.Pi, "openai", "a\nb")), "A key is one line.");

        // omp: a variable in the profile's .env; every other line stays.
        Directory.CreateDirectory(Path.GetDirectoryName(env)!);
        File.WriteAllText(env, "# my settings\nOTHER=1\nexport OPENAI_API_KEY=old\n");
        report = service.SetKey(new(ProviderAgent.Omp, "openai", "new value #1"));
        var lines = File.ReadAllLines(env);
        Require(lines.Contains("OTHER=1") && lines.Contains("# my settings") && lines.Count(line => line.Contains("OPENAI_API_KEY", StringComparison.Ordinal)) == 1 && lines.Contains("OPENAI_API_KEY=\"new value #1\""), "omp's key replaces the variable's line, quoted, and keeps the other lines.");
        Require(report.Providers.Single(row => row.Id == "openai").Source == ProviderSource.Key && !JsonSerializer.Serialize(report).Contains("new value", StringComparison.Ordinal), "omp's report shows the key as stored and never returns it.");
        Require(report.Note is { Length: > 0 }, "The report says which omp launches read the keys.");
        service.SetKey(new(ProviderAgent.Omp, "mistral", Secret));
        Require(File.ReadAllLines(env).Contains("MISTRAL_API_KEY=" + Secret), "A catalog provider uses its documented variable.");
        service.RemoveKey(new(ProviderAgent.Omp, "mistral"));
        Require(!File.ReadAllText(env).Contains("MISTRAL", StringComparison.Ordinal) && File.ReadAllText(env).Contains("OTHER=1", StringComparison.Ordinal), "Removing a key deletes only its line.");

        Throws(() => service.SetKey(new(ProviderAgent.Omp, "my-gateway", Secret)), "omp needs the variable for a provider the catalog does not know.");
        Throws(() => service.SetKey(new(ProviderAgent.Omp, "my-gateway", Secret) { EnvVar = "lower" }), "A variable name is validated.");
        report = service.SetKey(new(ProviderAgent.Omp, "my-gateway", Secret) { Name = "Gateway", EnvVar = "MY_GATEWAY_API_KEY" });
        Require(report.Providers.Single(row => row.Id == "my-gateway") is { Custom: true, EnvVar: "MY_GATEWAY_API_KEY", Source: ProviderSource.Key }, "A custom omp provider is remembered with its variable.");
        Require(service.List(ProviderAgent.Omp).Providers.Single(row => row.Id == "anthropic").Note == ProviderCatalog.AnthropicNote && service.List(ProviderAgent.Omp).Providers.All(row => row.Source != ProviderSource.SignIn), "Anthropic is offered by API key only, with the reason, and omp rows never show a sign-in.");
        Require(ProviderCatalog.Pi.Concat(ProviderCatalog.Omp).All(provider => ProviderCatalog.IsProviderId(provider.Id) && ProviderCatalog.IsEnvVar(provider.EnvVar)), "Every catalog entry has a valid id and variable.");
    }
}