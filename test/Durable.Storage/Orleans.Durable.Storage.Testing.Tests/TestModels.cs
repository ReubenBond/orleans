using System.Collections.Immutable;
using System.Globalization;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// A register's operation in Porcupine's <c>registerModel</c> (porcupine_test.go): a put of a value, or a get.
/// </summary>
internal readonly record struct RegisterInput(bool IsGet, int Value)
{
    public static RegisterInput Get => new(IsGet: true, 0);

    public static RegisterInput Put(int value) => new(IsGet: false, value);
}

/// <summary>
/// Porcupine's register model: an integer register initialized to 0, with puts and gets.
/// </summary>
internal sealed class RegisterModel : LinearizabilityModel<int, RegisterInput, int>
{
    private RegisterModel()
    {
    }

    public static RegisterModel Instance { get; } = new();

    public override int Init() => 0;

    public override bool Step(int state, RegisterInput input, int output, out int next)
    {
        if (input.IsGet)
        {
            next = state;
            return output == state;
        }

        next = input.Value;
        return true;
    }

    public override string DescribeOperation(RegisterInput input, int output) =>
        input.IsGet ? Invariant($"get() -> '{output}'") : Invariant($"put('{input.Value}')");

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}

/// <summary>
/// A key-value store operation in Porcupine's <c>kvModel</c>: a get, put, or append on one key.
/// </summary>
internal readonly record struct KvInput(KvOperation Operation, string Key, string Value)
{
    public static KvInput Get(string key) => new(KvOperation.Get, key, string.Empty);

    public static KvInput Put(string key, string value) => new(KvOperation.Put, key, value);

    public static KvInput Append(string key, string value) => new(KvOperation.Append, key, value);
}

internal enum KvOperation
{
    Get,
    Put,
    Append,
}

/// <summary>
/// Porcupine's <c>kvModel</c>: one key's string value, with histories partitioned by key.
/// </summary>
internal sealed class KvModel : LinearizabilityModel<string, KvInput, string>
{
    private KvModel()
    {
    }

    public static KvModel Instance { get; } = new();

    public override IEqualityComparer<KvInput> PartitionComparer { get; } = new KeyComparer();

    public override string Init() => string.Empty;

    public override bool Step(string state, KvInput input, string output, out string next)
    {
        next = input.Operation switch
        {
            KvOperation.Get => state,
            KvOperation.Put => input.Value,
            KvOperation.Append => state + input.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Operation, "Undefined operation."),
        };
        return input.Operation != KvOperation.Get || string.Equals(output, state, StringComparison.Ordinal);
    }

    public override string DescribeOperation(KvInput input, string output) => Describe(input, output);

    internal static string Describe(KvInput input, string output) => input.Operation switch
    {
        KvOperation.Get => string.Create(CultureInfo.InvariantCulture, $"get('{input.Key}') -> '{output}'"),
        KvOperation.Put => string.Create(CultureInfo.InvariantCulture, $"put('{input.Key}', '{input.Value}')"),
        KvOperation.Append => string.Create(CultureInfo.InvariantCulture, $"append('{input.Key}', '{input.Value}')"),
        _ => throw new ArgumentOutOfRangeException(nameof(input), input.Operation, "Undefined operation."),
    };

    private sealed class KeyComparer : IEqualityComparer<KvInput>
    {
        public bool Equals(KvInput x, KvInput y) => string.Equals(x.Key, y.Key, StringComparison.Ordinal);

        public int GetHashCode(KvInput obj) => StringComparer.Ordinal.GetHashCode(obj.Key);
    }
}

/// <summary>
/// Porcupine's <c>kvNoPartitionModel</c>: the whole store as one map, without partitioning.
/// </summary>
internal sealed class UnpartitionedKvModel : LinearizabilityModel<ImmutableSortedDictionary<string, string>, KvInput, string>
{
    private UnpartitionedKvModel()
    {
    }

    public static UnpartitionedKvModel Instance { get; } = new();

    public override IEqualityComparer<ImmutableSortedDictionary<string, string>> StateComparer { get; } =
        new MapComparer<string>(StringComparer.Ordinal);

    public override ImmutableSortedDictionary<string, string> Init() => ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    public override bool Step(ImmutableSortedDictionary<string, string> state, KvInput input, string output, out ImmutableSortedDictionary<string, string> next)
    {
        var value = state.GetValueOrDefault(input.Key, string.Empty);
        switch (input.Operation)
        {
            case KvOperation.Get:
                next = state;
                return string.Equals(output, value, StringComparison.Ordinal);
            case KvOperation.Put:
                next = state.SetItem(input.Key, input.Value);
                return true;
            case KvOperation.Append:
                next = state.SetItem(input.Key, value + input.Value);
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Operation, "Undefined operation.");
        }
    }

    public override string DescribeOperation(KvInput input, string output) => KvModel.Describe(input, output);
}

