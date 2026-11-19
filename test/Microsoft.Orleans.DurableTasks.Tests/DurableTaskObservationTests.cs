using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Diagnostics;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

public sealed partial class DurableTaskRuntimeInvariantTests
{
    [Fact]
    public Task DelayWaitActivityCorrelatesWithPersistedResumeAndStopsAfterCompletion() => ActivationScheduler.RunAsync(async () =>
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new ObservationObserver();
        using var subscription = source.Subscribe(observer);
        var (runtime, storage, manager, transport) = CreateRuntime(observations: source);
        var id = TaskId.Parse("observed-wait");
        runtime.ScheduleFromInbox(id, CreateRequest(1,
            () => DurableTask.Run(async _ => await DurableTask.Delay(TimeSpan.FromMinutes(1)))));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await transport.ResumeSent.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var before = await observer.ReadThroughAsync("DelayScheduled");
        var resume = Assert.Single(transport.Resumes);
        var delay = Assert.Single(before, item => item.Transition == "DelayScheduled");
        Assert.True(delay.IsCommitted);
        Assert.Equal(resume.DueTime, delay.DueTime);
        Assert.Equal(storage.Get(resume.TaskId).DueTime, delay.DueTime);
        Assert.Equal(DurableTaskMessageTransport.CreateResumeMessageId(
            runtime.GrainId, resume.TaskId, resume.Incarnation, resume.Generation, resume.DueTime).ToString(), delay.MessageId);
        var started = Assert.Single(before, item => item.Transition == "AttemptStarted" && item.TaskId == id.ToString());
        var waiting = Assert.Single(before, item => item.Transition == "WaitStarted" && item.TaskId == id.ToString());
        Assert.Equal(id.ToString(), waiting.TaskId);
        Assert.Equal(resume.TaskId.ToString(), waiting.RelatedTaskId);
        Assert.Equal(started.AttemptId, waiting.AttemptId);
        Assert.False(waiting.IsCommitted);
        Assert.Null(waiting.WriteVersion);

        await HandleAsync(CreateHandler(runtime), ResumeContext(runtime, resume.TaskId, resume.Generation, resume.DueTime));
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var after = await observer.ReadThroughAsync("AttemptStopped", id.ToString());
        var ended = Assert.Single(after, item => item.Transition == "WaitEnded" && item.TaskId == id.ToString());
        Assert.Equal(waiting.TaskId, ended.TaskId);
        Assert.Equal(waiting.RelatedTaskId, ended.RelatedTaskId);
        Assert.Equal(waiting.AttemptId, ended.AttemptId);
        Assert.False(ended.IsCommitted);
        var stopped = after[^1];
        Assert.Equal(started.AttemptId, stopped.AttemptId);
        Assert.False(stopped.IsCommitted);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, storage.Get(id).Result!.Status);
    });

    [Fact]
    public Task RecoveredRemoteCompletionKeepsWireIdentityAndLinksAppliedMessage() => ActivationScheduler.RunAsync(async () =>
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new ObservationObserver();
        using var subscription = source.Subscribe(observer);
        var (runtime, storage, manager, _) = CreateRuntime(observations: source);
        var root = TaskId.Parse("root");
        var child = root.Child("remote");
        var incarnation = storage.AddInvoked(root).Incarnation;
        var request = CreateRemoteRequest(7);
        await runtime.ScheduleChildAsync(child, new TestStateManager.TestRemoteDurableTask(request), TestContext.Current.CancellationToken);
        var sent = Assert.Single(await observer.DrainAsync(source), item => item.Transition == "InvocationSent");
        var wireId = runtime.GetWireId(child);
        Assert.Equal(wireId.ToString(), sent.WireTaskId);
        Assert.Equal(incarnation, sent.Incarnation);
        Assert.False(string.IsNullOrEmpty(sent.MessageId));

        var (recovered, _, _, _) = CreateRuntime(storage, manager, observations: source);
        await recovered.ResumePendingTasksAsync(TestContext.Current.CancellationToken);
        var recoveredChild = Assert.Single(await observer.DrainAsync(source), item => item.TaskId == child.ToString());
        Assert.Equal("Recovered", recoveredChild.Transition);
        Assert.False(recoveredChild.IsCommitted);
        Assert.Equal(sent.WireTaskId, recoveredChild.WireTaskId);
        Assert.Equal(incarnation, recoveredChild.Incarnation);
        Assert.NotEqual(sent.Epoch, recoveredChild.Epoch);
        var completion = CompletionMessage(request.Context!.TargetId, recovered.GrainId, wireId, DurableTaskResponse.FromResult(42));
        Accept(CreateHandler(recovered), completion);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var applied = Assert.Single(await observer.DrainAsync(source), item => item.Transition == "MessageApplied");
        Assert.True(applied.IsCommitted);
        Assert.Equal(child.ToString(), applied.TaskId);
        Assert.Equal(wireId.ToString(), applied.WireTaskId);
        Assert.Equal(completion.Envelope.MessageId.ToString(), applied.MessageId);
        Assert.Equal(request.Context.TargetId.ToString(), applied.RelatedGrainId);
        Assert.Equal(42, storage.Get(child).Result!.GetResult<int>());
    });

    [Fact]
    public Task CommittedObservationsWaitForAcknowledgementAndExcludeLaterStaging() => ActivationScheduler.RunAsync(async () =>
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new ObservationObserver();
        using var subscription = source.Subscribe(observer);
        var (runtime, storage, manager, _) = CreateRuntime(observations: source);
        var root = TaskId.Parse("root");
        var rootState = storage.AddInvoked(root);
        var incarnation = rootState.Incarnation;
        var firstId = root.Child("first");
        var secondId = root.Child("second");
        var api = (IDurableTaskGrainRuntime)runtime;
        Assert.Equal(17, api.RunEffect(firstId, 17, value => DurableTaskResponse.FromResult(value)).GetResult<int>());
        manager.AfterWriteStarted = async () =>
        {
            // This effect is staged while the first write is captured, but is not part of that write.
            api.RunEffect(secondId, 29, value => DurableTaskResponse.FromResult(value));
            Assert.Empty(await observer.DrainAsync(source));
        };
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        manager.AfterWriteStarted = null;
        var first = await observer.DrainAsync(source);
        Assert.Equal(["Created", "Outcome"], first.Select(item => item.Transition));
        Assert.All(first, item =>
        {
            Assert.True(item.IsCommitted);
            Assert.Equal(firstId.ToString(), item.TaskId);
            Assert.Equal(incarnation, item.Incarnation);
            Assert.Equal(runtime.GetWireId(firstId).ToString(), item.WireTaskId);
            Assert.Equal(1, item.WriteVersion);
        });
        Assert.Equal("CompletedSuccessfully", first[1].Detail);
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        var second = await observer.DrainAsync(source);
        Assert.Equal(["Created", "Outcome"], second.Select(item => item.Transition));
        Assert.All(second, item =>
        {
            Assert.Equal(secondId.ToString(), item.TaskId);
            Assert.Equal(2, item.WriteVersion);
        });
        Assert.Equal([1L, 2L, 3L, 4L], first.Concat(second).Select(item => item.Sequence));
        Assert.Equal(17, storage.Get(firstId).Result!.GetResult<int>());
        Assert.Equal(29, storage.Get(secondId).Result!.GetResult<int>());
    });

    [Fact]
    public Task FailedTaskWriteEmitsNoCommittedObservation() => ActivationScheduler.RunAsync(async () =>
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new ObservationObserver();
        using var subscription = source.Subscribe(observer);
        var (runtime, _, manager, _) = CreateRuntime(observations: source);
        var request = CreateRequest(1);
        runtime.ScheduleFromInbox(TaskId.Parse("failed-observation"), request);
        var failure = new IOException("Storage did not acknowledge.");
        manager.AfterWriteStarted = () => throw failure;

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(
            () => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Equal(0, manager.WriteCount);
        Assert.Equal(0, request.CreateTaskCallCount);
        Assert.Empty(await observer.DrainAsync(source));
    });

    [Fact]
    public Task TaskAttemptActivityFollowsCommittedSchedule() => ActivationScheduler.RunAsync(async () =>
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var observer = new ObservationObserver();
        using var subscription = source.Subscribe(observer);
        var (runtime, _, manager, _) = CreateRuntime(observations: source);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = CreateRequest(1, () => DurableTask.Run(_ => invoked.TrySetResult()));
        var id = TaskId.Parse("observed-attempt");
        runtime.ScheduleFromInbox(id, request);
        Assert.Empty(await observer.DrainAsync(source));

        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var events = await observer.DrainAsync(source);
        var scheduled = Assert.Single(events, item => item.Transition == "Scheduled");
        var started = Assert.Single(events, item => item.Transition == "AttemptStarted");
        Assert.True(scheduled.IsCommitted);
        Assert.False(started.IsCommitted);
        Assert.Null(started.WriteVersion);
        Assert.Equal(id.ToString(), started.TaskId);
        Assert.Equal(scheduled.WireTaskId, started.WireTaskId);
        Assert.True(scheduled.Sequence < started.Sequence);
        Assert.NotNull(started.AttemptId);
    });

    [Fact]
    public void DisabledTaskFeedDoesNotReadStateToConstructObservationPayloads()
    {
        using var source = new DurableObservationSource(NullLogger<DurableObservationSource>.Instance);
        var (runtime, storage, _, _) = CreateRuntime(observations: source);
        var result = ((IDurableTaskGrainRuntime)runtime).RunEffect(
            TaskId.Parse("disabled/effect"), 23, value => DurableTaskResponse.FromResult(value));

        Assert.Equal(23, result.GetResult<int>());
        // RunEffect's duplicate check is the only lookup. Building Created/Outcome would look up this record and parent.
        Assert.Equal(1, storage.TryGetTaskCount);
        Assert.Equal(0, storage.TasksEnumerationCount);
        Assert.False(source.IsEnabled);
    }

    [Fact]
    public void StagedTaskDiagnosticSubscriberStillThrowsSynchronouslyBeforeCommit()
    {
        var (runtime, _, manager, _) = CreateRuntime();
        var id = TaskId.Parse("throwing-staged-hook");
        var failure = new InvalidOperationException("Staged listener failure.");
        using var subscription = DurableTaskEvents.AllEvents.Subscribe(new StagedObserver(value =>
        {
            if (value is DurableTaskEvents.TaskCreated && value.TaskId == id) throw failure;
        }));

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => runtime.ScheduleFromInbox(id, CreateRequest(1))));
        Assert.Equal(0, manager.WriteCount);
    }

    private sealed class ObservationObserver : IObserver<DurableObservation>
    {
        private readonly Channel<DurableObservation> _items = Channel.CreateUnbounded<DurableObservation>();
        public void OnNext(DurableObservation value) => _items.Writer.TryWrite(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public async Task<List<DurableObservation>> ReadThroughAsync(string transition, string? taskId = null)
        {
            var result = new List<DurableObservation>();
            do
            {
                result.Add(await _items.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            } while (result[^1].Transition != transition || (taskId is not null && result[^1].TaskId != taskId));
            return result;
        }
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

    private sealed class StagedObserver(Action<DurableTaskEvents.DurableTaskEvent> callback) : IObserver<DurableTaskEvents.DurableTaskEvent>
    {
        public void OnNext(DurableTaskEvents.DurableTaskEvent value) => callback(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
