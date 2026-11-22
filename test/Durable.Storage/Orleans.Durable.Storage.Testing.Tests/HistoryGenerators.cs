using System.Buffers.Binary;
using System.Globalization;
using CsCheck;
using EtcdHistoryOperation = Orleans.Durable.Storage.Testing.HistoryOperation<Orleans.Durable.Storage.Testing.Tests.EtcdInput, Orleans.Durable.Storage.Testing.Tests.EtcdOutput>;
using JournalOperation = Orleans.Durable.Storage.Testing.HistoryOperation<Orleans.Durable.Storage.Testing.JournalInput, Orleans.Durable.Storage.Testing.JournalOutput>;

namespace Orleans.Durable.Storage.Testing.Tests;

/// <summary>
/// Generates linearizable histories from sequential executions and injects known violations into them.
/// </summary>
/// <remarks>
/// An execution runs operations one after another through the model's semantics, the <c>i</c>th at point <c>10 i</c>.
/// Each operation's interval contains its point, so the points' order is a linearization which respects real time:
/// if one operation returns before another is invoked, its point is earlier. An operation with an unknown outcome which
/// took no effect has an unbounded response and is linearized after every other operation, where its effect, if any,
/// is never observed.
/// </remarks>
internal static class HistoryGenerators
{
    private const int Spacing = 10;

