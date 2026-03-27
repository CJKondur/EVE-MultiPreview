using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Converters;

/// <summary>
/// Handles AHK config files that store colors or other strings as raw numbers (e.g. 333333 instead of "333333").
/// Also gracefully converts booleans and nulls into strings to prevent deserialization crashes.
/// </summary>
public sealed class StringFallbackConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "1", // AHK typically represents true as 1
            JsonTokenType.False => "0", // AHK typically represents false as 0
            JsonTokenType.Null => string.Empty,
            _ => string.Empty
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
