using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Converters;

/// <summary>
/// Handles AHK config files that store boolean-like settings as JSON true/false
/// while the C# model uses int (0/1). Also handles string numbers ("0"/"1"), and a
/// decimal (a hand-edited 5.5) reads as the nearest whole number instead of failing
/// the whole file.
/// </summary>
public sealed class BoolOrIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var whole) ? whole
                                  : reader.TryGetDouble(out var number) ? Nearest(number) : 0,
            JsonTokenType.True   => 1,
            JsonTokenType.False  => 0,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var v) ? v : 0,
            _                    => 0
        };
    }

    /// <summary>The nearest int (halves away from zero), held to the int range.</summary>
    private static int Nearest(double number) =>
        (int)Math.Clamp(Math.Round(number, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}
