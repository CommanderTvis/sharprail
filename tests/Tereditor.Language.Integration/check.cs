#!/usr/bin/env dotnet
// Validates corpus.json without network, server launch or downloads, and proves malformed and startup-only scenarios are
// rejected: dotnet run --file tests/Tereditor.Language.Integration/check.cs
#:include ../../scripts/lib/Tasks.cs
#:include Integration.cs
using System.Text.Json.Nodes;
using static Tasks;

return Suite.Entry(() =>
{
    var corpus = Suite.ReadJson("corpus.json");
    Suite.ValidateCorpus(corpus);
    Action<JsonObject>[] mutations =
    [
        value => First(value)["revision"] = "main",
        value => First(value)["path"] = "../outside",
        value => First(value)["calls"] = new JsonArray(new JsonObject { ["tool"] = "lsp_status", ["arguments"] = new JsonObject { ["operation"] = "retry" } }),
        value => First(value)["calls"]![1]!["assert"] = "ignored-typo",
        value => value["cases"]!.AsArray().Add(First(value).DeepClone()),
    ];
    for (var index = 0; index < mutations.Length; index++)
    {
        var invalid = corpus.DeepClone().AsObject();
        mutations[index](invalid);
        try { Suite.ValidateCorpus(invalid); }
        catch (Exception) { continue; }
        throw new TaskFailure($"Malformed corpus {index + 1} was accepted.");
    }
    Console.WriteLine($"PASS {Suite.Items(corpus, "cases")!.Count} pinned scenarios; malformed and startup-only scenarios rejected");
    return 0;
});

static JsonObject First(JsonObject corpus) => corpus["cases"]![0]!.AsObject();
