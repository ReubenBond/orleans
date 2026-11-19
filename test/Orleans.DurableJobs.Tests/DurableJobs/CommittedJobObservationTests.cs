using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Diagnostics;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

public partial class JournaledJobShardManagerTests
{
    [Fact]
    public async Task JobObservationsFollowAcknowledgedWritesAndKeepScheduleMetadataAcrossRetry()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new JobObservationObserver();
        using var subscription = source.Subscribe(observer);
        await using var test = await SignalTest.CreateAsync(observations: source);
        var write = test.GateNextAppend();
        var scheduling = test.Schedule(0);
        await write.Started.Task.WaitAsync(test.Token);
        var following = test.Schedule(1);
        Assert.Empty(await observer.DrainAsync(source));
        Assert.Empty(await test.ReadPersistedJobNamesAsync());
        var nextWrite = test.GateNextAppend();
        write.Release.SetResult();
        var job = Assert.IsType<DurableJob>(await scheduling.WaitAsync(test.Token));
        await nextWrite.Started.Task.WaitAsync(test.Token);
        var scheduled = Assert.Single(await observer.DrainAsync(source));
        Assert.Equal("Scheduled", scheduled.Transition);
        Assert.Equal(job.Id, scheduled.JobId);
        Assert.Equal(job.DueTime, scheduled.DueTime);
        Assert.Equal($"{test.Shard.Provider.Name}:{test.Shard.StorageId}", scheduled.Owner);
        Assert.True(scheduled.IsCommitted);
        Assert.Equal(1, scheduled.WriteVersion);
        Assert.Equal(["job-0"], await test.ReadPersistedJobNamesAsync());
        nextWrite.Release.SetResult();
        var second = Assert.IsType<DurableJob>(await following.WaitAsync(test.Token));
        var secondObservation = Assert.Single(await observer.DrainAsync(source));
        Assert.Equal(second.Id, secondObservation.JobId);
        Assert.Equal(2, secondObservation.WriteVersion);
        Assert.Equal(2, secondObservation.Sequence);

        var context = new JobRunContext(job, "retry", 2);
        var retryDue = job.DueTime.AddHours(2);
        Assert.Equal(DurableJobMutationResult.Applied, await test.Shard.RetryJobLaterAsync(context, retryDue, test.Token));
        var retry = Assert.Single(await observer.DrainAsync(source));
        Assert.Equal("RetryScheduled", retry.Transition);
        Assert.Equal(scheduled.JobId, retry.JobId);
        Assert.Equal(retryDue, retry.DueTime);
        Assert.NotEqual(retryDue, scheduled.DueTime);
        Assert.Equal(3, retry.WriteVersion);
        Assert.Equal(DurableJobMutationResult.Applied, await test.Shard.RemoveJobAsync(job.Id, test.Token));
        var retired = Assert.Single(await observer.DrainAsync(source));
        Assert.Equal("Retired", retired.Transition);
        Assert.Equal(job.Id, retired.JobId);
        Assert.Null(retired.DueTime);
        Assert.Equal(4, retired.WriteVersion);
        Assert.Equal(["job-1"], await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public async Task FailedJobWriteEmitsNoCommittedObservationOrPersistedJob()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new JobObservationObserver();
        using var subscription = source.Subscribe(observer);
        await using var test = await SignalTest.CreateAsync(observations: source);
        var write = test.GateNextAppend();
        var scheduling = test.Schedule(0);
        await write.Started.Task.WaitAsync(test.Token);
        var failure = new IOException("Job storage failed before acknowledgement.");
        write.Release.SetException(failure);
        await Assert.ThrowsAsync<IOException>(() => scheduling.WaitAsync(test.Token));

        Assert.Empty(await observer.DrainAsync(source));
        Assert.Empty(await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public void DisabledJobFeedDoesNotReadObservationClockOrBuildPayload()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var clock = new ObservationClock();
        var codec = new ObservationCodec();
        var state = new JournaledJobShardState(new("disabled"), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), codec, clock);
        state.ConfigureObservations(source, "provider:disabled");
        var request = new ScheduleJobRequest
        {
            Target = GrainId.Create("test", "target"),
            JobName = "disabled",
            DueTime = DateTimeOffset.UnixEpoch.AddHours(1)
        };
        Assert.NotNull(state.TryScheduleJob(request));
        Assert.Equal(0, clock.Reads);
        var observer = new JobObservationObserver();
        using var subscription = source.Subscribe(observer);
        Assert.NotNull(state.TryScheduleJob(request));
        Assert.Equal(1, clock.Reads);
        Assert.Equal(2, codec.Writes);
    }

    private sealed class ObservationCodec : IDurableValueCommandCodec<DurableJobShardJournalRecord>
    {
        public int Writes { get; private set; }
        public void WriteSet(DurableJobShardJournalRecord value, JournalStreamWriter writer) => Writes++;
        public void Apply(JournalBufferReader input, IDurableValueCommandHandler<DurableJobShardJournalRecord> consumer) =>
            throw new NotSupportedException();
    }

    private sealed class ObservationClock : FakeTimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return DateTimeOffset.UnixEpoch;
        }
    }

    private sealed class JobObservationObserver : IObserver<DurableObservation>
    {
        private readonly Channel<DurableObservation> _items = Channel.CreateUnbounded<DurableObservation>();
        public void OnNext(DurableObservation value) => _items.Writer.TryWrite(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public async Task<List<DurableObservation>> DrainAsync(DurableObservationSource source)
        {
            var marker = new DurableObservation { Owner = "test", Component = "barrier", Transition = "barrier" };
            source.Publish(marker);
            var result = new List<DurableObservation>();
            while (true)
            {
                var item = await _items.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (ReferenceEquals(marker, item)) return result;
                result.Add(item);
            }
        }
    }
}
