using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Converters;

/// <summary>
/// A double that a hand-edited file can't make unreadable, as BoolOrIntConverter does for ints: a number, or a
/// number as text (invariant culture). Anything else (null, true, other text, an object) reads as NaN, so one odd
/// value never fails the whole file; the caller replaces a value it can't use with its default.
/// </summary>
public sealed class LenientDoubleConverter : JsonConverter<double>
{
    public override bool HandleNull => true;

    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDouble(out var number) ? number : double.NaN;
            case JsonTokenType.String:
                return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN;
            default:
                reader.Skip();   // an object or array is read to its end
                return double.NaN;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
