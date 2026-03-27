using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Converters;

/// <summary>
/// Handles AHK config files that store boolean-like settings as JSON true/false
/// while the C# model uses int (0/1). Also handles string numbers ("0"/"1").
/// </summary>
public sealed class BoolOrIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32(),
            JsonTokenType.True   => 1,
            JsonTokenType.False  => 0,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var v) ? v : 0,
            _                    => 0
        };
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
