using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Testing.Simulation.Conformance.Tests;

/// <summary>
/// End-to-end conformance for the controlled <see cref="System.Threading.Interlocked"/> surface. The
/// rule set redirects every <c>Increment</c>/<c>Decrement</c>/<c>Add</c>/<c>And</c>/<c>Or</c>/
/// <c>Exchange</c>/<c>CompareExchange</c>/<c>Read</c> call site to a controlled shim with the identical
/// <c>ref</c>-first signature. Because Clockwork runs on a single cooperative logical thread each
/// read-modify-write is indivisible under simulation; outside simulation, rewritten calls fail before
/// mutating ref state.
/// </summary>
public sealed class InterlockedConformanceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Source = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Conf { public static class InterlockedProbe {
            // A pair of controlled threads each increment a shared counter many times; because the logical
            // thread is cooperative every increment is atomic, so the total is exact with no lost updates.
            public static Task<int> ConcurrentIncrementsAreAtomic()
            {
                int counter = 0;
                var a = new Thread(() => { for (int i = 0; i < 1000; i++) Interlocked.Increment(ref counter); });
                var b = new Thread(() => { for (int i = 0; i < 1000; i++) Interlocked.Increment(ref counter); });
                a.Start(); b.Start();
                a.Join(); b.Join();
                return Task.FromResult(counter);
            }

            // Increment/Decrement return the updated value.
            public static Task<bool> IncrementDecrementReturns()
            {
                int i = 0;
                bool ok = Interlocked.Increment(ref i) == 1 && i == 1
                       && Interlocked.Decrement(ref i) == 0 && i == 0;
                long l = 0;
                ok &= Interlocked.Increment(ref l) == 1L && Interlocked.Decrement(ref l) == 0L;
                return Task.FromResult(ok);
            }

            public static int IncrementRef(ref int value) => Interlocked.Increment(ref value);

            // Add returns the new sum and stores it.
            public static Task<bool> AddReturnsSum()
            {
                int i = 10;
                bool ok = Interlocked.Add(ref i, 7) == 17 && i == 17;
                long l = 10;
                ok &= Interlocked.Add(ref l, 7) == 17L;
                return Task.FromResult(ok);
            }

            // And/Or apply the bitwise operation and return the original value.
            public static Task<bool> AndOrReturnOriginal()
            {
                int and = 0b1111;
                bool ok = Interlocked.And(ref and, 0b1010) == 0b1111 && and == 0b1010;
                int or = 0b0100;
                ok &= Interlocked.Or(ref or, 0b0001) == 0b0100 && or == 0b0101;
                return Task.FromResult(ok);
            }

            // Exchange returns the original value and stores the new one, across value and reference kinds.
            public static Task<bool> ExchangeReturnsOriginal()
            {
                int i = 5;
                bool ok = Interlocked.Exchange(ref i, 9) == 5 && i == 9;
                double d = 1.5;
                ok &= Interlocked.Exchange(ref d, 2.5) == 1.5 && d == 2.5;
                object o = "a";
                ok &= (string)Interlocked.Exchange(ref o, "b") == "a" && (string)o == "b";
                return Task.FromResult(ok);
            }

            // CompareExchange swaps only when the comparand matches.
            public static Task<bool> CompareExchangeConditional()
            {
                int i = 7;
                bool ok = Interlocked.CompareExchange(ref i, 42, 7) == 7 && i == 42;
                ok &= Interlocked.CompareExchange(ref i, 99, 7) == 42 && i == 42;
                string s = "x";
                ok &= Interlocked.CompareExchange(ref s, "y", "x") == "x" && s == "y";
                return Task.FromResult(ok);
            }

            // The generic reference overloads operate by identity.
            public static Task<bool> GenericReferenceOverloads()
            {
                var first = new object();
                var second = new object();
                object slot = first;
                bool ok = ReferenceEquals(Interlocked.Exchange(ref slot, second), first) && ReferenceEquals(slot, second);
                ok &= ReferenceEquals(Interlocked.CompareExchange(ref slot, first, second), second) && ReferenceEquals(slot, first);
                return Task.FromResult(ok);
            }