    /// <summary>Gets the steps of a journal execution: raw choices which <see cref="Journal"/> interprets.</summary>
    public static Gen<JournalStep> JournalSteps { get; } =
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 9], Gen.Int[0, 5], Gen.Int[0, 9], Gen.Int[0, 3 * Spacing], Gen.Int[0, 3 * Spacing])
            .Select(static (key, kind, expected, outcome, before, after) => new JournalStep(key, kind, expected, outcome, before, after));
    /// <summary>Gets the steps of an etcd execution: raw choices which <see cref="Etcd"/> interprets.</summary>
    public static Gen<EtcdStep> EtcdSteps { get; } =
        Gen.Select(Gen.Int[0, 9], Gen.Int[0, 9], Gen.Int[0, 3], Gen.Int[0, 3 * Spacing], Gen.Int[0, 3 * Spacing])
            .Select(static (kind, outcome, expected, before, after) => new EtcdStep(kind, outcome, expected, before, after));

    /// <summary>
    /// Runs a journal execution over <paramref name="keys"/> journals: appends, replaces, deletes, expiries when
    /// <paramref name="expire"/>, and reads, with stale and never-held expected versions which are rejected, appends
    /// refused for the store's limits, ambiguous writes which take effect or do not, writes which are fenced, moved, or
    /// throttled, and rebases, which give a journal a version it never held and a read which names it. Each accepted
    /// entry and rebase takes a version of its own, which the execution names. The journals are folded by
    /// <see cref="JournalReference"/>, independently of the model: a writer writes at a version an answer gave it.
    /// </summary>
    public static JournalExecution Journal(IReadOnlyList<JournalStep> steps, int keys, bool expire)
    {
        var journals = Enumerable.Range(0, keys).Select(static _ => new JournalReference()).ToArray();
        var operations = new List<JournalOperation>();
        var writes = new List<AcceptedWrite>();
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var key = step.Key % keys;
            var journal = journals[key];
            var point = Spacing * (index + 1L);
            var call = point - step.Before;
            var response = point + step.After;
            var name = Name(key);
            JournalInput input;
            JournalOutput output;
            if (step.Kind == 9)
            {
                journal.Rebase(Version(index, "r"));
                input = JournalInput.Read(name);
                output = journal.Answer();
            }
            else if (step.Kind is 6 or 7 or 8)
            {
                input = JournalInput.Read(name);
                output = step.Outcome switch
                {
                    8 => JournalOutput.Ambiguous,
                    9 => JournalOutput.Moved,
                    _ => journal.Answer(),
                };
            }
            else
            {
                input = Write(step, index, name, journal, expire, UniquePayload(index));
                var applies = journal.Applies(input);
                var version = Version(index, "v");
                (output, var effect) = (applies, step.Outcome) switch
                {
                    (true, 5) when input.Kind == JournalOperationKind.Append => (JournalOutput.ReplaceRequired, false),
                    (true, < 6) => (JournalOutput.Accepted(version), true),
                    (true, 6) => (JournalOutput.Ambiguous, true),
                    (_, 6 or 7) => (JournalOutput.Ambiguous, false),
                    (_, 8) => (JournalOutput.Fenced, false),
                    (_, 9) => (index % 2 == 0 ? JournalOutput.Moved : JournalOutput.Throttled, false),
                    _ => (JournalOutput.Rejected(journal.Version), false),
                };
                if (output.Outcome == JournalOutcome.Ambiguous)
                {
                    response = HistoryOperation.Unbounded;
                }

                if (output.Outcome == JournalOutcome.Rejected)
                {
                    journal.Announce();
                }

                if (effect)
                {
                    var before = journal.Snapshot();
                    journal.Apply(input, version, announced: output.Outcome == JournalOutcome.Accepted);
                    if (output.Outcome == JournalOutcome.Accepted)
                    {
                        writes.Add(new AcceptedWrite(operations.Count, key, input, before));
                    }
                }
            }

            operations.Add(new JournalOperation(0, input, call, output, response));
        }

        return new JournalExecution([.. operations], writes, [.. journals.Select(static journal => journal.Snapshot())]);
    }
    /// <summary>
    /// Adds a read of a journal, invoked after an accepted write to it returned, which answers the journal's state
    /// before that write. Its version is one the journal held and no longer holds, so no linearization allows it. A history
    /// without an accepted write gains one.
    /// </summary>
    public static JournalOperation[] InjectStaleRead(JournalExecution execution, int choice, int gap)
    {
        var (operations, write) = WithAcceptedWrite(execution, choice);
        var call = operations[write.Index].Return + 1 + gap;
        return
        [
            .. operations,
            new JournalOperation(0, JournalInput.Read(write.Input.Key), call, write.Before.Answer(), call + Spacing),
        ];
    }

    /// <summary>
    /// Adds a second accepted write at the expected version of an accepted write, at any time: one of two acknowledged
    /// writes is lost. A version is the journal's once, so no linearization allows it. A history without an accepted write
    /// gains one.
    /// </summary>
    public static JournalOperation[] InjectLostWrite(JournalExecution execution, int choice, int position)
    {
        var (operations, write) = WithAcceptedWrite(execution, choice);
        var call = position % (Spacing * (operations.Length + 2L));
        return
        [
            .. operations,
            new JournalOperation(0, JournalInput.Append(write.Input.Key, write.Input.Expected!, UniquePayload(operations.Length)), call, JournalOutput.Accepted(Version(operations.Length, "lost")), call + Spacing),
        ];
    }

    /// <summary>
    /// Replaces one answered read's response with an arbitrary state, which may or may not be linearizable: its existence,
    /// and a version among the first an execution names, <c>Origin</c> included.
    /// </summary>
    public static JournalOperation[] PerturbRead(IReadOnlyList<JournalOperation> operations, int choice, bool exists, int version)
    {
        var history = operations.ToArray();
        var reads = Enumerable.Range(0, history.Length)
            .Where(index => history[index].Input.Kind == JournalOperationKind.Read && history[index].Output.Outcome == JournalOutcome.Accepted)
            .ToArray();
        if (reads.Length > 0)
        {
            var index = reads[choice % reads.Length];
            history[index] = history[index] with { Output = history[index].Output with { Exists = exists, Version = version == 0 ? JournalModel.Origin : Version(version - 1, "v") } };
        }

        return history;
    }
    /// <summary>
    /// Runs an etcd execution on one register: reads, some timing out; writes of unique values; and compare-and-sets to
    /// unique values from the current or an earlier value, some with unknown outcomes which take effect where they apply
    /// or do not.
    /// </summary>
    public static EtcdExecution Etcd(IReadOnlyList<EtcdStep> steps)
    {
        int? state = null;
        var written = new List<int>();
        var operations = new List<EtcdHistoryOperation>();
        var writes = new List<int>();
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var point = Spacing * (index + 1L);
            var call = point - step.Before;
            var response = point + step.After;
            var value = index + 1;
            EtcdInput input;
            EtcdOutput output;
            var definite = false;
            if (step.Kind < 4)
            {
                input = EtcdInput.Read;
                output = step.Outcome < 8 ? EtcdOutput.Read(state) : EtcdOutput.Timeout;
            }
            else
            {
                if (step.Kind < 7)
                {
                    input = EtcdInput.Write(value);
                }
                else
                {
                    var expected = step.Expected < 2 || written.Count == 0 ? state ?? 0 : written[(step.Expected + index) % written.Count];
                    input = EtcdInput.CompareAndSet(expected, value);
                }

                _ = EtcdModel.Instance.Step(state, input, EtcdOutput.Timeout, out var next);
                var applies = next != state;
                switch (step.Outcome)
                {
                    case < 8:
                        output = input.Operation == EtcdOperation.CompareAndSet ? EtcdOutput.CompareAndSet(applies) : EtcdOutput.Done;
                        definite = applies;
                        state = next;
                        break;
                    case 8:
                        output = EtcdOutput.Timeout;
                        response = HistoryOperation.Unbounded;
                        state = next;
                        break;
                    default:
                        output = EtcdOutput.Timeout;
                        response = HistoryOperation.Unbounded;
                        applies = false;
                        break;
                }

                if (applies)
                {
                    written.Add(value);
                }
            }

            if (definite)
            {
                writes.Add(operations.Count);
            }

            operations.Add(new EtcdHistoryOperation(0, input, call, output, response));
        }

        return new EtcdExecution([.. operations], writes);
    }

    /// <summary>
    /// Adds a read, invoked after a definite write returned, of the value of a definite write which returned before
    /// that one was invoked, or of the initial absent value. Values are written once, so no linearization allows it. A
    /// history without a definite write gains one.
    /// </summary>
    public static EtcdHistoryOperation[] InjectStaleRegisterRead(EtcdExecution execution, int choice, int gap)
    {
        var operations = execution.Operations.ToList();
        var writes = execution.DefiniteWrites.ToList();
        if (writes.Count == 0)
        {
            var call = Spacing * (operations.Count + 1L) + 3 * Spacing;
            writes.Add(operations.Count);
            operations.Add(new EtcdHistoryOperation(0, EtcdInput.Write(operations.Count + 1), call, EtcdOutput.Done, call + Spacing));
        }

        var overwrite = operations[writes[choice % writes.Count]];
        var earlier = writes.Select(index => operations[index]).Where(write => write.Return < overwrite.Call).ToArray();
        int? stale = earlier.Length == 0 || choice % (earlier.Length + 1) == 0
            ? null
            : Written(earlier[choice % earlier.Length].Input);
        var readCall = overwrite.Return + 1 + gap;
        operations.Add(new EtcdHistoryOperation(0, EtcdInput.Read, readCall, EtcdOutput.Read(stale), readCall + Spacing));
        return [.. operations];
    }

    /// <summary>
    /// Assigns each operation the lowest client whose previous operation returned before it was invoked, as clients
    /// which run one operation at a time; an operation with an unbounded response keeps its client.
    /// </summary>
    public static HistoryOperation<TInput, TOutput>[] AssignClients<TInput, TOutput>(IReadOnlyList<HistoryOperation<TInput, TOutput>> operations)
    {
        var busyUntil = new List<long>();
        var history = new HistoryOperation<TInput, TOutput>[operations.Count];
        foreach (var index in Enumerable.Range(0, operations.Count).OrderBy(index => operations[index].Call))
        {
            var operation = operations[index];
            var client = busyUntil.FindIndex(end => end < operation.Call);
            if (client < 0)
            {
                client = busyUntil.Count;
                busyUntil.Add(operation.Return);
            }
            else
            {
                busyUntil[client] = operation.Return;
            }

            history[index] = operation with { ClientId = client };
        }

        return history;
    }

    /// <summary>
    /// Returns the history's invocations and responses in time order, invocations before responses at one instant.
    /// </summary>
    public static HistoryEvent<TInput, TOutput>[] ToEvents<TInput, TOutput>(IReadOnlyList<HistoryOperation<TInput, TOutput>> operations) =>
    [
        .. operations
            .SelectMany(static (operation, index) => new[]
            {
                (Time: operation.Call, Order: 0, Event: new HistoryEvent<TInput, TOutput> { Kind = HistoryEventKind.Call, Id = index, ClientId = operation.ClientId, Input = operation.Input }),
                (Time: operation.Return, Order: 1, Event: new HistoryEvent<TInput, TOutput> { Kind = HistoryEventKind.Return, Id = index, Output = operation.Output }),
            })
            .OrderBy(static entry => entry.Time)
            .ThenBy(static entry => entry.Order)
            .Select(static entry => entry.Event),
    ];

    private static (JournalOperation[] Operations, AcceptedWrite Write) WithAcceptedWrite(JournalExecution execution, int choice)
    {
        if (execution.AcceptedWrites.Count > 0)
        {
            return (execution.Operations, execution.AcceptedWrites[choice % execution.AcceptedWrites.Count]);
        }

        // A read first names the journal's state, which the write then expects, as a writer would.
        var state = execution.States[0];
        var read = Spacing * (execution.Operations.Length + 1L) + 3 * Spacing;
        var call = read + 2 * Spacing;
        var input = JournalInput.Append(Name(0), state.Version, UniquePayload(execution.Operations.Length));
        var write = new AcceptedWrite(execution.Operations.Length + 1, 0, input, state);
        return (
            [
                .. execution.Operations,
                new JournalOperation(0, JournalInput.Read(Name(0)), read, state.Answer(), read + Spacing),
                new JournalOperation(0, input, call, JournalOutput.Accepted(Version(execution.Operations.Length + 1_000, "v")), call + Spacing),
            ],
            write);
    }

    private static int? Written(EtcdInput input) => input.Operation == EtcdOperation.Write ? input.Argument : input.Replacement;

    /// <summary>
    /// Interprets a step as a write: mostly at the version an answer last gave for the journal, sometimes at a version it
    /// held before or one it never held.
    /// </summary>
    private static JournalInput Write(JournalStep step, int index, string name, JournalReference journal, bool expire, byte[] payload)
    {
        var expected = step.Expected switch
        {
            0 => journal.Held.Where(held => held != journal.Version).Order(StringComparer.Ordinal).FirstOrDefault() ?? journal.Known,
            5 => Version(index, "x"),
            _ => journal.Known,
        };
        if (step.Kind == 5 && expire)
        {
            return JournalInput.Expire(name, expected);
        }

        return step.Kind switch
        {
            3 => JournalInput.Replace(name, expected, payload),
            4 => JournalInput.Delete(name, expected),
            _ => JournalInput.Append(name, expected, payload),
        };
    }

    /// <summary>Returns the version an execution names for its step at <paramref name="index"/>: an entry's, a rebase's, or one never held.</summary>
    private static string Version(int index, string kind) => string.Create(CultureInfo.InvariantCulture, $"{kind}{index}");
    private static string Name(int key) => string.Create(CultureInfo.InvariantCulture, $"k{key}");

    private static byte[] UniquePayload(int index)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        return payload;
    }
}

