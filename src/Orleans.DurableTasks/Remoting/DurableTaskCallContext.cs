using System.Diagnostics.CodeAnalysis;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Remoting;

[GenerateSerializer]
public class DurableTaskCallContext
{
    private static readonly AsyncLocal<DurableTaskCallContext?> CurrentContext = new();

    public static DurableTaskCallContext? Current => CurrentContext.Value;

    private readonly Serializer _serializer;

    internal DurableTaskCallContext(Serializer serializer)
    {
        _serializer = serializer;
    }

    [Id(0)]
    public TaskId Id { get; internal set; }

    [Id(1)]
    internal Dictionary<string, byte[]> Values { get; } = new();

    public static void SetCurrentContext(DurableTaskCallContext? value)
    {
        CurrentContext.Value = value;
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

    public static void Clear() => CurrentContext.Value = null;
}
