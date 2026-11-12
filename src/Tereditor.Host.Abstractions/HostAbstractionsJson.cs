using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Host.Abstractions;

/// <summary>Source-generated JSON metadata, with the serializer's default options, for the values state changes carry as text.</summary>
[JsonSerializable(typeof(TerminalRef))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class HostAbstractionsJson : JsonSerializerContext;