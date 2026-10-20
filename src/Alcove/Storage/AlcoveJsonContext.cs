using System.Text.Json;
using System.Text.Json.Serialization;
using Alcove.Trust;

namespace Alcove.Storage;

/// <summary>Source-generated serialisation of the configuration file and the state file.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(NuGetVersionJsonConverter), typeof(VersionRangeJsonConverter)])]
[JsonSerializable(typeof(PluginHostOptions))]
[JsonSerializable(typeof(TrustPolicy))]
[JsonSerializable(typeof(StateDocument))]
internal sealed partial class AlcoveJsonContext : JsonSerializerContext;
