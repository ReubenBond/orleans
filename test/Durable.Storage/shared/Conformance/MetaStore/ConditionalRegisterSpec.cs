using System.Diagnostics;
using System.Globalization;
using Microsoft.Accordant;

namespace Orleans.Durable.Storage.Testing;

#pragma warning disable CA1852 // Accordant generates a virtual clone factory for state types.

/// <summary>
/// The abstract state of one <see cref="Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister"/>: whether it exists, and its value
/// (metastore.md §2.2, §5).
/// </summary>
/// <remarks>
/// Each value is written by at most one write request, so a value names the version the write which wrote it gave the
/// register (MS-I5).
/// </remarks>
[State]
internal partial class ConditionalRegisterState
{
    /// <summary>
    /// Gets or sets a value indicating whether the register exists.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// Gets or sets the register's value, when it exists.
    /// </summary>
    public byte Value { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller has learned the current value's version, from the write which
    /// applied it or from a read which returned it.
    /// </summary>
    public bool VersionKnown { get; set; }

    /// <summary>
    /// Gets or sets the values the register's writes carried, whatever their outcomes.
    /// </summary>
    public List<byte> Written { get; set; } = [];
}

#pragma warning restore CA1852

/// <summary>
/// A <c>WriteAsync</c> of <see cref="ConditionalRegisterSpec.Payload"/> of <paramref name="Value"/>, naming the
/// version of the write which wrote <paramref name="Expected"/>, or creating the register when it is
/// <see langword="null"/>.
/// </summary>
internal sealed record RegisterWrite(byte? Expected, byte Value)
{
    public override string ToString() => Expected is { } expected
        ? string.Create(CultureInfo.InvariantCulture, $"Write({expected} → {Value})")
        : string.Create(CultureInfo.InvariantCulture, $"Create({Value})");
}

/// <summary>
/// A <c>ReadAsync</c>.
/// </summary>
internal sealed record RegisterRead
{
    public static RegisterRead Instance { get; } = new();

    public override string ToString() => "Read";
}

/// <summary>
/// The kinds of <see cref="RegisterOutcome"/>.
/// </summary>
internal enum RegisterOutcomeKind
{
    /// <summary>A write answers <see cref="Orleans.Durable.Storage.Engine.MetaStore.RegisterWriteOutcome.Applied"/>.</summary>
    Applied,

    /// <summary>A write answers <see cref="Orleans.Durable.Storage.Engine.MetaStore.RegisterWriteOutcome.Mismatch"/>.</summary>
    Mismatch,

    /// <summary>A read reports no register.</summary>
    Absent,

    /// <summary>A read reports the register's value.</summary>
    Present,

    /// <summary>The call gave no answer: it threw, or its process crashed (metastore.md §2.2).</summary>
    Unknown,
}

/// <summary>
/// What a register call answered: for <see cref="RegisterOutcomeKind.Present"/>, with the value read.
/// </summary>
internal sealed record RegisterOutcome(RegisterOutcomeKind Kind, byte Value = 0)
{
    public static RegisterOutcome Unknown { get; } = new(RegisterOutcomeKind.Unknown);

    public override string ToString() => Kind == RegisterOutcomeKind.Present
        ? string.Create(CultureInfo.InvariantCulture, $"Present({Value})")
        : Kind.ToString();
}

/// <summary>
/// The executable spec of <see cref="Orleans.Durable.Storage.Engine.MetaStore.IConditionalRegister"/> (metastore.md §2.2, §5): a write applies
/// exactly when the register holds the version it names, or is absent for a create (MS-I1, MS-I2), and a read returns
/// the register's value. Since the register's state is durable, the spec also judges what a register recovers to after a
/// crash (MS-I1).
/// </summary>
/// <remarks>
/// With unknown outcomes, a call may give no answer, as one which throws or whose process crashes does. An unknown
/// write is a pending step function which may take effect once at any later point, and only where it applies, or never
/// (MS-I3); an unknown read asserts nothing.
/// </remarks>
internal static class ConditionalRegisterSpec
{
    /// <summary>
    /// The name of the write operation.
    /// </summary>
    internal const string Write = "Write";

    /// <summary>
    /// The name of the read operation.
    /// </summary>
    internal const string Read = "Read";

    /// <summary>
    /// Returns the bytes a write of <paramref name="value"/> stores: <paramref name="value"/> repeated, from 1 to 1024
    /// times, so a provider's stored values span one or several sectors.
    /// </summary>
    internal static byte[] Payload(byte value) => [.. Enumerable.Repeat(value, 1 + value * 211 % 1024)];

    /// <summary>
    /// Returns whether a caller may issue <paramref name="request"/> in <paramref name="state"/>: each write carries a
    /// value no earlier write carried, so a value names one version of the register (MS-I5).
    /// </summary>
    internal static bool IsCallerAllowed(object request, ConditionalRegisterState state) =>
        request is not RegisterWrite write || !state.Written.Contains(write.Value);

