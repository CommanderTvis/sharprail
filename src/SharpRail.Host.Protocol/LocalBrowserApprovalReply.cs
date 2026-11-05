using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpRail.Host.Protocol;

/// <summary>A temporary, origin-bound browser credential issued after approval in the local app.</summary>
public sealed record LocalBrowserApprovalReply(string Token);

/// <summary>The reply's source-generated JSON metadata, with the web defaults ASP.NET writes and System.Net.Http.Json reads.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LocalBrowserApprovalReply))]
public sealed partial class LocalBrowserApprovalJson : JsonSerializerContext;