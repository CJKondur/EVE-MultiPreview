using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

/// <summary>
/// Reads a boolean from JSON <c>true</c>/<c>false</c>, a number (0 = false, non-zero
/// = true), or a string ("true"/"false"/"1"/"0"). Writes native <c>true</c>/<c>false</c>.
///
/// EVE MultiPreview is an AHK-compatible port: the AHK EVE-O-Preview config (and the
/// rest of <see cref="AhkConfigRoot"/>) stores every boolean as an integer 0/1. A raw
/// <c>bool</c> property throws "could not be converted to System.Boolean" on such a
/// value, and because that aborts the whole deserialization the user loses ALL their
/// settings. Applying this converter to crop booleans keeps loading resilient (#69).
/// </summary>
public sealed class FlexibleBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var n) ? n != 0 : reader.GetDouble() != 0;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (bool.TryParse(s, out var b)) return b;
                if (long.TryParse(s, out var sn)) return sn != 0;
                return false;
            case JsonTokenType.Null:
                return false;
            default:
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