    /// <summary>
    /// Returns whether a caller may not issue <paramref name="first"/> and <paramref name="second"/> concurrently: two
    /// writes of one value.
    /// </summary>
    internal static bool Conflicts(object first, object second) =>
        first is RegisterWrite a && second is RegisterWrite b && a.Value == b.Value;

    /// <summary>
    /// Creates the spec; with <paramref name="unknownOutcomes"/>, every call may also give no answer, and with
    /// <paramref name="appliedMismatch"/>, an applying write may answer <see cref="RegisterOutcomeKind.Mismatch"/> as a
    /// backend-SDK retry of an applied attempt does (metastore.md §2.2, §6).
    /// </summary>
    /// <remarks>
    /// <c>Allows</c> and <c>AllowsConcurrent</c> assume callers enforce <see cref="IsCallerAllowed"/> and
    /// <see cref="Conflicts"/>, so every write carries a value no earlier or concurrent write carried (MS-I5).
    /// </remarks>
    internal static Spec<ConditionalRegisterState> Create(bool unknownOutcomes, bool appliedMismatch = false)
    {
        var spec = new Spec<ConditionalRegisterState>();
        spec.Operation<RegisterWrite, RegisterOutcome>(Write, (request, state) =>
        {
            var applies = Applies(request, state);
            ExpectedOutcome[] answered = applies
                ? appliedMismatch
                    ? [
                        Is(RegisterOutcomeKind.Applied).ThenState<ConditionalRegisterState>(next => Store(request, Wrote(next, request.Value), versionKnown: true)),
                        Is(RegisterOutcomeKind.Mismatch).ThenState<ConditionalRegisterState>(next => Store(request, Wrote(next, request.Value), versionKnown: false)),
                    ]
                    : [Is(RegisterOutcomeKind.Applied).ThenState<ConditionalRegisterState>(next => Store(request, Wrote(next, request.Value), versionKnown: true))]
                : [Is(RegisterOutcomeKind.Mismatch).ThenState<ConditionalRegisterState>(next => Wrote(next, request.Value))];
            if (!unknownOutcomes)
            {
                return answered.Length == 1 ? answered[0] : Expect.OneOf(answered);
            }

            // A write which does not apply at dispatch either targets an existing register for a create, names a stale
            // version, or names a value whose version the executor never learned and thus passed a synthetic version: it
            // can never apply later (MS-I3, MS-I5).
            var unknown = Is(RegisterOutcomeKind.Unknown).ThenState<ConditionalRegisterState>(next => Wrote(next, request.Value));
            return Expect.OneOf([.. answered, applies ? unknown.Triggers(new PendingWrite(request)) : unknown]);
        });
        spec.Operation<RegisterRead, RegisterOutcome>(Read, (_, state) =>
        {
            ExpectedOutcome answered = state.Exists
                ? Expect.That<RegisterOutcome>(
                    outcome => outcome.Kind == RegisterOutcomeKind.Present && outcome.Value == state.Value,
                    string.Create(CultureInfo.InvariantCulture, $"Expected Present({state.Value})."))
                    .ThenState<ConditionalRegisterState>(static next => next.VersionKnown = true)
                : Is(RegisterOutcomeKind.Absent).SameState();
            return unknownOutcomes ? Expect.OneOf(answered, Is(RegisterOutcomeKind.Unknown).SameState()) : answered;
        });
        return spec;
    }

    /// <summary>
    /// Returns whether <paramref name="write"/> applies in <paramref name="state"/>: a create to an absent register,
    /// and any other write to a register holding the version it names when the caller has learned that version (MS-I1,
    /// MS-I2, MS-I5).
    /// </summary>
    private static bool Applies(RegisterWrite write, ConditionalRegisterState state) =>
        write.Expected is { } expected ? state.Exists && state.Value == expected && state.VersionKnown : !state.Exists;

    private static ConditionalRegisterState Wrote(ConditionalRegisterState state, byte value)
    {
        Debug.Assert(!state.Written.Contains(value), $"ConditionalRegisterSpec assumes unique write values (IsCallerAllowed/Conflicts); {value} was already written.");
        state.Written.Add(value);
        return state;
    }

    private static void Store(RegisterWrite write, ConditionalRegisterState state, bool versionKnown)
    {
        state.Exists = true;
        state.Value = write.Value;
        state.VersionKnown = versionKnown;
    }

    private static ExpectedOutcomeBuilder<RegisterOutcome> Is(RegisterOutcomeKind kind) =>
        Expect.That<RegisterOutcome>(outcome => outcome.Kind == kind, string.Create(CultureInfo.InvariantCulture, $"Expected {kind}."));

    /// <summary>
    /// A write with an unknown outcome, which may take effect once at any later point, only where it applies (MS-I3).
    /// </summary>
    private sealed class PendingWrite(RegisterWrite write) : BaseStepFunction
    {
        public override string StepFunctionId => write.ToString();

        protected override IList<StepResult> ApplyInternal(IState state)
        {
            var next = (ConditionalRegisterState)((ConditionalRegisterState)state).Clone();
            if (Applies(write, next))
            {
                Store(write, next, versionKnown: false);
            }

            return [new StepResult { State = next }];
        }
    }
}