internal readonly record struct JournalStep(int Key, int Kind, int Expected, int Outcome, int Before, int After);

internal readonly record struct EtcdStep(int Kind, int Outcome, int Expected, int Before, int After);

/// <summary>
/// An accepted write of a journal execution and the journal's state before it.
/// </summary>
internal sealed record AcceptedWrite(int Index, int Key, JournalInput Input, JournalSnapshot Before);

/// <summary>
/// A journal execution's history, its accepted writes, and each journal's final state.
/// </summary>
internal sealed record JournalExecution(JournalOperation[] Operations, IReadOnlyList<AcceptedWrite> AcceptedWrites, IReadOnlyList<JournalSnapshot> States);

/// <summary>A journal's state as <see cref="JournalReference"/> folded it: its existence, its version, and its content.</summary>
internal sealed record JournalSnapshot(bool Exists, string Version, byte[] Content)
{
    /// <summary>Returns a read's answer of this state.</summary>
    public JournalOutput Answer() => JournalOutput.Read(Exists, Version, Content);
}

/// <summary>
/// A journal folded by journals.md §2.1, independently of <see cref="JournalModel"/>: its content, existence, and deletion;
/// its version, which a rebase or an ambiguous write may change before any answer names it; every version it held; and
/// the version an answer last gave, at which a writer writes.
/// </summary>
internal sealed class JournalReference
{
    private readonly List<byte[]> _content = [];
    private bool _opened;
    private bool _deleted;

