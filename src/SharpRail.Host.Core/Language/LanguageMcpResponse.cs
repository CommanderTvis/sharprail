using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal static class LanguageMcpResponse
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Format(LanguageResult result, string operation, int offset, int limit, bool singleServer)
    {
        var response = new JsonObject { ["state"] = result.State, ["detail"] = Clip(result.Detail, 2_000) };
        if (operation == "logs")
        {
            response["detail"] = "Workspace server log; newest output retained.";
            response["server"] = result.Server;
            response["logs"] = result.Detail.Length > 12_000 ? result.Detail[^12_000..] : result.Detail;
            response["truncated"] = result.Truncated || result.Detail.Length > 12_000;
            return response.ToJsonString(Json);
        }
        if (result.Server.Length > 0) response["server"] = result.Server;
        if (result.Configuration.Length > 0) response["configuration"] = JsonNode.Parse(result.Configuration);
        if (result.ActiveConfiguration.Length > 0) response["activeConfiguration"] = JsonNode.Parse(result.ActiveConfiguration);
        if (operation is "status" or "retry" or "restart" or "source")
        {
            response["freshness"] = result.Freshness;
            response["coverage"] = result.Coverage;
        }
        if (operation == "source")
        {
            var source = result.Locations.FirstOrDefault();
            if (source is not null)
            {
                response["source"] = Item(source);
                response["totalCharacters"] = result.Total;
                response["offset"] = offset;
                response["nextOffset"] = result.Truncated ? JsonValue.Create(offset + source.Body.Length) : null;
                response["truncated"] = result.Truncated;
            }
            return response.ToJsonString(Json);
        }
        var remaining = 28_000;
        if (operation == "diagnostics")
        {
            response["freshness"] = result.Freshness;
            response["coverage"] = result.Coverage;
            response["summary"] = new JsonObject
            {
                ["files"] = result.Files.Count,
                ["checked"] = result.Files.Count(file => file.State == "checked"),
                ["pending"] = result.Files.Count(file => file.State == "pending"),
                ["skipped"] = result.Files.Count(file => file.State == "skipped"),
                ["errors"] = result.Files.Count(file => file.State == "error"),
                ["diagnostics"] = result.Diagnostics.Count
            };
            var diagnostics = result.Diagnostics.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line).ThenBy(item => item.Column).ThenBy(item => item.Text, StringComparer.Ordinal).ToArray();
            Page("diagnostics", diagnostics.Skip(offset).Take(limit).Select(Item), diagnostics.Length);
            var files = result.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
            Page("files", files.Skip(offset).Take(limit).Select(file => new JsonObject
            {
                ["path"] = file.Path,
                ["state"] = file.State,
                ["freshness"] = file.Freshness,
                ["detail"] = Clip(file.Detail, 500)
            }), files.Length);
        }
        else if (operation == "projects")
        {
            response["coverage"] = result.Coverage;
            response["freshness"] = result.Freshness;
            Page("projects", result.Projects.Select(project => new JsonObject
            {
                ["root"] = project.Root,
                ["manifest"] = project.Manifest,
                ["server"] = project.Server,
                ["kind"] = project.Kind,
                ["state"] = project.State,
                ["detail"] = project.Detail
            }), result.Total);
        }
        else if (operation is "catalog" or "servers")
            Page("servers", result.Servers.Skip(offset).Take(limit).Select(server => new JsonObject
            {
                ["id"] = server.Id,
                ["path"] = server.Path,
                ["state"] = server.State,
                ["detail"] = Clip(server.Detail, 1_000),
                ["version"] = server.Version,
                ["hasWorkspaceConfiguration"] = server.Configured,
                ["restartRequired"] = server.ActiveConfiguration.Length > 0 && server.ActiveConfiguration != server.Configuration,
                ["lastExit"] = server.Exit is not { } exit ? null : new JsonObject
                {
                    ["time"] = exit.Time,
                    ["reason"] = exit.Reason,
                    ["unexpected"] = exit.Unexpected,
                    ["requestedBy"] = exit.Initiator.Length == 0 ? null : exit.Initiator,
                    ["exitCode"] = exit.ExitCode,
                    ["signal"] = exit.Signal.Length == 0 ? null : exit.Signal,
                    ["uptimeSeconds"] = exit.UptimeSeconds,
                    ["lastMemoryBytes"] = exit.MemoryBytes == 0 ? null : exit.MemoryBytes,
                    ["details"] = Clip(exit.Details, 12_000)
                }
            }), result.Servers.Count);
        else if (operation is not ("status" or "retry" or "restart" or "stop" or "configurationSchema" or "configuration" or "configure"))
        {
            response["freshness"] = result.Freshness;
            response["coverage"] = result.Coverage;
            response["total"] = result.Total;
            Page("locations", result.Locations.Select(Item), result.Total, singleServer);
        }
        return response.ToJsonString(Json);

        void Page(string name, IEnumerable<JsonObject> items, int total, bool canContinue = true)
        {
            var values = new JsonArray();
            foreach (var item in items)
            {
                var length = item.ToJsonString(Json).Length + 1;
                if (length > remaining) break;
                remaining -= length;
                values.Add(item);
            }
            var more = offset + values.Count < total;
            response[name] = values;
            response[name + "Page"] = new JsonObject
            {
                ["total"] = total,
                ["offset"] = offset,
                ["returned"] = values.Count,
                ["truncated"] = more,
                ["nextOffset"] = more && canContinue && values.Count > 0 ? JsonValue.Create(offset + values.Count) : null
            };
            if (more)
            {
                response["truncated"] = true;
                response["continuation"] = canContinue && values.Count > 0
                    ? "More results exist. Use the corresponding page nextOffset. Coverage describes analysis, not the completeness of this displayed page."
                    : "Output is incomplete. Narrow paths/query or select a file to page a single language server.";
            }
        }
    }

    private static JsonObject Item(LanguageItem item)
    {
        var result = new JsonObject
        {
            ["path"] = item.Path,
            ["line"] = item.Line,
            ["column"] = item.Column,
            ["endLine"] = item.EndLine,
            ["endColumn"] = item.EndColumn
        };
        if (item.Text.Length > 0) result["text"] = Clip(item.Text, 2_000);
        if (item.Kind.Length > 0) result["kind"] = item.Kind;
        if (item.Source.Length > 0) result["source"] = item.Source;
        if (item.Preview.Length > 0) result["preview"] = item.Preview;
        if (item.Body.Length > 0) result["body"] = item.Body;
        if (item.Truncated || item.Text.Length > 2_000) result["truncated"] = true;
        return result;
    }

    private static string Clip(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        if (char.IsHighSurrogate(value[maximum - 1]) && char.IsLowSurrogate(value[maximum])) maximum--;
        return value[..maximum] + "… [truncated]";
    }
}