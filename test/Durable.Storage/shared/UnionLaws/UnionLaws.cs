using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using CsCheck;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// One case of a hand-written union under the union laws.
/// </summary>
/// <typeparam name="TUnion">The union.</typeparam>
/// <param name="Name">The case's name.</param>
/// <param name="Values">
/// Generates factories of the case's values; the laws call a factory inside the leak scope which owns its value.
/// </param>
/// <param name="Wrap">Returns the union holding a value.</param>
/// <param name="Release">Releases a value which owns pages, or is <see langword="null"/> for a case which owns none.</param>
internal sealed record UnionCase<TUnion>(string Name, Gen<Func<object>> Values, Func<object, TUnion> Wrap, Action<object>? Release = null)
    where TUnion : struct;

/// <summary>
/// Describes one hand-written union to the union laws (conventions.md §15): its cases, and a type-pattern switch over
/// them.
/// </summary>
/// <typeparam name="TUnion">The union.</typeparam>
internal interface IUnionLawsAdapter<TUnion>
    where TUnion : struct
{
    /// <summary>
    /// Gets the union's cases, in the order <see cref="Switch"/> numbers them.
    /// </summary>
    IReadOnlyList<UnionCase<TUnion>> Cases { get; }

    /// <summary>
    /// Gets pairs of values which hold different cases with equal fields, which a union declaring equality holds unequal.
    /// </summary>
    IEnumerable<(Func<TUnion> Left, Func<TUnion> Right)> EqualFieldsInDifferentCases => [];

    /// <summary>
    /// Returns the index in <see cref="Cases"/> of the case <paramref name="value"/> holds, by a type-pattern switch
    /// which names every case and ends with a throwing <see langword="null"/> arm.
    /// </summary>
    int Switch(TUnion value);
}

/// <summary>
/// Holds a hand-written union to the laws of conventions.md §15: for a held value, exactly one <c>TryGetValue</c>
/// answers true, with that value; <c>HasValue</c> is true exactly when <c>Value</c> is non-null; repeated access agrees;
/// a default value holds nothing; where the union declares equality, two values are equal exactly when they hold the same
/// case with equal values, <c>GetHashCode</c> agrees, and equal fields in different cases differ; and a type-pattern
/// switch in a warmed loop allocates no bytes. Each generated value lives in its own page leak scope.
/// </summary>
internal static class UnionLaws
{
    /// <summary>
    /// The iterations of the warmed switch loop whose allocations the laws measure.
    /// </summary>
    private const int SwitchIterations = 10_000;

    /// <summary>
    /// Checks every law over <paramref name="adapter"/>'s cases.
    /// </summary>
    internal static void Check<TUnion>(IUnionLawsAdapter<TUnion> adapter, ArcBufferLeakCheck leaks)
        where TUnion : struct
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(leaks);
        var reads = TryGetValues<TUnion>();
        Assert.Equal(adapter.Cases.Count, reads.Count);
        CheckDefault<TUnion>(reads);
        for (var index = 0; index < adapter.Cases.Count; index++)
        {
            var caseIndex = index;
            adapter.Cases[index].Values.Sample(create => leaks.InScope(() => WithValue(adapter, caseIndex, create, value => CheckHeld(adapter, reads, caseIndex, value))));
        }

        if (DeclaresEquality<TUnion>())
        {
            CheckEquality(adapter, leaks);
        }

