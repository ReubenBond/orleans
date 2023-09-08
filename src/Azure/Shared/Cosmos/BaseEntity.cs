using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.Cosmos;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.Cosmos;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.Cosmos;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.Cosmos;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.Cosmos;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

internal abstract class BaseEntity
{
    internal const string ID_FIELD = "id";
    internal const string ETAG_FIELD = "_etag";    

    [JsonProperty(ID_FIELD)]
    [JsonPropertyName(ID_FIELD)]
    public string Id { get; set; } = default!;

    [JsonProperty(ETAG_FIELD)]
    [JsonPropertyName(ETAG_FIELD)]
    public string ETag { get; set; } = default!;
}

internal sealed class UnixDateTimeConverter : DateTimeConverterBase
{
    private static readonly DateTime UnixStartTime = new(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateTime)
        {
            long value2 = (long)((DateTime)value - UnixStartTime).TotalSeconds;
            writer.WriteValue(value2);
            return;
        }

        throw new ArgumentException($"Invalid time value {value}", "value");
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType != JsonToken.Integer)
        {
            throw new Exception($"Expected an integer, encountered a value of type {reader.TokenType} at path {reader.Path}.");
        }

        double num;
        try
        {
            num = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
        }
        catch
        {
            throw new Exception($"Invalid value. Expected a double, encountered {reader.Value}.");
        }

        return UnixStartTime.AddSeconds(num);
    }
}