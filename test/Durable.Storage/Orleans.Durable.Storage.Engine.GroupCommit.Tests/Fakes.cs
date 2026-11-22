using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.GroupCommit.Tests;

/// <summary>A value of the fake store: an epoch, and a set of items which the changes add.</summary>
internal sealed record FakeValue(long Epoch, string Items)
{
    public static FakeValue Empty { get; } = new(0, string.Empty);

    public ImmutableSortedSet<string> Set => Items.Length == 0 ? [] : [.. Items.Split(',')];

    public bool Contains(string item) => Set.Contains(item);

    public FakeValue With(long epoch, IEnumerable<string> items) => new(epoch, string.Join(',', Set.Union(items)));
}

/// <summary>A version of the fake store's value.</summary>
internal sealed record FakeVersion(long Number);

/// <summary>
/// One store of one value with a conditional write, scripted by the tests: reads and writes can be held, failed, made to
/// apply and then fail, or answered as mismatches; a read can find the value corrupt. It records each call, and the
/// caller's <see cref="Context"/> during it.
/// </summary>
internal sealed class FakeStore
{
    private readonly Lock _lock = new();
    private long _versions;
    private TaskCompletionSource? _readGate;
    private HeldWrite? _heldWrite;

    public static AsyncLocal<string?> Context { get; } = new();

    public FakeValue Value { get; private set; } = FakeValue.Empty;

    public FakeVersion? Version { get; private set; }

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public int Applied { get; private set; }

    public List<string?> Contexts { get; } = [];

    public Exception? ReadFailure { get; set; }

    /// <summary>Gets or sets the clock whose timestamp each write records as it starts.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    public List<long> WriteStarts { get; } = [];

    public bool Corrupt { get; set; }

    /// <summary>Gets or sets the number of writes, from the next, the store answers <see cref="GroupCommitWrite{TVersion}.Mismatch"/> after applying them.</summary>
    public int AppliedMismatches { get; set; }

    /// <summary>Holds the next read until the returned gate is released.</summary>
    public TaskCompletionSource HoldNextRead()
    {
        lock (_lock)
        {
            return _readGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Holds the next write until the test decides it.</summary>
    public HeldWrite HoldNextWrite()
    {
        lock (_lock)
        {
            return _heldWrite = new HeldWrite();
        }
    }

    /// <summary>Applies <paramref name="value"/> behind the committer's back, at a new version.</summary>
    public void Put(FakeValue value)
    {
        lock (_lock)
        {
            Value = value;
            Version = new FakeVersion(++_versions);
        }
    }

    public async ValueTask<GroupCommitRead<FakeValue, FakeVersion>> ReadAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? gate;
        lock (_lock)
        {
            Reads++;
            Contexts.Add(Context.Value);
            (gate, _readGate) = (_readGate, null);
        }

        if (gate is not null)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        if (Corrupt)
        {
            return new GroupCommitRead<FakeValue, FakeVersion>.Corrupt(new InvalidDataException("The fake value is corrupt."));
        }

        lock (_lock)
        {
            return new GroupCommitRead<FakeValue, FakeVersion>.Found(Value, Version);
        }
    }

    public async ValueTask<GroupCommitWrite<FakeVersion>> WriteAsync(FakeVersion? expected, FakeValue value, CancellationToken cancellationToken)
    {
        HeldWrite? held;
        lock (_lock)
        {
            Writes++;
            WriteStarts.Add(Clock.GetTimestamp());
            Contexts.Add(Context.Value);
            (held, _heldWrite) = (_heldWrite, null);
        }

        if (held is null)
        {
            return Apply(expected, value);
        }

        held.Arrive();
        var decision = await held.Decision.WaitAsync(cancellationToken);
        return decision switch
        {
            WriteDecision.Answer => Apply(expected, value),
            WriteDecision.FailApplied => FailAfterApplying(expected, value),
            WriteDecision.Fail => throw new IOException("The fake write failed before it applied."),
            WriteDecision.Mismatch => new GroupCommitWrite<FakeVersion>.Mismatch(),
            WriteDecision.Refuse => new GroupCommitWrite<FakeVersion>.Refused(new FakeRefusalException()),
            _ => throw new InvalidOperationException(),
        };
    }

    private GroupCommitWrite<FakeVersion> FailAfterApplying(FakeVersion? expected, FakeValue value)
    {
        _ = Apply(expected, value);
        throw new IOException("The fake write failed after it applied.");
    }

    private GroupCommitWrite<FakeVersion> Apply(FakeVersion? expected, FakeValue value)
    {
        lock (_lock)
        {
            if (expected != Version)
            {
                return new GroupCommitWrite<FakeVersion>.Mismatch();
            }

            Value = value;
            Version = new FakeVersion(++_versions);
            Applied++;
            if (AppliedMismatches > 0)
            {
                AppliedMismatches--;
                return new GroupCommitWrite<FakeVersion>.Mismatch();
            }

            return new GroupCommitWrite<FakeVersion>.Applied(Version);
        }
    }

    internal enum WriteDecision
    {
        Answer,
        FailApplied,
        Fail,
        Mismatch,
        Refuse,
    }

    /// <summary>A held write, which arrives at the store and waits for the test's decision.</summary>
    internal sealed class HeldWrite
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<WriteDecision> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Arrived => _arrived.Task;

        public Task<WriteDecision> Decision => _decision.Task;

        public void Arrive() => _arrived.TrySetResult();

        public void Decide(WriteDecision decision) => _decision.TrySetResult(decision);
    }
}

