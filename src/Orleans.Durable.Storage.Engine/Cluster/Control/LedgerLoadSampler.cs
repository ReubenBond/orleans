using System.Collections.Immutable;
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A node's measure of the load of each ledger it is primary of and serves (orleans.md §5.6): the bytes its writer
/// proposed per second over <c>LoadWindow</c>, from <see cref="LedgerWriter.ProposedBytes"/>, sampled once per report on
/// the monotonic clock.
/// </summary>
/// <remarks>
/// A ledger's load is known once its writer's samples span a whole window: until then the node reports none for it, which
/// the controller reads as unknown, so a burst in a writer's first moments never splits its ledger. Samples are kept per
/// writer lifetime, since a writer's proposed bytes grow only for its lifetime: a ledger served under a new lifetime, or
/// missing from a sample, starts over.
/// </remarks>
internal sealed class LedgerLoadSampler
{
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private Dictionary<LedgerId, WriterSamples> _samples = [];

    /// <summary>Initializes a new instance of the <see cref="LedgerLoadSampler"/> class.</summary>
    /// <param name="window">The window, <c>LoadWindow</c>.</param>
    /// <param name="timeProvider">The clock whose timestamps time the samples.</param>
    public LedgerLoadSampler(TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _window = window;
        _time = timeProvider;
    }

    /// <summary>
    /// Records each writer's proposed bytes now and returns the proposed bytes per second over the window of each ledger
    /// whose writer's samples span it.
    /// </summary>
    /// <param name="proposed">The ledger, writer lifetime, and proposed bytes of each ledger the node is primary of and serves.</param>
    public ImmutableDictionary<LedgerId, long> Sample(IEnumerable<(LedgerId Ledger, SequencerInstanceId Writer, long ProposedBytes)> proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var now = _time.GetTimestamp();
        var next = new Dictionary<LedgerId, WriterSamples>();
        var load = ImmutableDictionary.CreateBuilder<LedgerId, long>();
        foreach (var (ledger, writer, bytes) in proposed)
        {
            var samples = _samples.TryGetValue(ledger, out var known) && known.Writer == writer ? known : new WriterSamples(writer);
            Debug.Assert(samples.Taken.Count == 0 || bytes >= samples.Taken[^1].Bytes, "A writer's proposed bytes only grow (ledgers.md §2.4).");
            samples.Taken.Add((now, bytes));
            next[ledger] = samples;

            // The newest sample at least a window old bounds the window; every older one is dropped.
            var bound = samples.Taken.FindLastIndex(sample => _time.GetElapsedTime(sample.At, now) >= _window);
            if (bound > 0)
            {
                samples.Taken.RemoveRange(0, bound);
            }

            if (bound >= 0)
            {
                var (since, before) = samples.Taken[0];
                load[ledger] = (long)((bytes - before) / _time.GetElapsedTime(since, now).TotalSeconds);
            }
        }

        _samples = next;
        return load.ToImmutable();
    }

    /// <summary>One writer lifetime's samples of a ledger, oldest first.</summary>
    private sealed class WriterSamples(SequencerInstanceId writer)
    {
        public SequencerInstanceId Writer { get; } = writer;

        public List<(long At, long Bytes)> Taken { get; } = [];
    }
}
