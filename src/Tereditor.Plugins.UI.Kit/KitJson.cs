using System.Text.Json.Serialization;

using Tereditor.Plugins.UI.Kit.Editor;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>Source-generated JSON metadata, with the serializer's default options, for the values the kit reads and writes itself.</summary>
[JsonSerializable(typeof(CustomGrammar[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(double))]
internal sealed partial class KitJson : JsonSerializerContext;