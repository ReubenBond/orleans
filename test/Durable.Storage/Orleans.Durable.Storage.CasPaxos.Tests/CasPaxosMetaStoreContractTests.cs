using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using Contracts = Orleans.Durable.Storage.Testing.ConditionalRegisterContract;

namespace Orleans.Durable.Storage.CasPaxos.Tests;

public sealed class CasPaxosMetaStoreContractTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    [Fact]
    public Task EmptyStoreReturnsNull() => AssertContractAsync(Contracts.EmptyStoreReturnsNull);

    [Fact]
    public Task NullVersionCreatesOnlyWhenAbsent() => AssertContractAsync(Contracts.NullVersionCreatesOnlyWhenAbsent);

    [Fact]
    public Task EmptyValueReadsAsPresent() => AssertContractAsync(Contracts.EmptyValueReadsAsPresent);

    [Fact]
    public Task NonNullVersionDoesNotCreateMissingRegister() => AssertContractAsync(Contracts.NonNullVersionDoesNotCreateMissingRegister);

    [Fact]
    public Task MatchingVersionUpdatesAndChangesVersion() => AssertContractAsync(Contracts.MatchingVersionUpdatesAndChangesVersion);

    [Fact]
    public Task AnAppliedWriteReturnsTheRegistersNewVersion() => AssertContractAsync(Contracts.AnAppliedWriteReturnsTheRegistersNewVersion);

    [Fact]
    public Task StaleVersionDoesNotOverwriteValue() => AssertContractAsync(Contracts.StaleVersionDoesNotOverwriteValue);

    [Fact]
    public Task ValuesAreCopiedOnWriteAndEntriesKeepTheirValues() => AssertContractAsync(Contracts.ValuesAreCopiedOnWriteAndEntriesKeepTheirValues);

    [Fact]
    public Task ConcurrentCreatesHaveOneWinner() => AssertContractAsync(Contracts.ConcurrentCreatesHaveOneWinner);

    [Fact]
    public Task ConcurrentWritesWithOneVersionHaveOneWinner() => AssertContractAsync(Contracts.ConcurrentWritesWithOneVersionHaveOneWinner);

    [Fact]
    public Task CancellationIsObserved() => AssertContractAsync(Contracts.CancellationIsObserved);

    public void Dispose() => _leaks.Dispose();

    private static Task AssertContractAsync(Func<IConditionalRegister, Task> contract) =>
        CasPaxosTestFixture.RunAsync(810, async simulation =>
        {
            using var factory = new CasPaxosMetaStoreFactory(simulation.CreateClient());
            var register = await factory.GetRegisterAsync(CasPaxosTestFixture.Cluster, "contracts", TestContext.Current.CancellationToken);
            await contract(register);
        });
}