            // The generic overloads operate on an enum as on its underlying integer.
            public static Task<bool> GenericValueOverloads()
            {
                Bits bits = Bits.A | Bits.B;
                bool ok = Interlocked.And(ref bits, Bits.B) == (Bits.A | Bits.B) && bits == Bits.B;
                ok &= Interlocked.Or(ref bits, Bits.C) == Bits.B && bits == (Bits.B | Bits.C);
                ok &= Interlocked.Exchange(ref bits, Bits.A) == (Bits.B | Bits.C) && bits == Bits.A;
                ok &= Interlocked.CompareExchange(ref bits, Bits.C, Bits.A) == Bits.A && bits == Bits.C;
                return Task.FromResult(ok);
            }

            // Read returns the current 64-bit value.
            public static Task<bool> ReadReturnsCurrent()
            {
                long l = 123;
                bool ok = Interlocked.Read(ref l) == 123L;
                ulong u = 456;
                ok &= Interlocked.Read(ref u) == 456ul;
                return Task.FromResult(ok);
            }
        }

        [Flags]
        public enum Bits { A = 1, B = 2, C = 4 }
        }
        """;

    private readonly RewriteFixture _fixture = new();
    private readonly Lazy<StagedProbe> _probe;

    public InterlockedConformanceTests() =>
        _probe = new Lazy<StagedProbe>(() =>
            _fixture.StageControlledTasks("Conf.Interlocked", "Conf.InterlockedProbe", Source, optimize: true));

    [Fact]
    public void ConcurrentIncrementsAreAtomic()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<int>)host.Invoke(Method("ConcurrentIncrementsAreAtomic"))!;
        Assert.Equal(2000, Result<int>(task));
    }

    [Fact]
    public void IncrementDecrementReturns()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("IncrementDecrementReturns"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void AddReturnsSum()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("AddReturnsSum"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void AndOrReturnOriginal()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("AndOrReturnOriginal"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void ExchangeReturnsOriginal()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ExchangeReturnsOriginal"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void CompareExchangeConditional()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("CompareExchangeConditional"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void GenericReferenceOverloads()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("GenericReferenceOverloads"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public void GenericValueOverloadsAreControlled()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("GenericValueOverloads"))!;
        Assert.True(Result<bool>(task));
        string[] members = ["and", "or", "exchange", "compareexchange"];
        foreach (string member in members)
        {
            Assert.Contains(
                _probe.Value.Result.Manifest.Transformations,
                transformation => transformation.RuleId == $"clockwork.interlocked.{member}.generic");
        }
    }

    [Fact]
    public void ReadReturnsCurrent()
    {
        using var host = new SimulationHost(Start);
        var task = (Task<bool>)host.Invoke(Method("ReadReturnsCurrent"))!;
        Assert.True(Result<bool>(task));
    }

    [Fact]
    public async Task OnlyRewrittenInterlockedRequiresActiveSimulationWithoutMutatingRefState()
    {
        UninstrumentedProbe uninstrumented = _fixture.CompileUninstrumented(
            "Conf.UninstrumentedInterlocked",
            "Conf.InterlockedProbe",
            Source);
        var task = (Task<bool>)uninstrumented.Method("IncrementDecrementReturns").Invoke(null, null)!;
        Assert.True(await task);

        object?[] args = [7];
        SimulationNotActiveExceptionAssert.Throws(Method("IncrementRef"), args);
        Assert.Equal(7, args[0]);
    }

    private MethodInfo Method(string name) => _probe.Value.Method(name);

    private static T Result<T>(object task)
    {
        var typed = (Task)task;
        Assert.True(typed.IsCompleted);
        PropertyInfo result = typed.GetType().GetProperty("Result")!;
        return (T)result.GetValue(typed)!;
    }

    public void Dispose() => _fixture.Dispose();
}
