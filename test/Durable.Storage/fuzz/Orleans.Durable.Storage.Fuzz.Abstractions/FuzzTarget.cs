using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Orleans.Durable.Storage.Fuzz;

/// <summary>
/// The outcomes a decoder reached for one input: the input as given and, for a format with checksums, the input with
/// its checksums recomputed.
/// </summary>
[Flags]
public enum FuzzOutcome
{
    /// <summary>The input lies outside the decoder's domain, so the decoder made no ruling.</summary>
    None = 0,

    /// <summary>The decoder rejected the bytes, and the oracle found the rule they break.</summary>
    Rejected = 1,

    /// <summary>The decoder accepted the bytes, and the oracle confirmed the decoded value.</summary>
    Accepted = 2,
}

/// <summary>
/// One decoder of durable or wire data under fuzzing (verification.md §6.1): its seeds and its oracle. The decoder
/// either rejects an input which breaks a rule its format documents, or accepts it with the value the format's rules,
/// applied to the bytes independently of the decoder, give. It never throws anything else, never hangs, and
/// allocates in proportion to the objects it decodes, never in proportion to a length field.
/// </summary>
public abstract class FuzzTarget
{
    private readonly Lock _warming = new();
    private bool _warm;
    private bool _measuring;

    /// <summary>
    /// Gets or sets a value indicating whether the process runs one decoding at a time and nothing else, as the harness
    /// does in a campaign or a replay, so that its allocations are the decoding's. Bounds measured process-wide are checked only then; bounds
    /// measured on the decoding's thread are always checked.
    /// </summary>
    public static bool IsolatedProcess { get; set; }

    /// <summary>
    /// Gets the target's name: its command-line argument and the name of its corpus directory.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Gets the longest input a campaign generates, libFuzzer's <c>-max_len</c>, unless a seed is longer.
    /// </summary>
    public abstract int MaxLength { get; }

    /// <summary>
    /// Returns the seed inputs of the target's format, accepted and rejected: its golden vectors where it has them.
    /// </summary>
    public abstract IEnumerable<byte[]> Seeds(GoldenVectors vectors);

    /// <summary>
    /// Decodes an input, and the input with its checksums recomputed, and applies the oracle to each.
    /// </summary>
    /// <returns>The outcomes the decodings reached.</returns>
    /// <exception cref="FuzzFindingException">The decoder broke its oracle.</exception>
    public FuzzOutcome Check(ReadOnlySpan<byte> input)
    {
        Warm();
        return CheckWithSeal(input);
    }

    /// <summary>
    /// Decodes one input and checks the result against the oracle.
    /// </summary>
    /// <exception cref="FuzzFindingException">The decoder broke its oracle.</exception>
    protected abstract FuzzOutcome Decode(ReadOnlySpan<byte> input);

    /// <summary>
    /// Returns the input with its checksums recomputed, so the fuzzer reaches the rules behind them, or
    /// <see langword="null"/> when the format has none.
    /// </summary>
    protected virtual byte[]? Seal(ReadOnlySpan<byte> input) => null;

    /// <summary>
    /// Throws a finding unless <paramref name="condition"/> holds.
    /// </summary>
    protected void Require([DoesNotReturnIf(false)] bool condition, string violation)
    {
        if (!condition)
        {
            throw new FuzzFindingException($"{Name}: {violation}");
        }
    }

    /// <summary>
    /// Starts measuring one decoding of <paramref name="objectLength"/> bytes on the current thread.
    /// </summary>
    protected AllocationBound MeasureAllocations(long objectLength) => AllocationBound.Start(Name, objectLength, _measuring, processWide: false);

    /// <summary>
    /// Starts measuring one decoding of <paramref name="objectLength"/> bytes in the whole process, for a decoder which
    /// continues on other threads, when the process is isolated (<see cref="IsolatedProcess"/>).
    /// </summary>
    protected AllocationBound MeasureProcessAllocations(long objectLength) =>
        AllocationBound.Start(Name, objectLength, _measuring && IsolatedProcess, processWide: true);

