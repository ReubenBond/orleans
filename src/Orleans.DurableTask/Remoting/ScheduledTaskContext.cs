using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization;

namespace Orleans.Vesuvius.Remoting;

[GenerateSerializer]
public class ScheduledTaskContext
{
    private static readonly AsyncLocal<ScheduledTaskContext?> _context = new();

    public static ScheduledTaskContext? Current => _context.Value;

    private readonly Serializer _serializer;
    private readonly ITransactionClient _transactionClient;

    internal ScheduledTaskContext(Serializer serializer, ITransactionClient transactionClient)
    {
        _serializer = serializer;
        _transactionClient = transactionClient;
    }

    [Id(0)]
    public ScheduledTaskId Id { get; internal set; }

    [Id(0)]
    internal Dictionary<string, byte[]> Values { get; } = new();

    public static void SetCurrentContext(ScheduledTaskContext? value)
    {
        _context.Value = value;
    }

    public bool TryGetValue<T>(string key, [NotNullWhen(true)] out T? value)
    {
        if (Values.TryGetValue(key, out var payload))
        {
            value = _serializer!.Deserialize<T>(payload)!;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryAdd(string key, object value)
    {
        var result = Values.TryAdd(key, _serializer!.SerializeToArray(value));
        return result;
    }

    public static void Clear() => _context.Value = null;

    public async ValueTask<T> GetOrAddInTransaction<T>(string key, TransactionOption transactionOptions, Func<ValueTask<T>> transactionDelegate)
    {
        return await transactionDelegate();
    }
}
