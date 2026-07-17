namespace Benchmarks.Serialization.Models;

[GenerateSerializer]
public sealed class SerializerBenchmarkPayload
{
    [Id(0)]
    public Guid RequestId { get; set; }

    [Id(1)]
    public string TenantId { get; set; }

    [Id(2)]
    public long Timestamp { get; set; }

    [Id(3)]
    public Dictionary<string, string> Headers { get; set; }

    [Id(4)]
    public SerializerBenchmarkItem[] Items { get; set; }

    [Id(5)]
    public byte[] Body { get; set; }
}

[GenerateSerializer]
public sealed class SerializerBenchmarkItem
{
    [Id(0)]
    public long ProductId { get; set; }

    [Id(1)]
    public string Sku { get; set; }

    [Id(2)]
    public int Quantity { get; set; }

    [Id(3)]
    public long UnitPrice { get; set; }

    [Id(4)]
    public bool IsBackordered { get; set; }
}
