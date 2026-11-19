using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Diagnostics;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT"), TestProvider("None"), TestArea("Messaging")]
public sealed class CommittedObservationTests() : MessagingBehaviorTestBase(receiverOnly: false)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OwnerPublishesOnlyAfterStorageAcknowledges(bool outbox, bool snapshot)
    {
        var owner = NewGrain();
        var receiver = outbox ? NewGrain() : owner;
        _ = await owner.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(owner);
        var source = context.ActivationServices.GetRequiredService<DurableObservationSource>();
        var observer = new Observer();
        using var subscription = source.Subscribe(observer);
        const string route = "messages/observation";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(1, "observed"), route);
        var id = outbox
            ? await owner.StageWithoutCommitAsync(receiver.GetGrainId(), route, NewMessage(1, "observed"))
            : envelope.Envelope.MessageId;
        var journal = JournalId.FromGrainId(owner.GetGrainId());
        if (snapshot) Fixture.Storage.RequestSnapshot(journal);
        using var acknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        Task writing = outbox ? owner.RetryWriteStateAsync() : DeliverAsync(owner, envelope);
        await acknowledgement.WaitUntilEnteredAsync();
        Assert.False(writing.IsCompleted);
        Assert.Empty(await observer.DrainOwnerAsync(source, owner.GetGrainId().ToString()));

        acknowledgement.Release();
        await writing;
        var observations = await observer.DrainOwnerAsync(source, owner.GetGrainId().ToString());
        var observation = Assert.Single(observations, item => item.Transition == (outbox ? "Enqueued" : "Accepted"));
        Assert.Equal(outbox ? "outbox" : "inbox", observation.Component);
        Assert.Equal(id.ToString(), observation.MessageId);
        Assert.True(observation.IsCommitted);
        Assert.Equal(1, observation.WriteVersion);
        if (outbox) Assert.Equal(receiver.GetGrainId().ToString(), observation.RelatedGrainId);
        handler.Release();
        Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementEmitsNothingButRecoveryRetainsAuthoritativeMessage(bool outbox)
    {
        var owner = NewGrain();
        var receiver = outbox ? NewGrain() : owner;
        _ = await owner.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(owner);
        var source = context.ActivationServices.GetRequiredService<DurableObservationSource>();
        var observer = new Observer();
        using var subscription = source.Subscribe(observer);
        const string route = "messages/observation-recovery";
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        var envelope = CreateEnvelope(receiver, NewMessage(2, "recovered"), route);
        var id = outbox
            ? await owner.StageWithoutCommitAsync(receiver.GetGrainId(), route, NewMessage(2, "recovered"))
            : envelope.Envelope.MessageId;
        var journal = JournalId.FromGrainId(owner.GetGrainId());
        var writesBefore = Fixture.Storage.GetSuccessfulWriteCount(journal);
        using var acknowledgement = Fixture.Storage.BlockAcknowledgement(journal);
        Task writing = outbox ? owner.RetryWriteStateAsync() : DeliverAsync(owner, envelope);
        await acknowledgement.WaitUntilEnteredAsync();
        Assert.Equal(writesBefore + 1, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.Empty(await observer.DrainOwnerAsync(source, owner.GetGrainId().ToString()));
        acknowledgement.Fail();
        await Assert.ThrowsAsync<IOException>(() => writing);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(await observer.DrainOwnerAsync(source, owner.GetGrainId().ToString()));

        _ = await owner.GetSnapshotAsync();
        var recoveredContext = Fixture.GetGrainContext(owner);
        var recovered = Assert.IsType<MessagingTestGrain>(recoveredContext.GrainInstance);
        Assert.NotSame(context, recoveredContext);
        var replay = Assert.IsType<EndpointSnapshot>(recovered.ReplayedSnapshot);
        Assert.Equal(1, outbox ? replay.OutboxCount : replay.InboxCount);
        handler.Release();
        Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects);
        if (outbox) await Fixture.WaitForOutboxCountAsync(owner, 0);
        var observations = await observer.DrainOwnerAsync(source, owner.GetGrainId().ToString());
        Assert.DoesNotContain(observations, item => item.Transition == (outbox ? "Enqueued" : "Accepted"));
        var completed = Assert.Single(observations, item => item.Transition == (outbox ? "Retired" : "Completed"));
        Assert.Equal(id.ToString(), completed.MessageId);
        Assert.True(completed.IsCommitted);
    }

    internal sealed class Observer : IObserver<DurableObservation>
    {
        private readonly Channel<DurableObservation> _items = Channel.CreateUnbounded<DurableObservation>();
        public void OnNext(DurableObservation value) => _items.Writer.TryWrite(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public async Task<List<DurableObservation>> DrainOwnerAsync(DurableObservationSource source, string owner)
        {
            var marker = new DurableObservation { Owner = "test", Component = "barrier", Transition = "barrier" };
            source.Publish(marker);
            var result = new List<DurableObservation>();
            while (true)
            {
                var item = await _items.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                if (ReferenceEquals(marker, item)) return result;
                if (item.Owner == owner) result.Add(item);
            }
        }
    }
}
