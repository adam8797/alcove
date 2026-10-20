using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Alcove.Storage;

/// <summary>Writes a <see cref="NuGetVersion"/> as its full string (e.g. <c>"2.1.0-beta.1"</c>).</summary>
internal sealed class NuGetVersionJsonConverter : JsonConverter<NuGetVersion>
{
    public override NuGetVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null || !NuGetVersion.TryParse(text, out var version))
        {
            throw new JsonException($"'{text}' is not a valid NuGet version.");
        }

        return version;
    }

    public override void Write(Utf8JsonWriter writer, NuGetVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToFullString());
}
