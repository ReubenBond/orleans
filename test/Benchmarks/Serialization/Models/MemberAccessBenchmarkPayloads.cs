namespace Benchmarks.Serialization.Models;

internal static class MemberAccessBenchmarkValues
{
    public static Guid Id { get; } = new("3ddf1f8f-8597-4c90-969f-bb6f0f4a5180");
    public const long Sequence = 9_876_543_210;
    public const int Attempts = 7;
    public const bool IsEnabled = true;
    public const double Amount = 12_345.67;
    public static DateTime Timestamp { get; } = new(2026, 7, 17, 21, 5, 26, DateTimeKind.Utc);
    public const string Name = "contoso-westus-production";
    public const byte Status = 3;
}

[GenerateSerializer]
public sealed class PublicMutableMemberPayload
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public long Sequence { get; set; }
    [Id(2)] public int Attempts { get; set; }
    [Id(3)] public bool IsEnabled { get; set; }
    [Id(4)] public double Amount { get; set; }
    [Id(5)] public DateTime Timestamp { get; set; }
    [Id(6)] public string Name { get; set; }
    [Id(7)] public byte Status { get; set; }

    public static PublicMutableMemberPayload Create() => new()
    {
        Id = MemberAccessBenchmarkValues.Id,
        Sequence = MemberAccessBenchmarkValues.Sequence,
        Attempts = MemberAccessBenchmarkValues.Attempts,
        IsEnabled = MemberAccessBenchmarkValues.IsEnabled,
        Amount = MemberAccessBenchmarkValues.Amount,
        Timestamp = MemberAccessBenchmarkValues.Timestamp,
        Name = MemberAccessBenchmarkValues.Name,
        Status = MemberAccessBenchmarkValues.Status,
    };
}

[GenerateSerializer]
public sealed class PrivateFieldMemberPayload
{
    [Id(0)] private readonly Guid _id;
    [Id(1)] private readonly long _sequence;
    [Id(2)] private readonly int _attempts;
    [Id(3)] private readonly bool _isEnabled;
    [Id(4)] private readonly double _amount;
    [Id(5)] private readonly DateTime _timestamp;
    [Id(6)] private readonly string _name;
    [Id(7)] private readonly byte _status;

    public PrivateFieldMemberPayload(
        Guid id,
        long sequence,
        int attempts,
        bool isEnabled,
        double amount,
        DateTime timestamp,
        string name,
        byte status)
    {
        _id = id;
        _sequence = sequence;
        _attempts = attempts;
        _isEnabled = isEnabled;
        _amount = amount;
        _timestamp = timestamp;
        _name = name;
        _status = status;
    }

    public static PrivateFieldMemberPayload Create() => new(
        MemberAccessBenchmarkValues.Id,
        MemberAccessBenchmarkValues.Sequence,
        MemberAccessBenchmarkValues.Attempts,
        MemberAccessBenchmarkValues.IsEnabled,
        MemberAccessBenchmarkValues.Amount,
        MemberAccessBenchmarkValues.Timestamp,
        MemberAccessBenchmarkValues.Name,
        MemberAccessBenchmarkValues.Status);
}

[GenerateSerializer]
public sealed class InitOnlyMemberPayload
{
    [Id(0)] public Guid Id { get; init; }
    [Id(1)] public long Sequence { get; init; }
    [Id(2)] public int Attempts { get; init; }
    [Id(3)] public bool IsEnabled { get; init; }
    [Id(4)] public double Amount { get; init; }
    [Id(5)] public DateTime Timestamp { get; init; }
    [Id(6)] public string Name { get; init; }
    [Id(7)] public byte Status { get; init; }

    public static InitOnlyMemberPayload Create() => new()
    {
        Id = MemberAccessBenchmarkValues.Id,
        Sequence = MemberAccessBenchmarkValues.Sequence,
        Attempts = MemberAccessBenchmarkValues.Attempts,
        IsEnabled = MemberAccessBenchmarkValues.IsEnabled,
        Amount = MemberAccessBenchmarkValues.Amount,
        Timestamp = MemberAccessBenchmarkValues.Timestamp,
        Name = MemberAccessBenchmarkValues.Name,
        Status = MemberAccessBenchmarkValues.Status,
    };
}

[GenerateSerializer]
public sealed class GetOnlyMemberPayload
{
    [Id(0)] public Guid Id { get; }
    [Id(1)] public long Sequence { get; }
    [Id(2)] public int Attempts { get; }
    [Id(3)] public bool IsEnabled { get; }
    [Id(4)] public double Amount { get; }
    [Id(5)] public DateTime Timestamp { get; }
    [Id(6)] public string Name { get; }
    [Id(7)] public byte Status { get; }

    public GetOnlyMemberPayload(
        Guid id,
        long sequence,
        int attempts,
        bool isEnabled,
        double amount,
        DateTime timestamp,
        string name,
        byte status)
    {
        Id = id;
        Sequence = sequence;
        Attempts = attempts;
        IsEnabled = isEnabled;
        Amount = amount;
        Timestamp = timestamp;
        Name = name;
        Status = status;
    }

    public static GetOnlyMemberPayload Create() => new(
        MemberAccessBenchmarkValues.Id,
        MemberAccessBenchmarkValues.Sequence,
        MemberAccessBenchmarkValues.Attempts,
        MemberAccessBenchmarkValues.IsEnabled,
        MemberAccessBenchmarkValues.Amount,
        MemberAccessBenchmarkValues.Timestamp,
        MemberAccessBenchmarkValues.Name,
        MemberAccessBenchmarkValues.Status);
}