/// <summary>
/// An etcd register operation in Porcupine's <c>etcdModel</c>: a read, a write, or a compare-and-set.
/// </summary>
internal readonly record struct EtcdInput(EtcdOperation Operation, int Argument, int Replacement)
{
    public static EtcdInput Read => new(EtcdOperation.Read, 0, 0);

    public static EtcdInput Write(int value) => new(EtcdOperation.Write, value, 0);

    public static EtcdInput CompareAndSet(int expected, int replacement) => new(EtcdOperation.CompareAndSet, expected, replacement);
}

internal enum EtcdOperation
{
    Read,
    Write,
    CompareAndSet,
}

/// <summary>
/// An etcd response: whether a compare-and-set succeeded, a read's value (null when the key does not exist), or an
/// unknown outcome after a timeout.
/// </summary>
internal readonly record struct EtcdOutput(bool Succeeded, int? Value, bool Unknown)
{
    public static EtcdOutput Done => default;

    public static EtcdOutput Timeout => new(Succeeded: false, null, Unknown: true);

    public static EtcdOutput Read(int? value) => new(Succeeded: false, value, Unknown: false);

    public static EtcdOutput CompareAndSet(bool succeeded) => new(succeeded, null, Unknown: false);
}

/// <summary>
/// Porcupine's <c>etcdModel</c>: one register which starts absent, with reads, writes, and compare-and-set; an unknown
/// outcome is allowed in any state and a compare-and-set with one takes effect where it applies.
/// </summary>
internal sealed class EtcdModel : LinearizabilityModel<int?, EtcdInput, EtcdOutput>
{
    private EtcdModel()
    {
    }

    public static EtcdModel Instance { get; } = new();

    public override int? Init() => null;

    public override bool Step(int? state, EtcdInput input, EtcdOutput output, out int? next)
    {
        switch (input.Operation)
        {
            case EtcdOperation.Read:
                next = state;
                return output.Unknown || output.Value == state;
            case EtcdOperation.Write:
                next = input.Argument;
                return true;
            case EtcdOperation.CompareAndSet:
                var applies = state == input.Argument;
                next = applies ? input.Replacement : state;
                return output.Unknown || output.Succeeded == applies;
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Operation, "Undefined operation.");
        }
    }

    public override string DescribeOperation(EtcdInput input, EtcdOutput output) => input.Operation switch
    {
        EtcdOperation.Read => Invariant($"read() -> {(output.Unknown ? "unknown" : output.Value?.ToString(CultureInfo.InvariantCulture) ?? "null")}"),
        EtcdOperation.Write => Invariant($"write({input.Argument})"),
        EtcdOperation.CompareAndSet => Invariant($"cas({input.Argument}, {input.Replacement}) -> {Describe(output)}"),
        _ => throw new ArgumentOutOfRangeException(nameof(input), input.Operation, "Undefined operation."),
    };

    private static string Describe(EtcdOutput output)
    {
        if (output.Unknown)
        {
            return "unknown";
        }

        return output.Succeeded ? "ok" : "fail";
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}

/// <summary>
/// Lifts a per-key model to one map over every key without partitioning, so a check of the lifted model searches the
/// whole history at once. Agreement with the partitioned check is P-compositionality.
/// </summary>
internal sealed class UnpartitionedModel<TState, TInput, TOutput>(
    LinearizabilityModel<TState, TInput, TOutput> model,
    Func<TInput, string> key)
    : LinearizabilityModel<ImmutableSortedDictionary<string, TState>, TInput, TOutput>
    where TInput : notnull
{
    public override IEqualityComparer<ImmutableSortedDictionary<string, TState>> StateComparer { get; } = new MapComparer<TState>(model.StateComparer);

    public override ImmutableSortedDictionary<string, TState> Init() => ImmutableSortedDictionary.Create<string, TState>(StringComparer.Ordinal);

    public override bool Step(ImmutableSortedDictionary<string, TState> state, TInput input, TOutput output, out ImmutableSortedDictionary<string, TState> next)
    {
        var name = key(input);
        if (!model.Step(state.TryGetValue(name, out var value) ? value : model.Init(), input, output, out var stepped))
        {
            next = state;
            return false;
        }

        next = state.SetItem(name, stepped);
        return true;
    }

    public override string DescribeOperation(TInput input, TOutput output) => model.DescribeOperation(input, output);
}

/// <summary>
/// Equates maps with equal keys and values, hashing order-independently.
/// </summary>
internal sealed class MapComparer<TValue>(IEqualityComparer<TValue> values) : IEqualityComparer<ImmutableSortedDictionary<string, TValue>>
{
    public bool Equals(ImmutableSortedDictionary<string, TValue>? x, ImmutableSortedDictionary<string, TValue>? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null || x.Count != y.Count)
        {
            return false;
        }

        foreach (var (key, value) in x)
        {
            if (!y.TryGetValue(key, out var other) || !values.Equals(value, other))
            {
                return false;
            }
        }

        return true;
    }

    public int GetHashCode(ImmutableSortedDictionary<string, TValue> obj)
    {
        var hash = 0;
        foreach (var (key, value) in obj)
        {
            hash ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(key), value is null ? 0 : values.GetHashCode(value));
        }

        return hash;
    }
}
