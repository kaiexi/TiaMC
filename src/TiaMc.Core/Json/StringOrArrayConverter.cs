using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Json;

/// <summary>
/// Minecraft version JSON uses a polymorphic "value" for argument entries:
/// either a plain string, or an array of strings. This converter accepts both
/// and normalises everything into <see cref="List{T}"/> of string.
/// </summary>
public sealed class StringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = new List<string>();
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                list.Add(reader.GetString() ?? string.Empty);
                break;
            case JsonTokenType.StartArray:
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        list.Add(reader.GetString() ?? string.Empty);
                    }
                    else
                    {
                        // Non string member (should not happen for arguments) - skip it.
                        using var doc = JsonDocument.ParseValue(ref reader);
                    }
                }
                break;
            case JsonTokenType.Null:
                break;
            default:
                using (var doc = JsonDocument.ParseValue(ref reader)) { }
                break;
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
