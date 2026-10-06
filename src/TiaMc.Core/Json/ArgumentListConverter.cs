using System.Text.Json;
using System.Text.Json.Serialization;

namespace TiaMc.Core.Json;

/// <summary>
/// The "arguments.game" / "arguments.jvm" arrays of a version JSON contain a mix
/// of plain strings and rule guarded objects. This converter accepts a whole
/// array and normalises every element into <see cref="Minecraft.ArgumentJson"/>.
/// It is registered as a list converter so it never recurses into itself.
/// </summary>
public sealed class ArgumentListConverter : JsonConverter<List<Minecraft.ArgumentJson>>
{
    public override List<Minecraft.ArgumentJson> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new List<Minecraft.ArgumentJson>();

        if (reader.TokenType == JsonTokenType.Null) return result;

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            using var skip = JsonDocument.ParseValue(ref reader);
            return result;
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    result.Add(new Minecraft.ArgumentJson { Value = [reader.GetString() ?? ""] });
                    break;

                case JsonTokenType.StartObject:
                {
                    using var doc = JsonDocument.ParseValue(ref reader);
                    var element = doc.RootElement;
                    var argument = new Minecraft.ArgumentJson();

                    if (element.TryGetProperty("value", out var value))
                    {
                        switch (value.ValueKind)
                        {
                            case JsonValueKind.String:
                                argument.Value.Add(value.GetString() ?? "");
                                break;
                            case JsonValueKind.Array:
                                foreach (var item in value.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.String)
                                    {
                                        argument.Value.Add(item.GetString() ?? "");
                                    }
                                }

                                break;
                        }
                    }

                    if (element.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                    {
                        argument.Rules = rules.Deserialize<List<Minecraft.RuleJson>>(options);
                    }

                    result.Add(argument);
                    break;
                }

                default:
                    using (JsonDocument.ParseValue(ref reader)) { }
                    break;
            }
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<Minecraft.ArgumentJson> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var argument in value)
        {
            if (argument.Rules is null)
            {
                foreach (var item in argument.Value) writer.WriteStringValue(item);
            }
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("value");
                if (argument.Value.Count == 1) writer.WriteStringValue(argument.Value[0]);
                else
                {
                    writer.WriteStartArray();
                    foreach (var item in argument.Value) writer.WriteStringValue(item);
                    writer.WriteEndArray();
                }

                writer.WritePropertyName("rules");
                JsonSerializer.Serialize(writer, argument.Rules, options);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
    }
}
