using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orleans.Testing.Simulation.Runtime.Replay;

internal sealed class StrictJsonStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> DeclaredValues = CreateDeclaredValues();

    public override TEnum Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            reader.GetString() is not { } name ||
            !DeclaredValues.TryGetValue(name, out TEnum value))
        {
            throw new JsonException($"Expected one declared {typeof(TEnum).Name} name.");
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        TEnum value,
        JsonSerializerOptions options)
    {
        string? name = Enum.GetName(value);
        if (name is null)
        {
            throw new JsonException($"{typeof(TEnum).Name} value '{value}' is not defined.");
        }

        writer.WriteStringValue(name);
    }

    private static Dictionary<string, TEnum> CreateDeclaredValues()
    {
        var result = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (string name in Enum.GetNames<TEnum>())
        {
            result.Add(name, Enum.Parse<TEnum>(name));
        }

        return result;
    }
}
