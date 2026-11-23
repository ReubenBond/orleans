using Orleans.Consensus.CasPaxos.Messages;
using Orleans.Consensus.CasPaxos.Model;
using Orleans.Consensus.CasPaxos.Testing;
using Xunit;

namespace Orleans.Consensus.CasPaxos.Sqlite.Tests;

/// <summary>
/// A voter's identity binding and the admissions it grants share its acceptor database's fate: a fresh database binds
/// once, a bound one serves only its voter, acceptor state without a binding is refused, and every record is durable
/// across reopening.
/// </summary>
public sealed class SqliteVoterRegistryTests
{
    [Fact]
    public async Task AFreshDatabaseBindsOnceAndKeepsItsIncarnationAndAdmissionAcrossReopening()
    {
        using var database = new TestDatabase();
        Guid incarnation;
        await using (var server = await database.OpenAsync())
        {
            var registry = await SqliteVoterRegistry.OpenAsync(server, "a", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("a", registry.VoterId);
            Assert.False(registry.IsAdmitted);
            incarnation = registry.Incarnation;
            await registry.MarkAdmittedAsync(TestContext.Current.CancellationToken);
            Assert.True(registry.IsAdmitted);
        }

        await using var reopened = await database.OpenAsync();
        var restored = await SqliteVoterRegistry.OpenAsync(reopened, "a", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(incarnation, restored.Incarnation);
        Assert.True(restored.IsAdmitted);
    }

    [Fact]
    public async Task ABoundDatabaseRefusesAnotherVoter()
    {
        using var database = new TestDatabase();
        await using var server = await database.OpenAsync();
        _ = await SqliteVoterRegistry.OpenAsync(server, "a", cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => SqliteVoterRegistry.OpenAsync(server, "b", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("bound to voter 'a'", failure.Message, StringComparison.Ordinal);

        // The refusal is an answer, not a storage failure: the acceptor still serves.
        Assert.Null(await server.InspectAsync("key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptorStateWithoutABindingIsRefused()
    {
        using var database = new TestDatabase();
        await using var server = await database.OpenAsync();
        _ = await server.PrepareAsync("key", new PrepareRequest(new Ballot(1, 1)), TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => SqliteVoterRegistry.OpenAsync(server, "a", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("without a voter binding", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A voter admits the first incarnation it is asked to admit for another voter, durably, and answers it ever after.</summary>
    [Fact]
    public async Task AdmissionsRecordTheFirstIncarnationDurably()
    {
        using var database = new TestDatabase();
        var first = Guid.NewGuid();
        await using (var server = await database.OpenAsync())
        {
            var registry = await SqliteVoterRegistry.OpenAsync(server, "a", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(first, await registry.AdmitAsync("b", first, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(first, await registry.AdmitAsync("b", first, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(first, await registry.AdmitAsync("b", Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken));
            Assert.Throws<ArgumentException>(() => registry.AdmitAsync("a", Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var reopened = await database.OpenAsync();
        var restored = await SqliteVoterRegistry.OpenAsync(reopened, "a", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(first, await restored.AdmitAsync("b", Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Admissions run in the acceptor's own transactions, beside its protocol requests, under load.</summary>
    [Fact]
    public async Task AdmissionsShareTheAcceptorsWriterUnderLoad()
    {
        using var database = new TestDatabase();
        await using var server = await database.OpenAsync();
        var registry = await SqliteVoterRegistry.OpenAsync(server, "a", cancellationToken: TestContext.Current.CancellationToken);
        var incarnations = Enumerable.Range(0, 32).Select(static _ => Guid.NewGuid()).ToArray();
        var prepares = Enumerable.Range(0, 256).Select(index => server.PrepareAsync(
            index.ToString(System.Globalization.CultureInfo.InvariantCulture), new PrepareRequest(new Ballot(1, 1)), TestContext.Current.CancellationToken).AsTask());
        var admissions = incarnations.Select((incarnation, index) => registry.AdmitAsync(
            "v" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), incarnation, cancellationToken: TestContext.Current.CancellationToken).AsTask());

        await Task.WhenAll(prepares);
        Assert.Equal(incarnations, await Task.WhenAll(admissions));
    }
    /// <summary>
    /// A transaction which fails once the acceptor serves is reported to the fatal-error handler, where the host stops
    /// the process, and fails its requests; a failed open reports nothing, since its caller sees the failure.
    /// </summary>
    [Fact]
    public async Task AServingWritersFailedTransactionIsReportedAsAStorageFailure()
    {
        using var database = new TestDatabase();
        var fatalErrors = new RecordingFatalErrorHandler();
        var server = await SqliteAcceptorServer.OpenAsync(database.Path, fatalErrors, cancellationToken: TestContext.Current.CancellationToken).AsTask().WaitAsync(TestDatabase.Timeout, TestContext.Current.CancellationToken);
        var failure = new IOException("injected commit failure");
        server.BeforeCommit = (_, _, _) => throw failure;

        var thrown = await Assert.ThrowsAsync<IOException>(() => server.PrepareAsync("key", new PrepareRequest(new Ballot(1, 1)), TestContext.Current.CancellationToken).AsTask());
        Assert.Same(failure, thrown);
        var reported = Assert.Single(fatalErrors.Failures);
        Assert.Equal("transaction", reported.Operation);
        Assert.Equal(System.IO.Path.GetFullPath(database.Path), reported.Path);
        Assert.Same(failure, reported.Exception);
        await Assert.ThrowsAsync<IOException>(() => server.DisposeAsync().AsTask());

        using (var connection = database.Connect())
        {
            TestDatabase.Execute(connection, "PRAGMA application_id = 7;");
        }

        var refusing = new RecordingFatalErrorHandler();
        await Assert.ThrowsAsync<InvalidDataException>(() => SqliteAcceptorServer.OpenAsync(database.Path, refusing, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(refusing.Failures);
    }
}