        leaks.InScope(() => CheckSwitchAllocatesNothing(adapter));
    }

    private static void CheckDefault<TUnion>(List<MethodInfo> reads)
        where TUnion : struct
    {
        var empty = default(TUnion);
        Assert.False(HasValue(empty), $"default({typeof(TUnion).Name}) holds a value.");
        Assert.Null(ValueOf(empty));
        Assert.All(reads, read => Assert.False(Read(read, empty, out _), $"default({typeof(TUnion).Name}) answered {read}."));
    }

    private static void CheckHeld<TUnion>(IUnionLawsAdapter<TUnion> adapter, List<MethodInfo> reads, int caseIndex, object value)
        where TUnion : struct
    {
        var unionCase = adapter.Cases[caseIndex];
        var union = unionCase.Wrap(value);
        for (var access = 0; access < 2; access++)
        {
            Assert.True(HasValue(union), $"{typeof(TUnion).Name}.{unionCase.Name}: HasValue is false for a held value.");
            Assert.Equal(value, ValueOf(union));
            var answered = new List<object?>();
            foreach (var read in reads)
            {
                if (Read(read, union, out var held))
                {
                    answered.Add(held);
                }
            }

            Assert.True(answered.Count == 1, $"{typeof(TUnion).Name}.{unionCase.Name}: {answered.Count} TryGetValue overloads answered true.");
            Assert.Equal(value, answered[0]);
            Assert.Equal(caseIndex, adapter.Switch(union));
        }
    }

    /// <summary>
    /// Checks that two values, each of any case, are equal exactly when they hold the same case with equal values, that
    /// equal values hash alike, and that the adapter's pairs of equal fields in different cases differ.
    /// </summary>
    private static void CheckEquality<TUnion>(IUnionLawsAdapter<TUnion> adapter, ArcBufferLeakCheck leaks)
        where TUnion : struct
    {
        var anyValue = Gen.Int[0, adapter.Cases.Count - 1].SelectMany(index => adapter.Cases[index].Values.Select(create => (Index: index, Create: create)));
        Gen.Select(anyValue, anyValue).Sample((left, right) => leaks.InScope(() =>
            WithValue(adapter, left.Index, left.Create, leftValue =>
                WithValue(adapter, right.Index, right.Create, rightValue =>
                {
                    var leftUnion = adapter.Cases[left.Index].Wrap(leftValue);
                    var rightUnion = adapter.Cases[right.Index].Wrap(rightValue);
                    var expected = left.Index == right.Index && Equals(leftValue, rightValue);
                    Assert.True(
                        expected == leftUnion.Equals(rightUnion),
                        expected
                            ? $"{typeof(TUnion).Name}: {leftUnion} and {rightUnion} hold equal values of one case but compare unequal."
                            : $"{typeof(TUnion).Name}: {leftUnion} and {rightUnion} compare equal but hold different cases or values.");
                    Assert.True(leftUnion.Equals(adapter.Cases[left.Index].Wrap(leftValue)), $"{typeof(TUnion).Name}: a value is not equal to itself.");
                    Assert.False(leftUnion.Equals(default(TUnion)), $"{typeof(TUnion).Name}: a held value equals the default.");
                    if (expected)
                    {
                        Assert.Equal(leftUnion.GetHashCode(), rightUnion.GetHashCode());
                    }
                }))));
        foreach (var (left, right) in adapter.EqualFieldsInDifferentCases)
        {
            leaks.InScope(() =>
            {
                var leftUnion = left();
                var rightUnion = right();
                Assert.False(leftUnion.Equals(rightUnion), $"{typeof(TUnion).Name}: {leftUnion} and {rightUnion} hold different cases but compare equal.");
            });
        }
    }

    private static void CheckSwitchAllocatesNothing<TUnion>(IUnionLawsAdapter<TUnion> adapter)
        where TUnion : struct
    {
        var held = new List<(object Value, UnionCase<TUnion> Case)>();
        try
        {
            foreach (var unionCase in adapter.Cases)
            {
                held.Add((unionCase.Values.Single()(), unionCase));
            }

            var unions = held.Select(static entry => entry.Case.Wrap(entry.Value)).ToArray();
            var total = Run(adapter, unions);
            var before = GC.GetAllocatedBytesForCurrentThread();
            total += Run(adapter, unions);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated == 0, $"{typeof(TUnion).Name}: a type-pattern switch allocated {allocated.ToString(CultureInfo.InvariantCulture)} bytes over {SwitchIterations.ToString(CultureInfo.InvariantCulture)} iterations ({total.ToString(CultureInfo.InvariantCulture)}).");
        }
        finally
        {
            foreach (var (value, unionCase) in held)
            {
                unionCase.Release?.Invoke(value);
            }
        }

        static long Run(IUnionLawsAdapter<TUnion> adapter, TUnion[] unions)
        {
            var total = 0L;
            for (var iteration = 0; iteration < SwitchIterations; iteration++)
            {
                total += adapter.Switch(unions[iteration % unions.Length]);
            }

            return total;
        }
    }

    /// <summary>
    /// Creates a value of a case, runs <paramref name="body"/> over it, and releases it.
    /// </summary>
    private static void WithValue<TUnion>(IUnionLawsAdapter<TUnion> adapter, int caseIndex, Func<object> create, Action<object> body)
        where TUnion : struct
    {
        var value = create();
        try
        {
            body(value);
        }
        finally
        {
            adapter.Cases[caseIndex].Release?.Invoke(value);
        }
    }

    private static List<MethodInfo> TryGetValues<TUnion>()
    {
        var reads = typeof(TUnion).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(static method => method.Name == "TryGetValue" && method.ReturnType == typeof(bool)
                && method.GetParameters() is [{ IsOut: true }]);
        return [.. reads];
    }

    private static bool DeclaresEquality<TUnion>() => typeof(IEquatable<TUnion>).IsAssignableFrom(typeof(TUnion));

    private static bool HasValue<TUnion>(TUnion union)
        where TUnion : struct => (bool)typeof(TUnion).GetProperty("HasValue")!.GetValue(union)!;

    private static object? ValueOf<TUnion>(TUnion union)
        where TUnion : struct => ((IUnion)union).Value;

    private static bool Read<TUnion>(MethodInfo read, TUnion union, out object? value)
        where TUnion : struct
    {
        object?[] arguments = [null];
        var answered = (bool)read.Invoke(union, arguments)!;
        value = arguments[0];
        return answered;
    }
}
