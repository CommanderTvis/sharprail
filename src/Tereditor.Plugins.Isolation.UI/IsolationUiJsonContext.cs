using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Isolation.UI;

/// <summary>
/// The by-value UI API types that cross the plugin channel, which Api.UI cannot name itself because it carries no context
/// of its own. The generator's SRI003 rule names every type a non-pending member serializes that is missing here.
/// </summary>
[JsonSourceGenerationOptions(Converters = [typeof(PluginJson.EnumConverter<PluginNotificationKind>), typeof(PluginJson.EnumConverter<EditorKind>)])]
[JsonSerializable(typeof(PluginNotificationKind))]
[JsonSerializable(typeof(FileViewerProps))]
[JsonSerializable(typeof(PluginHostProjection))]
public sealed partial class IsolationUiJsonContext : JsonSerializerContext;