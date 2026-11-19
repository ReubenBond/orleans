using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Diagnostics;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT"), TestProvider("None"), TestArea("Journaling")]
public sealed class DurableObservationTests
{
    [Fact]
    public async Task CaptureAcknowledgesOnlyItsPrefixAndResetDiscardsUnacknowledgedObservations()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new Observer();
        using var subscription = source.Subscribe(observer);
        var buffer = new CommittedObservationBuffer(source);
        var first = Observation("scheduled") with { DueTime = DateTimeOffset.UnixEpoch };
        buffer.Stage(first);
        buffer.Capture();
        buffer.Stage(first with { Transition = "retry", DueTime = DateTimeOffset.UnixEpoch.AddHours(1) });
        Assert.False(observer.Items.Reader.TryRead(out _));

        buffer.Acknowledge();
        var scheduled = await observer.ReadAsync();
        Assert.Equal("scheduled", scheduled.Transition);
        Assert.Equal(DateTimeOffset.UnixEpoch, scheduled.DueTime);
        Assert.True(scheduled.IsCommitted);
        Assert.Equal(1, scheduled.Sequence);
        Assert.Equal(1, scheduled.WriteVersion);
        Assert.False(observer.Items.Reader.TryRead(out _));

        buffer.Capture();
        buffer.Acknowledge();
        var retry = await observer.ReadAsync();
        Assert.Equal("retry", retry.Transition);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), retry.DueTime);
        Assert.Equal(scheduled.Epoch, retry.Epoch);
        Assert.Equal(2, retry.Sequence);
        Assert.Equal(2, retry.WriteVersion);

        buffer.Stage(Observation("failed"));
        buffer.Capture();
        buffer.Reset();
        buffer.Stage(Observation("recovered"));
        buffer.Capture();
        buffer.Acknowledge();
        var recovered = await observer.ReadAsync();
        Assert.Equal("recovered", recovered.Transition);
        Assert.NotEqual(scheduled.Epoch, recovered.Epoch);
        Assert.Equal(1, recovered.Sequence);
        Assert.Equal(1, recovered.WriteVersion);
    }

    [Fact]
    public async Task SlowAndThrowingSubscribersDoNotBlockPublisherOrOtherSubscribers()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var throwing = new Observer(throwAfterRecording: true);
        var healthy = new Observer();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var slowSubscription = source.Subscribe(new CallbackObserver(_ =>
        {
            entered.TrySetResult();
            release.Wait(cancellationToken);
        }), capacity: 1);
        using var throwingSubscription = source.Subscribe(throwing);
        using var healthySubscription = source.Subscribe(healthy);
        try
        {
            source.Publish(Observation("first"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            source.Publish(Observation("second"));
            source.Publish(Observation("third"));

            Assert.Equal(["first", "second", "third"], new[]
            {
                (await healthy.ReadAsync()).Transition,
                (await healthy.ReadAsync()).Transition,
                (await healthy.ReadAsync()).Transition
            });
            Assert.Equal(["first", "second", "third"], new[]
            {
                (await throwing.ReadAsync()).Transition,
                (await throwing.ReadAsync()).Transition,
                (await throwing.ReadAsync()).Transition
            });
            Assert.Equal(1, source.DroppedCount);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ProducerOverflowIsCountedAtAcknowledgementAndLeavesSequenceGap()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new Observer();
        using var subscription = source.Subscribe(observer, capacity: 1024);
        var buffer = new CommittedObservationBuffer(source);
        for (var i = 0; i <= DurableObservationSource.DefaultCapacity; ++i)
        {
            buffer.Stage(Observation(i.ToString()));
        }

        buffer.Capture();
        Assert.Equal(0, source.DroppedCount);
        buffer.Acknowledge();
        Assert.Equal(1, source.DroppedCount);
        for (var i = 0; i < DurableObservationSource.DefaultCapacity; ++i)
        {
            var item = await observer.ReadAsync();
            Assert.Equal(i.ToString(), item.Transition);
            Assert.Equal(i + 1, item.Sequence);
            Assert.Equal(1, item.WriteVersion);
        }

        buffer.Stage(Observation("after-overflow"));
        buffer.Capture();
        buffer.Acknowledge();
        var next = await observer.ReadAsync();
        Assert.Equal("after-overflow", next.Transition);
        Assert.Equal(DurableObservationSource.DefaultCapacity + 2, next.Sequence);
        Assert.Equal(2, next.WriteVersion);
        Assert.Equal(1, source.DroppedCount);
    }

    private static DurableObservation Observation(string transition) =>
        new() { Owner = "owner", Component = "test", Transition = transition };

    private sealed class Observer(bool throwAfterRecording = false) : IObserver<DurableObservation>
    {
        public Channel<DurableObservation> Items { get; } = Channel.CreateUnbounded<DurableObservation>();
        public Task<DurableObservation> ReadAsync() =>
            Items.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void OnNext(DurableObservation value)
        {
            Items.Writer.TryWrite(value);
            if (throwAfterRecording) throw new InvalidOperationException("Exporter failed.");
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class CallbackObserver(Action<DurableObservation> callback) : IObserver<DurableObservation>
    {
        public void OnNext(DurableObservation value) => callback(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