    public string Version { get; private set; } = JournalModel.Origin;

    public string Known { get; private set; } = JournalModel.Origin;

    public HashSet<string> Held { get; } = [with(StringComparer.Ordinal), JournalModel.Origin];

    /// <summary>Returns whether an entry applies: at the journal's version, and an <c>Expired</c> only when it is deleted.</summary>
    public bool Applies(JournalInput input) =>
        string.Equals(input.Expected, Version, StringComparison.Ordinal) && (input.Kind != JournalOperationKind.Expire || _deleted);

    /// <summary>Applies an entry which applies, at <paramref name="version"/>, which an answer gave when <paramref name="announced"/>.</summary>
    public void Apply(JournalInput input, string version, bool announced)
    {
        switch (input.Kind)
        {
            case JournalOperationKind.Append:
                _content.Add(input.Payload.ToArray());
                (_opened, _deleted) = (true, false);
                break;
            case JournalOperationKind.Replace:
                _content.Clear();
                _content.Add(input.Payload.ToArray());
                (_opened, _deleted) = (true, false);
                break;
            case JournalOperationKind.Delete:
                _content.Clear();
                (_opened, _deleted) = (false, true);
                break;
            case JournalOperationKind.Expire:
                _content.Clear();
                (_opened, _deleted) = (false, false);
                break;
            case JournalOperationKind.Read:
            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Only a write applies.");
        }

        Version = version;
        _ = Held.Add(version);
        if (announced)
        {
            Known = version;
        }
    }

    /// <summary>Gives the journal's state a version it never held, its content unchanged.</summary>
    public void Rebase(string version)
    {
        Version = version;
        _ = Held.Add(version);
    }

    /// <summary>Records that an answer gave the journal's version.</summary>
    public void Announce() => Known = Version;

    /// <summary>Returns a read's answer of the journal, which gives its version.</summary>
    public JournalOutput Answer()
    {
        Announce();
        return Snapshot().Answer();
    }

    public JournalSnapshot Snapshot() => new(_opened, Version, [.. _content.SelectMany(static payload => payload)]);
}

/// <summary>
/// An etcd execution's history and the indexes of its definite writes: writes and successful compare-and-sets which
/// returned.
/// </summary>
internal sealed record EtcdExecution(EtcdHistoryOperation[] Operations, IReadOnlyList<int> DefiniteWrites);
