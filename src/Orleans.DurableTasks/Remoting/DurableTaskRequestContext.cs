using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Remoting;

[GenerateSerializer]
public class DurableTaskRequestContext
{
    private static readonly AsyncLocal<DurableTaskRequestContext?> CurrentContext = new();

    public static DurableTaskRequestContext? Current => CurrentContext.Value;

    [NonSerialized]
    private readonly Serializer _serializer;

    internal DurableTaskRequestContext(Serializer serializer)
    {
        _serializer = serializer;
    }

    [Id(0)]
    public TaskId Id { get; internal set; }

    [Id(1)]
    public GrainId CallerId { get; internal set; }

    [Id(2)]
    public SchedulingOptions? SchedulingOptions { get; internal set; }

    [Id(3)]
    internal Dictionary<string, byte[]> Values { get; } = new();

    public static void SetCurrentContext(DurableTaskRequestContext? value)
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
