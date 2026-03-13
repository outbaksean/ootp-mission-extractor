using System.Text.Json;
using System.Text.Json.Serialization;

namespace MissionExtractor.dto;

[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MissionType
{
    Count,
    Points,
    Missions
}

/// <summary>
/// Serializes enum values as lowercase strings and deserializes case-insensitively,
/// matching the missions.json format used by the Vue app.
/// </summary>
public class CamelCaseStringEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(EnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class EnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var str = reader.GetString() ?? throw new JsonException($"Expected a string for {typeof(T).Name}.");
            if (Enum.TryParse<T>(str, ignoreCase: true, out var result))
                return result;
            throw new JsonException($"Unable to convert \"{str}\" to {typeof(T).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
