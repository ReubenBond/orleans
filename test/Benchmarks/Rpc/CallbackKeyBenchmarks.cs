using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Orleans.Runtime;

namespace Benchmarks.Rpc;

[Config(typeof(RpcBenchmarkConfig))]
[BenchmarkCategory("Rpc", "Callbacks")]
public class CallbackKeyBenchmarks
{
    private const int OperationCount = 256;
    private readonly ConcurrentDictionary<(GrainId, CorrelationId), object> _compoundCallbacks = new();
    private readonly ConcurrentDictionary<CorrelationId, object> _correlationCallbacks = new();
    private readonly CallbackRegistry<RegistryValue> _callbackRegistry = new(static value => value.Key);
    private readonly (GrainId, CorrelationId)[] _compoundKeys = new (GrainId, CorrelationId)[OperationCount];
    private readonly CorrelationId[] _correlationKeys = new CorrelationId[OperationCount];
    private readonly RegistryValue[] _registryValues = new RegistryValue[OperationCount];
    private readonly object _value = new();

    [GlobalSetup]
    public void Setup()
    {
        var grainId = GrainId.Create("benchmark-client", "client");
        for (var i = 0; i < OperationCount; i++)
        {
            var correlationId = new CorrelationId(i);
            _compoundKeys[i] = (grainId, correlationId);
            _correlationKeys[i] = correlationId;
            _registryValues[i] = new(correlationId);
        }
    }

    [Benchmark(Baseline = true)]
    public int CompoundKey()
    {
        var removed = 0;
        foreach (var key in _compoundKeys)
        {
            _compoundCallbacks.TryAdd(key, _value);
            removed += _compoundCallbacks.TryRemove(key, out _) ? 1 : 0;
        }

        return removed;
    }

    [Benchmark]
    public int CorrelationIdKey()
    {
        var removed = 0;
        foreach (var key in _correlationKeys)
        {
            _correlationCallbacks.TryAdd(key, _value);
            removed += _correlationCallbacks.TryRemove(key, out _) ? 1 : 0;
        }

        return removed;
    }

    [Benchmark]
    public int CallbackRegistry()
    {
        var removed = 0;
        foreach (var value in _registryValues)
        {
            _callbackRegistry.TryAdd(value.Key, value);
            removed += _callbackRegistry.TryRemove(value.Key, out _) ? 1 : 0;
        }

        return removed;
    }

    private sealed record RegistryValue(CorrelationId Key);
}
