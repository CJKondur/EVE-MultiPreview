using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Converters;

/// <summary>
/// For the ONE settings key GroupAlertProfiles (alerts per group, IDEAS #31), which holds one line of JSON TEXT in a
/// JSON string. It stands on that property by attribute, never in the serializer's options: every other key keeps
/// the loader's rule, by which a value of the wrong kind fails the whole file (saving then stays blocked, and the
/// player's file is never written over).
/// A text is read as it is. An OBJECT, written by hand where the text belongs, is taken as that object's text,
/// written out again from what was parsed: the settings file allows a comment and a comma too many, and either would
/// read as "no choices" inside the text. Anything else (a number, true, false, an array, null) is no choices, and
/// does not fail the file. Writing is the plain text, as a string is written: the saved line is what it always was.
/// </summary>
public sealed class GroupAlertChoicesTextConverter : JsonConverter<string>
{
    // As GroupAlertChoices writes its text: a name outside ASCII stays a name, not an escape six times its length.
    private static readonly JsonSerializerOptions AsTheChoicesAreWritten = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString() ?? "";
            case JsonTokenType.StartObject:
                // An object that is never closed fails here, and with it the whole file, as any broken JSON does.
                using (var parsed = JsonDocument.ParseValue(ref reader))
                {
                    try
                    {
                        return JsonSerializer.Serialize(parsed.RootElement, AsTheChoicesAreWritten);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                    {
                        // A name that can't be written out (half a character; the serializer reports that as a
                        // JsonException of its own): no choices, as in the text itself. The object was read whole.
                        return "";
                    }
                }
            default:
                reader.Skip();   // an array is read to its end; a number, true, false and null are one token
                return "";
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
