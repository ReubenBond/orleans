using System.Buffers;
using Microsoft.Accordant;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Model.Tests;

public sealed class MetaStoreModelTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>
    /// The memory register conforms to <see cref="ConditionalRegisterSpec"/> over every sequence of up to five creates,
    /// current, stale, and never-issued conditional writes, and reads (MS-I1, MS-I2, MS-I5): up to four writes apply in
    /// turn, each naming the version an earlier write returned, and a write naming a value no write applied names a
    /// well-formed version no write issued.
    /// </summary>
    [Fact]
    public async Task GeneratedSequencesConformToVersionedRegisterModel()
    {
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: false, appliedMismatch: false);
        spec.ExecuteWith<MetaStoreModelClient>()
            .Bind<RegisterWrite, RegisterOutcome>(ConditionalRegisterSpec.Write, static (client, request) => client.Write(request))
            .Bind<RegisterRead, RegisterOutcome>(ConditionalRegisterSpec.Read, static (client, _) => client.Read());
        var write = spec.GetOperation<RegisterWrite, RegisterOutcome>(ConditionalRegisterSpec.Write);
        var read = spec.GetOperation<RegisterRead, RegisterOutcome>(ConditionalRegisterSpec.Read);
        RegisterWrite[] writes = [new(null, 1), new(1, 2), new(1, 3), new(2, 4), new(4, 5)];
        var inputs = new InputSet();
        foreach (var request in writes)
        {
            inputs.Add(write.With(request, request.ToString()));
        }

        inputs.Add(read.With(RegisterRead.Instance, RegisterRead.Instance.ToString()));
        var initialState = new ConditionalRegisterState();
        var testCases = spec.GenerateTests(
            initialState,
            inputs,
            new TestGenerationOptions
            {
                MaxDepth = 5,
                ShouldApply = static (input, state) => ConditionalRegisterSpec.IsCallerAllowed(input.Request, (ConditionalRegisterState)state),
            });
        var client = new MetaStoreModelClient();
        var context = spec.CreateTestingContext();
        context.Register(client);

        var results = await spec.RunTests(
            context,
            initialState,
            testCases,
            new TestExecutionOptions
            {
                BeforeEachAsync = _ =>
                {
                    client.Reset();
                    return Task.CompletedTask;
                },
            });

        Assert.NotEmpty(testCases);
        Assert.All(results, static result => Assert.True(result.Success, result.LastFailureMessage));
    }

    /// <summary>
    /// Regression for #458: after an applied-mismatch create, the caller has not learned the created value's version, so
    /// a concurrent conditional write naming that value may dispatch with a synthetic version before a concurrent read
    /// learns it and mismatch without applying.
    /// </summary>
    [Fact]
    public void WriteAfterAppliedMismatchWithUnlearnedVersionMayMismatchWithoutApplying()
    {
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: true, appliedMismatch: true);
        var write = spec.GetOperation(ConditionalRegisterSpec.Write);
        var read = spec.GetOperation(ConditionalRegisterSpec.Read);

        var profile = new StateProfile(new ConditionalRegisterState());
        profile = Allow(spec, read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Absent), profile);
        profile = Allow(spec, read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Absent), profile);
        profile = Allow(spec, write, new RegisterWrite(null, 5), new RegisterOutcome(RegisterOutcomeKind.Mismatch), profile);

        var (concurrentAllowed, concurrentExplanation, afterConcurrent) = spec.AllowsConcurrent(
            profile,
            [
                (write, new RegisterWrite(10, 15), new RegisterOutcome(RegisterOutcomeKind.Mismatch)),
                (read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 5)),
                (write, new RegisterWrite(5, 13), new RegisterOutcome(RegisterOutcomeKind.Mismatch)),
            ]);
        Assert.True(concurrentAllowed, concurrentExplanation);

        Assert.True(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 5), afterConcurrent).IsValid);
        Assert.True(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 13), afterConcurrent).IsValid);
        Assert.False(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 15), afterConcurrent).IsValid);
        Assert.False(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Absent), afterConcurrent).IsValid);
    }

    /// <summary>
    /// A conditional write naming a value whose version the caller has not learned cannot apply immediately or as a
    /// pending unknown write; once the version is learned, an applied-mismatch write must advance the value and an
    /// unknown write may land later.
    /// </summary>
    [Fact]
    public void WriteWithUnlearnedVersionCannotApplyNowOrLaterAndLearnedVersionRemainsStrict()
    {
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: true, appliedMismatch: true);
        var write = spec.GetOperation(ConditionalRegisterSpec.Write);
        var read = spec.GetOperation(ConditionalRegisterSpec.Read);

        var afterAppliedMismatch = Allow(
            spec,
            write,
            new RegisterWrite(null, 5),
            new RegisterOutcome(RegisterOutcomeKind.Mismatch),
            new StateProfile(new ConditionalRegisterState()));

        // Without learning 5's version, Write(5 -> 13) passes a synthetic version: Applied is invalid, and Mismatch
        // leaves 5 in place.
        Assert.False(spec.Allows(write, new RegisterWrite(5, 13), new RegisterOutcome(RegisterOutcomeKind.Applied), afterAppliedMismatch).IsValid);
        Assert.False(spec.AllowsConcurrent(
            afterAppliedMismatch,
            [
                (read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 13)),
                (write, new RegisterWrite(5, 13), new RegisterOutcome(RegisterOutcomeKind.Applied)),
            ]).IsValid);
        var afterUnlearnedMismatch = Allow(spec, write, new RegisterWrite(5, 13), new RegisterOutcome(RegisterOutcomeKind.Mismatch), afterAppliedMismatch);
        Assert.True(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 5), afterUnlearnedMismatch).IsValid);
        Assert.False(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 13), afterUnlearnedMismatch).IsValid);

        // An unknown write dispatched with an unlearned version cannot land even after a later read learns 5's version.
        var afterUnlearnedUnknown = Allow(spec, write, new RegisterWrite(5, 13), RegisterOutcome.Unknown, afterAppliedMismatch);
        var afterLearned = Allow(spec, read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 5), afterUnlearnedUnknown);
        Assert.False(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 13), afterLearned).IsValid);

        // Once 5's version is learned, an unknown write may land later, and a mismatch must have applied.
        var afterLearnedUnknown = Allow(spec, write, new RegisterWrite(5, 14), RegisterOutcome.Unknown, afterLearned);
        Assert.True(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 14), afterLearnedUnknown).IsValid);

        var afterLearnedMismatch = Allow(spec, write, new RegisterWrite(5, 14), new RegisterOutcome(RegisterOutcomeKind.Mismatch), afterLearned);
        Assert.True(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 14), afterLearnedMismatch).IsValid);
        Assert.False(spec.Allows(read, RegisterRead.Instance, new RegisterOutcome(RegisterOutcomeKind.Present, 5), afterLearnedMismatch).IsValid);
    }

    /// <summary>
    /// Without <c>appliedMismatch</c>, a deterministic register cannot answer <see cref="RegisterOutcomeKind.Mismatch"/>
    /// to an applying create or conditional write.
    /// </summary>
    [Fact]
    public void DeterministicSpecRejectsAppliedMismatch()
    {
        var spec = ConditionalRegisterSpec.Create(unknownOutcomes: false, appliedMismatch: false);
        var write = spec.GetOperation(ConditionalRegisterSpec.Write);
        var initial = new StateProfile(new ConditionalRegisterState());

        Assert.False(spec.Allows(write, new RegisterWrite(null, 1), new RegisterOutcome(RegisterOutcomeKind.Mismatch), initial).IsValid);
        var afterCreate = Allow(spec, write, new RegisterWrite(null, 1), new RegisterOutcome(RegisterOutcomeKind.Applied), initial);
        Assert.False(spec.Allows(write, new RegisterWrite(1, 2), new RegisterOutcome(RegisterOutcomeKind.Mismatch), afterCreate).IsValid);
    }

    /// <summary>
    /// <see cref="ConditionalRegisterExecutor"/> enforces both directions of MS-I5 atomically: a version already learned
    /// for one value cannot be reused for a distinct value on a later write or read, a rejected learn leaves neither map
    /// mutated, and <see cref="ConditionalRegisterExecutor.Over"/> inherits that clean bijection.
    /// </summary>
    [Fact]
    public async Task ExecutorRejectsReusedVersionAcrossDistinctValues()
    {
        var reusedVersion = new MetaStoreVersion("v1");
        var freshVersion = new MetaStoreVersion("v2");
        var register = new FixedVersionRegister(reusedVersion);
        var executor = new ConditionalRegisterExecutor(register);

        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await executor.WriteAsync(new RegisterWrite(null, 1), CancellationToken.None));
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => executor.WriteAsync(new RegisterWrite(1, 2), CancellationToken.None));

        // The rejected write must not strand value 2 -> v1 in the executor or in clones created via Over.
        var freshReader = executor.Over(new FixedVersionRegister(freshVersion, ConditionalRegisterSpec.Payload(2)));
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Present, 2), await freshReader.ReadAsync(CancellationToken.None));

        var reusedReader = executor.Over(new FixedVersionRegister(reusedVersion, ConditionalRegisterSpec.Payload(3)));
        _ = await Assert.ThrowsAsync<InvalidDataException>(() => reusedReader.ReadAsync(CancellationToken.None));

        register.Version = freshVersion;
        Assert.Equal(new RegisterOutcome(RegisterOutcomeKind.Applied), await executor.WriteAsync(new RegisterWrite(1, 2), CancellationToken.None));
    }

    public void Dispose() => _leaks.Dispose();

    private static StateProfile Allow(
        Spec<ConditionalRegisterState> spec,
        IOperation operation,
        object request,
        RegisterOutcome response,
        StateProfile profile)
    {
        var (isValid, explanation, next) = spec.Allows(operation, request, response, profile);
        Assert.True(isValid, explanation);
        return next;
    }

    private sealed class FixedVersionRegister(MetaStoreVersion version, byte[]? initial = null) : IConditionalRegister
    {
        private byte[]? _value = initial;

        public MetaStoreVersion Version { get; set; } = version;

        public ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
        {
#pragma warning disable CA2000 // Ownership of the entry transfers to the caller.
            return ValueTask.FromResult(_value is null ? null : new MetaStoreEntry(Version, ArcBuffer.Wrap(_value)));
#pragma warning restore CA2000
        }

        public ValueTask<RegisterWriteOutcome> WriteAsync(
            MetaStoreVersion? expectedVersion,
            ReadOnlySequence<byte> value,
            CancellationToken cancellationToken = default)
        {
            _value = value.ToArray();
            return ValueTask.FromResult<RegisterWriteOutcome>(new RegisterWriteOutcome.Applied(Version));
        }
    }
}

/// <summary>
/// Runs the spec's requests against a fresh memory register. The executor learns each applied write's version from the
/// write's answer, so a later write names it without a read between them, and a later read must return it.
/// </summary>
internal sealed class MetaStoreModelClient
{
    private ConditionalRegisterExecutor _executor = new(new InMemoryConditionalRegister());

    public void Reset() => _executor = new ConditionalRegisterExecutor(new InMemoryConditionalRegister());

    public RegisterOutcome Write(RegisterWrite request) =>
        _executor.WriteAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    public RegisterOutcome Read() => _executor.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
}
