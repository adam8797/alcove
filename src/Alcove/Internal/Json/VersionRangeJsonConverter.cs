using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;

namespace Alcove.Internal.Json;

/// <summary>Writes a <see cref="VersionRange"/> as its normalized string (e.g. <c>"[2.0.0, 3.0.0)"</c>).</summary>
internal sealed class VersionRangeJsonConverter : JsonConverter<VersionRange>
{
    public override VersionRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is null || !VersionRange.TryParse(text, out var range))
        {
            throw new JsonException($"'{text}' is not a valid NuGet version range.");
        }

        return range;
    }

    public override void Write(Utf8JsonWriter writer, VersionRange value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToNormalizedString());
}
