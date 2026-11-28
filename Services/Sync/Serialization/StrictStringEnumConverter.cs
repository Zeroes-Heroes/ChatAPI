using System.Text.Json;
using System.Text.Json.Serialization;

namespace Services.Sync.Serialization;

/// <summary>Wire enums accept exactly one declared name, without normalization.</summary>
internal sealed class StrictStringEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(EnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class EnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        private static readonly Dictionary<string, TEnum> Values = Enum.GetNames<TEnum>()
            .ToDictionary(name => name, name => Enum.Parse<TEnum>(name), StringComparer.Ordinal);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var value))
                return value;
            throw new JsonException($"Expected an exact {typeof(TEnum).Name} name.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            var name = Enum.GetName(value) ?? throw new JsonException($"Undefined {typeof(TEnum).Name} value.");
            writer.WriteStringValue(name);
        }
    }
}