    /// <summary>
    /// Decodes every seed once before the first input, unmeasured, so one-time costs (static state, the runtime's
    /// tables, and the buffer pool's slabs) fall outside every measured decoding.
    /// </summary>
    private void Warm()
    {
        lock (_warming)
        {
            if (_warm)
            {
                return;
            }

            var index = 0;
            foreach (var seed in Seeds(GoldenVectors.Find()))
            {
                try
                {
                    _ = CheckWithSeal(seed);
                }
                catch (FuzzFindingException finding)
                {
                    throw new FuzzFindingException(string.Create(CultureInfo.InvariantCulture, $"Seed {index} broke the oracle while warming: {finding.Message}"), finding);
                }

                index++;
            }

            _warm = true;
            _measuring = true;
        }
    }

    private FuzzOutcome CheckWithSeal(ReadOnlySpan<byte> input)
    {
        var outcome = CheckOne(input, "input");
        if (Seal(input) is { } sealedInput && !input.SequenceEqual(sealedInput))
        {
            outcome |= CheckOne(sealedInput, "sealed input");
        }

        return outcome;
    }

    private FuzzOutcome CheckOne(ReadOnlySpan<byte> input, string form)
    {
        try
        {
            return Decode(input);
        }
        catch (Exception exception) when (exception is not FuzzFindingException)
        {
            throw new FuzzFindingException(
                string.Create(CultureInfo.InvariantCulture, $"{Name} threw {exception.GetType().FullName} for the {input.Length}-byte {form}: {exception.Message}"),
                exception);
        }
    }
}

/// <summary>
/// Bounds the bytes one decoding allocates: a small allowance plus a multiple of the bytes of the object it decodes,
/// so no length field drives an allocation beyond the bytes which carry it.
/// </summary>
public readonly struct AllocationBound
{
    /// <summary>
    /// The allowance every decoding has, whatever its object: the decoder's fixed tables and the runtime's allocation
    /// granularity. One-time costs fall outside the measurement (<see cref="FuzzTarget"/> warms each target first).
    /// </summary>
    public const long Allowance = 64L << 10;

    /// <summary>
    /// The bytes a decoding may allocate per byte of its object: the decoded values and their copies of the bytes.
    /// </summary>
    public const long PerObjectByte = 64;

    private readonly string _target;
    private readonly long _start;
    private readonly long _limit;
    private readonly bool _enabled;
    private readonly bool _processWide;

    private AllocationBound(string target, long start, long limit, bool enabled, bool processWide)
    {
        _target = target;
        _start = start;
        _limit = limit;
        _enabled = enabled;
        _processWide = processWide;
    }

    /// <summary>
    /// Throws a finding when the decoding allocated more than its bound.
    /// </summary>
    public void Check(string decoder)
    {
        if (!_enabled)
        {
            return;
        }

        var allocated = Allocated(_processWide) - _start;
        if (allocated > _limit)
        {
            throw new FuzzFindingException(
                string.Create(CultureInfo.InvariantCulture, $"{_target}: {decoder} allocated {allocated} bytes, above its bound of {_limit}."));
        }
    }

    internal static AllocationBound Start(string target, long objectLength, bool enabled, bool processWide) =>
        new(target, Allocated(processWide), Allowance + PerObjectByte * objectLength, enabled, processWide);

    private static long Allocated(bool processWide) =>
        processWide ? GC.GetTotalAllocatedBytes(precise: true) : GC.GetAllocatedBytesForCurrentThread();
}

/// <summary>
/// A decoder broke its target's oracle: it threw an exception its format does not document, ruled on bytes otherwise
/// than the format's rules do, or allocated beyond its bound.
/// </summary>
public sealed class FuzzFindingException : Exception
{
    public FuzzFindingException()
    {
    }

    public FuzzFindingException(string message)
        : base(message)
    {
    }

    public FuzzFindingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
