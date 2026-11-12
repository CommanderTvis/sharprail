using System.Text.Json.Nodes;

using SharpRail.Host.Core.Language;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

internal static class LanguageOptionChecks
{
    internal static void Run()
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        LanguageOptionField Field(string provider, string path) => new(JsonNode.Parse(LanguageConfigurationCatalog.Read(provider).Configuration)!["fields"]!.AsArray().Single(node => node!["path"]!.ToString() == path)!, _ => { }, true);
        var memory = Field("typescript", "maxTsServerMemory");
        memory.Load(null);
        Require(memory.IsNumber && memory.Number is null && !memory.HasOverride, "Unset memory must show a nullable numeric input.");
        memory.Number = 4096;
        Require(memory.ParseInteger() == 4096 && memory.HasOverride, "Numeric entry did not retain a typed integer override.");
        memory.Value = "0";
        try { memory.ParseInteger(); throw new InvalidOperationException("Nonpositive memory was accepted."); } catch (FormatException) { }
        memory.Number = null;
        Require(!memory.HasOverride, "Clearing numeric entry did not restore the server default.");
        var folders = Field("python", "python.analysis.extraPaths");
        folders.Load(new JsonArray());
        Require(folders.Multiline && folders.FolderPath && folders.HasOverride && folders.Value.Length == 0, "An explicit empty directory list became server defaults.");
        folders.Value = LanguageOptionField.Default;
        Require(!folders.HasOverride, "Resetting a list did not remove its override.");
        var features = Field("rust", "rust-analyzer.cargo.features");
        features.Load(JsonValue.Create("all"));
        Require(features.AllFeatures && !features.CustomJson, "Cargo all-features string was mistaken for an array.");
        features.Load(new JsonArray("all"));
        Require(!features.AllFeatures && features.Value == "all", "A feature named all was mistaken for all-features mode.");
        features.AllFeatures = true;
        Require(features.AllFeatures && features.HasOverride, "All-features toggle lost its explicit override.");
        var libraries = Field("java", "java.project.referencedLibraries");
        libraries.Load(JsonNode.Parse("""{"include":["lib/*.jar"],"exclude":["lib/test.jar"],"sources":{"lib/core.jar":"lib/core-sources.jar"}}"""));
        Require(libraries.CustomJson && !libraries.IsText && libraries.HasOverride, "Structured Java library configuration was converted to text lines.");
        var choice = Field("python", "python.analysis.typeCheckingMode");
        choice.Load(JsonValue.Create("future-mode"));
        Require(choice.Choices.Contains("future-mode") && choice.Value == "future-mode", "A custom provider choice was discarded during refresh.");
        var command = Field("rust", "rust-analyzer.check.command");
        Require(command.AllowCustomChoices && command.IsText && command.IsChoice, "Open-ended Cargo subcommands were restricted to built-in choices.");
        Require(Field("shell", "bashIde.globPattern") is { HasPath: false } && Field("go", "gopls.directoryFilters") is { HasPath: false }, "Glob/filter patterns must not use filesystem pickers.");
        foreach (var provider in LanguageServers.Catalog)
        {
            var fields = JsonNode.Parse(LanguageConfigurationCatalog.Read(provider.Id).Configuration)!["fields"]!.AsArray();
            foreach (var node in fields)
            {
                var option = new LanguageOptionField(node!, _ => { });
                option.Load(null);
                Require(!option.FilePath && !option.FolderPath, "Remote/default fields advertised a native filesystem picker.");
            }
        }
        Console.WriteLine("PASS provider option types, defaults, empty arrays, union values, custom choices and host-path boundaries");
    }
}