/// <summary>
/// The fake value's rules: a change <c>x</c> adds item <c>x</c>, answered <c>applied@e</c> or <c>superseded@e</c>; a change
/// <c>refuse:x</c> never applies, and is answered <c>refused</c>, a negative answer a held write could revoke, so the
/// fold fences while one may land. Each member can be made to throw, or to misbehave, by the tests.
/// </summary>
internal sealed class FakeRules(FakeStore store) : IGroupCommitRules<FakeValue, FakeVersion, string, string, GroupCommitWrite<FakeVersion>>
{
    public List<(FakeValue Basis, bool HeldWriteMayLand, string[] Changes)> Folds { get; } = [];

    /// <summary>Gets or sets which members throw: each call of a member for which it answers true.</summary>
    public Func<string, bool>? Fails { get; set; }

    public bool Undecodable { get; set; }

    public string? Violation { get; set; }

    public bool AlwaysFence { get; set; }

    public bool Malformed { get; set; }

    /// <summary>Gets or sets a value indicating whether each answer is a failure which carries no exception.</summary>
    public bool FailureWithoutException { get; set; }

    /// <summary>Gets or sets what each batch's <c>OnApplied</c> runs, as a test's probe.</summary>
    public Action? OnAppliedProbe { get; set; }

    public ValueTask<GroupCommitRead<FakeValue, FakeVersion>> ReadAsync(CancellationToken cancellationToken) => store.ReadAsync(cancellationToken);

    public ValueTask<GroupCommitWrite<FakeVersion>> WriteAsync(FakeVersion? expectedVersion, GroupCommitEncoding encoded, CancellationToken cancellationToken) =>
        store.WriteAsync(expectedVersion, Decode(encoded.Content.AsReadOnlySequence()), cancellationToken);

    public GroupCommitWrite<FakeVersion> WriteOutcome(GroupCommitWrite<FakeVersion> reply)
    {
        Check(nameof(WriteOutcome));
        return reply;
    }

    public GroupCommitEncoding Encode(FakeValue value)
    {
        Check(nameof(Encode));
        return new(ArcBuffer.Wrap(Encoding.UTF8.GetBytes($"{value.Epoch}|{value.Items}")));
    }

    public bool DecodesAs(ReadOnlySequence<byte> encoded, FakeValue value) => !Undecodable && Decode(encoded) == value;

    public Exception OutcomeUnknown(string message, Exception cause) => new GroupCommitOutcomeUnknownException(message, cause);

    public IGroupCommitBatch<FakeValue, FakeVersion, string> Fold(FakeValue basis, bool heldWriteMayLand, IReadOnlyList<string> changes)
    {
        Folds.Add((basis, heldWriteMayLand, [.. changes]));
        Check(nameof(Fold));
        var carried = changes.Select(change => !change.StartsWith("refuse:", StringComparison.Ordinal) && !basis.Contains(change)).ToArray();
        FakeValue? written = carried.Contains(true)
            ? basis.With(basis.Epoch + 1, changes.Where((_, index) => carried[index]))
            : heldWriteMayLand && changes.Any(static change => change.StartsWith("refuse:", StringComparison.Ordinal)) || AlwaysFence
                ? basis with { Epoch = basis.Epoch + 1 }
                : null;
        return new Batch(this, changes, carried, written);
    }

    public void Check(string member)
    {
        if (Fails?.Invoke(member) == true)
        {
            throw new FormatException($"The fake rules' {member} failed.");
        }
    }

    private static FakeValue Decode(ReadOnlySequence<byte> encoded)
    {
        var parts = Encoding.UTF8.GetString(encoded).Split('|');
        return new FakeValue(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1]);
    }

    private sealed class Batch(FakeRules rules, IReadOnlyList<string> changes, bool[] carried, FakeValue? written)
        : IGroupCommitBatch<FakeValue, FakeVersion, string>
    {
        public FakeValue? Written => written;

        public bool Carries(int index) => carried[index];

        public GroupCommitAnswer<string> Settle(int index, FakeValue value)
        {
            rules.Check(nameof(Settle));
            if (rules.Malformed)
            {
                return default;
            }

            if (rules.FailureWithoutException)
            {
                return new GroupCommitAnswer<string>.Failed(null!);
            }

            return Answer(changes[index].StartsWith("refuse:", StringComparison.Ordinal) ? "refused" : value.Contains(changes[index]) ? $"applied@{value.Epoch}" : $"superseded@{value.Epoch}");
        }

        public GroupCommitAnswer<string>? Applied(int index, FakeValue read)
        {
            rules.Check(nameof(Applied));
            return read.Contains(changes[index]) ? Answer($"applied@{read.Epoch}") : Undecided;
        }

        public GroupCommitAnswer<string>? AfterUnknownOutcome(int index, FakeValue read, bool heldWriteMayLand)
        {
            rules.Check(nameof(AfterUnknownOutcome));
            if (changes[index].StartsWith("refuse:", StringComparison.Ordinal))
            {
                return heldWriteMayLand ? Undecided : Answer("refused");
            }

            return read.Contains(changes[index]) ? Answer($"applied@{read.Epoch}") : Undecided;
        }

        public string? Violation(FakeValue before) => rules.Violation;

        public void OnApplied(FakeVersion version)
        {
            rules.Check(nameof(OnApplied));
            rules.OnAppliedProbe?.Invoke();
        }

        private static GroupCommitAnswer<string>? Undecided => null;

        private static GroupCommitAnswer<string> Answer(string outcome) => new GroupCommitAnswer<string>.Answered(outcome);
    }
}

/// <summary>The fake store's refusal of a write, which wrote nothing.</summary>
internal sealed class FakeRefusalException : Exception
{
    public FakeRefusalException()
        : base("The fake store refused the write; it wrote nothing.")
    {
    }
}
