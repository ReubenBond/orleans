using System.Diagnostics;
using System.Runtime.CompilerServices;
using CsCheck;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Shows that the union laws (conventions.md §15) catch a hand-written union which breaks one: each broken union here
/// fails <see cref="UnionLaws.Check{TUnion}"/> for its law, and a union keeping every law, equality among them, passes.
/// </summary>
public sealed class UnionLawsSelfTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public enum Fault
    {
        None,
        TwoCasesAnswer,
        HasValueIsFalse,
        EqualityIgnoresTheCase,
        SwitchAllocates,
    }

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void AUnionKeepingEveryLawPasses() => UnionLaws.Check(new PairAdapter(Fault.None), _leaks);

    [Theory]
    [InlineData(Fault.TwoCasesAnswer, "TryGetValue overloads answered true")]
    [InlineData(Fault.HasValueIsFalse, "HasValue is false for a held value")]
    [InlineData(Fault.EqualityIgnoresTheCase, "compare equal")]
    [InlineData(Fault.SwitchAllocates, "a type-pattern switch allocated")]
    public void AUnionBreakingOneLawFails(Fault fault, string law)
    {
        var failure = Record.Exception(() => UnionLaws.Check(new PairAdapter(fault), _leaks));
        Assert.NotNull(failure);
        Assert.Contains(law, failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// One case of <see cref="Pair"/>; the two cases have equal fields.
    /// </summary>
    internal readonly record struct Left(int Value);

    /// <summary>
    /// The other case of <see cref="Pair"/>.
    /// </summary>
    internal readonly record struct Right(int Value);

    /// <summary>
    /// The case <see cref="Fault.SwitchAllocates"/> reads through a heap object.
    /// </summary>
    internal sealed record Boxed(int Value);

    /// <summary>
    /// A hand-written union of <see cref="Left"/>, <see cref="Right"/>, and <see cref="Boxed"/>, which breaks the law its
    /// <see cref="Fault"/> names.
    /// </summary>
    [Union]
    internal readonly struct Pair : IUnion, IEquatable<Pair>
    {
        private readonly int _value;
        private readonly Boxed? _boxed;
        private readonly Kind _kind;
        private readonly Fault _fault;

        public Pair(Left value)
            : this(value.Value, null, Kind.Left, default)
        {
        }

        public Pair(Right value)
            : this(value.Value, null, Kind.Right, default)
        {
        }

        public Pair(Boxed value)
            : this(value?.Value ?? throw new ArgumentNullException(nameof(value)), value, Kind.Boxed, default)
        {
        }

        private Pair(int value, Boxed? boxed, Kind kind, Fault fault)
        {
            _value = value;
            _boxed = boxed;
            _kind = kind;
            _fault = fault;
        }

        private enum Kind : byte
        {
            None,
            Left,
            Right,
            Boxed,
        }

        public object? Value => _kind switch
        {
            Kind.Left => new Left(_value),
            Kind.Right => new Right(_value),
            Kind.Boxed => _boxed,
            Kind.None => null,
            _ => throw new UnreachableException(),
        };

        public bool HasValue => _kind != Kind.None && _fault != Fault.HasValueIsFalse;

        public static Pair With(Pair pair, Fault fault) => new(pair._value, pair._boxed, pair._kind, fault);

        public bool TryGetValue(out Left value)
        {
            value = new Left(_value);
            return _kind == Kind.Left || _kind == Kind.Right && _fault == Fault.TwoCasesAnswer;
        }

        public bool TryGetValue(out Right value)
        {
            value = new Right(_value);
            return _kind == Kind.Right;
        }

        public bool TryGetValue(out Boxed? value)
        {
            value = _kind != Kind.Boxed ? null : _fault == Fault.SwitchAllocates ? new Boxed(_value) : _boxed;
            return _kind == Kind.Boxed;
        }

        public bool Equals(Pair other) =>
            (_kind == other._kind || _fault == Fault.EqualityIgnoresTheCase && _kind != Kind.None && other._kind != Kind.None) && _value == other._value;

        public override bool Equals(object? obj) => obj is Pair other && Equals(other);

        public override int GetHashCode() => _fault == Fault.EqualityIgnoresTheCase ? _value : HashCode.Combine(_kind, _value);

        public override string ToString() => Value?.ToString() ?? string.Empty;
    }

    private sealed class PairAdapter(Fault fault) : IUnionLawsAdapter<Pair>
    {
        public IReadOnlyList<UnionCase<Pair>> Cases { get; } =
        [
            new(nameof(Left), Gen.Int.Select(static value => (Func<object>)(() => new Left(value))), value => Pair.With(new Pair((Left)value), fault)),
            new(nameof(Right), Gen.Int.Select(static value => (Func<object>)(() => new Right(value))), value => Pair.With(new Pair((Right)value), fault)),
            new(nameof(Boxed), Gen.Int.Select(static value => (Func<object>)(() => new Boxed(value))), value => Pair.With(new Pair((Boxed)value), fault)),
        ];

        public IEnumerable<(Func<Pair> Left, Func<Pair> Right)> EqualFieldsInDifferentCases =>
        [
            (() => Pair.With(new Pair(new Left(7)), fault), () => Pair.With(new Pair(new Right(7)), fault)),
        ];

        public int Switch(Pair value) => value switch
        {
            Left => 0,
            Right => 1,
            Boxed => 2,
            null => throw new UnreachableException(),
        };
    }
}
