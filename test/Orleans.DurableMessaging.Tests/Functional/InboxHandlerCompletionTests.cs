using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.Serialization.Session;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxHandlerCompletionTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task DeadLetter_StagesReasonPayloadAndDedupeBeforeReturnAndPersistsAfterAcknowledgement()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var calls = 0;
        handler.Body = (self, _) =>
        {
            calls++;
            self.Context.Fail("unsupported input");
            AssertCompletedState(rig, self.Context.Envelope);
            var retained = Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters);
            Assert.Equal("unsupported input", retained.Reason);
            Assert.Equal(1, retained.AttemptCount);
            Assert.Equal(self.Context.Envelope.MessageId, retained.MessageId);
            Assert.Equal(new DurableTestMessage(self.Context.Envelope.MessageId, 501, "async-handler"),
                Assert.IsType<DurableTestMessage>(TestApplicationProtocol.Read(
                    rig.Context.ActivationServices.GetRequiredService<SerializerSessionPool>(), self.Context.Envelope).Body));
            self.Context.Fail("unsupported input");
            self.Context.Complete();
            Assert.Equal(retained, Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters));
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        Assert.False(finished.IsCompleted);
        AssertCompletedState(rig, input);
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Outbox);
        storage.Release();
        await WaitAsync(finished);
        Assert.Equal(1, calls);
        Assert.Equal(1, Fixture.Metrics.GetCount("orleans-durable-messaging-inbox-messages-processed", "dead_lettered"));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
        Assert.Equal(1, calls);
        await AssertHealthyAsync(rig);
        await DeactivateAsync(rig);
        var recovered = await rig.Receiver.GetSnapshotAsync();
        var letter = Assert.Single(recovered.InboxDeadLetters);
        Assert.Equal("unsupported input", letter.Reason);
        Assert.Equal(1, letter.AttemptCount);
        Assert.Equal(input.MessageId, letter.MessageId);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
        var recoveredContext = Fixture.GetGrainContext(rig.Receiver);
        await OnTurnAsync(recoveredContext, async () =>
        {
            var diagnostics = recoveredContext.ActivationServices.GetRequiredService<IDurableMessagingDiagnostics>();
            var retained = Assert.Single(diagnostics.InboxDeadLetters);
            Assert.Equal(input.Payload, retained.Message.Payload);
            Assert.Equal(input.Subject, retained.Message.Subject);
            Assert.Equal(input.SenderId, retained.Message.SenderId);
            Assert.Equal(input.ReceiverId, retained.Message.ReceiverId);
            Assert.True(diagnostics.RemoveInboxDeadLetter(input.MessageId));
            await recoveredContext.ActivationServices.GetRequiredService<IJournaledStateManager>().WriteStateAsync();
            Assert.Empty(diagnostics.InboxDeadLetters);
        });
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
    }

    [Collection(DurableMessagingClusterCollection.Name)]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("DurableMessaging")]
    public sealed class InboxPermanentFailureTests() : DurableMessagingBehaviorTestBase(new PermanentFailureClusterFixture())
    {
        [Fact]
        public async Task DeadLetter_AfterPreparationRetryTerminatesBeforeLimitAndCountsCurrentAttempt()
        {
            var receiver = NewGrain();
            await receiver.GetSnapshotAsync();
            var context = Fixture.GetGrainContext(receiver);
            var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
            var handler = new PermanentHandler();
            var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Scheduler.QueueAction(() =>
            {
                grain.HandlerOverride = handler;
                installed.SetResult();
            });
            await installed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var input = CreateEnvelope(receiver, NewMessage(901, "permanent input"), "permanent/input");
            const string counter = "orleans-durable-messaging-inbox-messages-processed";
            var retried = Fixture.Metrics.WaitForCountAsync(counter, 1, "retry");
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, input)).Status);
            await retried;
            Assert.Equal(1, handler.Attempts);
            Assert.Equal(1, (await receiver.GetSnapshotAsync()).InboxCount);

            var terminated = Fixture.Metrics.WaitForCountAsync(counter, 1, "dead_lettered");
            Fixture.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, input)).Status);
            await terminated;
            var snapshot = await receiver.GetSnapshotAsync();
            Assert.Equal(2, handler.Attempts);
            Assert.Equal(0, snapshot.InboxCount);
            Assert.Equal(1, snapshot.ProcessedMessageCount);
            Assert.Empty(snapshot.Effects);
            var deadLetter = Assert.Single(snapshot.InboxDeadLetters);
            Assert.Equal(input.MessageId, deadLetter.MessageId);
            Assert.Equal("permanent input", deadLetter.Reason);
            Assert.Equal(2, deadLetter.AttemptCount);
            Assert.Equal(1, Fixture.Metrics.GetCount(counter, "retry"));
            Assert.Equal(1, Fixture.Metrics.GetCount(counter, "dead_lettered"));
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, input)).Status);
            Assert.Equal(2, handler.Attempts);
        }

        private sealed class PermanentHandler : IInboxHandler
        {
            public int Attempts { get; private set; }

            public ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
            {
                if (++Attempts == 1)
                {
                    throw new ArgumentException("preparation failed");
                }
                context.Fail("permanent input");
                return ValueTask.CompletedTask;
            }
        }

        private sealed class PermanentFailureClusterFixture : DurableMessagingClusterFixture
        {
            protected override void ConfigureOptions(DurableInboxOptions options)
            {
                base.ConfigureOptions(options);
                options.MaxProcessingAttempts = 5;
                options.BackpressureRetryDelay = TimeSpan.FromHours(1);
                options.DeduplicationWindow = TimeSpan.FromDays(7);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DeadLetter_InvalidReasonLeavesTerminalStateUnchanged(string? reason)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, _) =>
        {
            var error = Assert.ThrowsAny<ArgumentException>(() => self.Context.Fail(reason!));
            Assert.Equal("reason", error.ParamName);
            Assert.Single(rig.Inbox);
            Assert.Empty(rig.Processed);
            Assert.Empty(rig.Grain.GetSnapshotForTest().InboxDeadLetters);
            self.Mutate();
            self.Context.Complete();
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadLetter_ConflictingTerminalOutcomePreservesFirstChoice(bool completeFirst)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        Exception? expected = null;
        handler.Body = (self, _) =>
        {
            if (completeFirst) self.Context.Complete();
            else self.Context.Fail("original reason");
            expected = Assert.Throws<InvalidOperationException>(() => self.Context.Fail("different reason"));
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        Assert.Same(expected, Assert.IsType<GrainTimerEvents.TickStop>(
            (await TimerStoppedAsync(events, timer)).Payload).Exception);
        AssertCompletedState(rig, input);
        var letters = rig.Grain.GetSnapshotForTest().InboxDeadLetters;
        if (completeFirst) Assert.Empty(letters);
        else Assert.Equal("original reason", Assert.Single(letters).Reason);
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Outbox);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadLetter_CancellationAndHandlerErrorPreserveTerminalOutcomeUnlessStorageFails(bool failWrite)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        IGrainTimer timer = null!;
        var handlerError = new IOException("after dead letter");
        handler.Body = (self, token) =>
        {
            timer.Dispose();
            Assert.True(token.IsCancellationRequested);
            self.Context.Fail("permanent input failure");
            throw handlerError;
        };
        var input = await DeliverAsync(rig);
        timer = GetTimer(events, rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        AssertCompletedState(rig, input);
        Assert.Equal("permanent input failure", Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters).Reason);
        var storageError = new IOException("actual persistence failure");
        if (failWrite) storage.Fail(storageError);
        else storage.Release();
        await WaitAsync(finished);
        if (failWrite)
        {
            Assert.Same(storageError, await WaitAsync(rig.Grain.DeactivationFailure.Task));
            await AssertFailureReplayAsync(rig, input);
            Assert.NotEqual("permanent input failure", Assert.Single((await rig.Receiver.GetSnapshotAsync()).InboxDeadLetters).Reason);
        }
        else
        {
            Assert.Same(handlerError, Assert.IsType<GrainTimerEvents.TickStop>(
                (await TimerStoppedAsync(events, timer)).Payload).Exception);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
            Assert.Empty(rig.Effects);
            Assert.Empty(rig.Outbox);
            await AssertHealthyAsync(rig);
        }
    }

    [Fact]
    public async Task LateAcknowledgedAcceptance_ImmediatelyRearmsSameLocalTimerWithoutClockAdvance()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, _) =>
        {
            Assert.Null(RequestContext.Get("reusable-turn-parent"));
            self.Mutate();
            self.Context.Complete();
            return default;
        };
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        RequestContext.Set("reusable-turn-parent", "first-call-chain");
        DurableEnvelope first;
        try { first = await DeliverAsync(rig); }
        finally { RequestContext.Remove("reusable-turn-parent"); }
        var timer = GetTimer(events, rig);
        var second = CreateEnvelope(rig.Receiver, NewMessage(502, "late-local"), "async/handler");
        Task<DeliveryResult> accepted = null!;
        // The pump is deliberately non-interleaving. Inject the owned late admission on its
        // scheduler, as capture/hook tests do, rather than waiting on a blocked ordinary RPC.
        await OnTurnAsync(rig.Context, () =>
        {
            RequestContext.Set("reusable-turn-parent", "second-call-chain");
            try
            {
                var extension = (IDurableInboxExtension)rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
                accepted = extension.DeliverAsync(second).AsTask();
            }
            finally { RequestContext.Remove("reusable-turn-parent"); }
        });
        Assert.Equal(DeliveryStatus.Accepted, (await accepted).Status);
        Assert.Equal(2, rig.Inbox.Count);
        var drained = events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, timer)
                && rig.Inbox.Count == 0 && rig.Effects.Count == 2,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        handler.Release.TrySetResult();
        await drained;
        Assert.Equal(2, rig.Effects.Count);
        Assert.Equal(2, rig.Processed.Count);
        Assert.Single(events.Events.Select(item => item.Payload).OfType<GrainTimerEvents.Created>(),
            created => ReferenceEquals(created.GrainContext, rig.Context) && IsLocalDrainTimer(created.Timer));
        Assert.Equal(2, events.Events.Select(item => item.Payload).OfType<GrainTimerEvents.TickStop>()
            .Count(stopped => ReferenceEquals(stopped.Timer, timer)));
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
    }

    [Fact]
    public async Task Complete_StagesBusinessOutputAndDedupeBeforeHandlerReturn()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, token) =>
        {
            self.Mutate();
            rig.Outbox.Send(self.Output);
            self.Context.Complete();
            AssertCompletedState(rig, self.Context.Envelope);
            Assert.Equal(1, Assert.Single(rig.Effects).Value.Count);
            Assert.Single(rig.Outbox);
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockAcknowledgement(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        Assert.False(finished.IsCompleted);
        storage.Release();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 1);
        await DeactivateAsync(rig);
        var recovered = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
    }

    [Fact]
    public async Task Complete_PrecedingWriterCapturesCompletionWithBusinessAcrossReturnContinuation()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var input = await DeliverAsync(rig);
        var writes = Writes(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var preceding = OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "prior";
            await rig.Manager.WriteStateAsync(CancellationToken.None);
        });
        await storage.WaitUntilEnteredAsync();
        var priorCapture = rig.Grain.Captures[^1];
        Assert.Empty(priorCapture.Effects);
        Assert.Equal(1, priorCapture.InboxCount);
        Assert.Equal(0, priorCapture.ProcessedMessageCount);
        Assert.Equal(0, priorCapture.OutboxCount);
        var queued = OnTurnAsync(rig.Context, async () => await rig.Manager.WriteStateAsync(CancellationToken.None));
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Body = async (self, _) =>
        {
            self.Mutate();
            rig.Outbox.Send(self.Output);
            self.Context.Complete();
            AssertCompletedState(rig, input);
            staged.TrySetResult();
            await returnContinuation.Task;
        };
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        try
        {
            await WaitAsync(staged.Task);
            Assert.False(finished.IsCompleted);
            storage.Release();
            await WaitAsync(Task.WhenAll(preceding, queued));
            Assert.False(finished.IsCompleted);
            Assert.Equal(writes + 2, Writes(rig));
            var capturedEffects = Assert.Single(rig.Grain.Captures, snapshot => snapshot.Effects.Count != 0);
            Assert.Equal(0, capturedEffects.InboxCount);
            Assert.Equal(1, capturedEffects.ProcessedMessageCount);
            Assert.Equal(1, capturedEffects.OutboxCount);
            Assert.Equal(new DurableEffect(input.MessageId, 1, 501, "async-handler"), Assert.Single(capturedEffects.Effects));
        }
        finally
        {
            returnContinuation.TrySetResult();
        }
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerFailure_BeforeVsAfterCompleteUsesActualLogicalOutcome(bool completed)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var error = new IOException("Handler outcome.");
        handler.Body = (self, _) =>
        {
            if (completed)
            {
                self.Mutate();
                rig.Outbox.Send(self.Output);
                self.Context.Complete();
            }
            throw error;
        };
        var input = await DeliverAsync(rig);
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        var stop = (GrainTimerEvents.TickStop)(await TimerStoppedAsync(events, timer)).Payload!;
        if (completed)
        {
            Assert.Same(error, stop.Exception);
            AssertSuccess(rig, input, outputCount: 1);
            Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(rig.Receiver, input)).Status);
        }
        else
        {
            Assert.Null(stop.Exception);
            Assert.Empty(rig.Effects);
            Assert.Empty(rig.Outbox);
            Assert.Equal(input.MessageId, Assert.Single(rig.Grain.GetSnapshotForTest().InboxDeadLetters).MessageId);
        }
        await AssertHealthyAsync(rig);
        await DeactivateAsync(rig);
        var replay = await rig.Receiver.GetSnapshotAsync();
        Assert.Equal(completed ? 1 : 0, replay.Effects.Count);
        Assert.Equal(1, replay.ProcessedMessageCount);
        Assert.Equal(completed ? 0 : 1, replay.InboxDeadLetters.Count);
    }

    [Fact]
    public async Task SuccessfulReturnWithoutComplete_IsExplicitTerminalMisuse()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (_, _) => ValueTask.CompletedTask;
        var input = await DeliverAsync(rig);
        var writes = Writes(rig);
        handler.Release.TrySetResult();
        var error = Assert.IsType<InvalidOperationException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
        Assert.Contains("must call Complete", error.Message, StringComparison.Ordinal);
        Assert.Equal(writes, Writes(rig));
        await rig.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Single(rig.Inbox);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Effects);
        await AssertFailureReplayAsync(rig, input);
    }

    [Fact]
    public async Task RepeatedComplete_SameActiveAttemptStagesExactlyOnce()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        handler.Body = (self, _) =>
        {
            self.Mutate();
            self.Context.Complete();
            var timestamp = Assert.Single(rig.Processed).Value;
            self.Context.Complete();
            Assert.Equal(timestamp, Assert.Single(rig.Processed).Value);
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dead-letter")]
    public async Task RetainedContext_AfterRetirementRejectsAndKeepsOwnerHealthy(string operation)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var input = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OnTurnAsync(rig.Context, () => InvokeContextOperation(handler, operation)));
        Assert.Contains("inactive or different attempt", error.Message, StringComparison.Ordinal);
        AssertSuccess(rig, input, outputCount: 0);
        await AssertHealthyAsync(rig);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("dead-letter")]
    public async Task RetainedContext_DuringOtherAttemptRetainsFirstMisuse(string operation)
    {
        var rig = await CreateAsync();
        using var first = rig.Handler;
        var one = await DeliverAsync(rig);
        var finished = await FinishedAsync(rig);
        first.Release.TrySetResult();
        await WaitAsync(finished);
        using var second = new TestHandler(rig.Effects, rig.Context);
        InvalidOperationException? rejection = null;
        second.Body = (_, _) =>
        {
            try { InvokeContextOperation(first, operation); }
            catch (InvalidOperationException error) { rejection = error; }
            throw new IOException("Replacement failure.");
        };
        await OnTurnAsync(rig.Context, () =>
            rig.Grain.HandlerOverride = second);
        var two = CreateEnvelope(rig.Receiver, NewMessage(502, "next"), "async/next");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, two)).Status);
        await WaitAsync(second.Entered.Task);
        second.Release.TrySetResult();
        var failure = await WaitAsync(rig.Grain.DeactivationFailure.Task);
        Assert.Same(Assert.IsType<InvalidOperationException>(rejection), failure);
        Assert.Equal(1, Assert.Single(rig.Effects).Value.Count);
        await WaitAsync(rig.Context.Deactivated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableJobCancellation_BeforeCompleteRetainsExactOwnerAndRetries(bool cancellationCheck)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var localEvents = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var input = await DeliverAsync(rig);
        var localTimer = GetTimer(localEvents, rig);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        OperationCanceledException? expected = null;
        handler.Body = async (self, token) =>
        {
            if (++attempts > 1)
            {
                await retry.Task.WaitAsync(token);
                self.Mutate();
                self.Context.Complete();
                return;
            }
            entered.TrySetResult();
            try
            {
                if (cancellationCheck)
                {
                    await resume.Task;
                    token.ThrowIfCancellationRequested();
                }
                else await resume.Task.WaitAsync(token);
            }
            catch (OperationCanceledException error) { expected = error; throw; }
        };
        var snapshot = rig.Grain.GetSnapshotForTest();
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, rig.Receiver.GetGrainId()));
        var run = new JobContext(job);
        var feature = (IDurableJobFeatureHandler)rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
        using var jobEvents = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        using var cancellation = new CancellationTokenSource();
        var start = new StartAtTimerStop(rig, localTimer, feature, run, cancellation.Token, jobEvents);
        using var subscription = GrainTimerEvents.AllEvents.Subscribe(start);
        await OnTurnAsync(rig.Context, localTimer.Dispose);
        var timer = await WaitAsync(start.Started.Task);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        handler.Release.TrySetResult();
        try
        {
            await WaitAsync(entered.Task);
            await OnTurnAsync(rig.Context, cancellation.Cancel);
        }
        finally
        {
            resume.TrySetResult();
        }
        await TimerStoppedAsync(jobEvents, timer);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OnTurnAsync(rig.Context, async () => await feature.ExecuteJobAsync(run, TestContext.Current.CancellationToken)));
        Assert.Same(expected, failure);
        Assert.Single(rig.Inbox);
        Assert.Empty(rig.Processed);
        Assert.Empty(rig.Effects);
        Assert.Equal(snapshot.InboxJobId, rig.Grain.GetSnapshotForTest().InboxJobId);
        Assert.Same(snapshot.InboxJob, rig.Grain.GetSnapshotForTest().InboxJob);
        await AssertHealthyAsync(rig);
        var duplicate = DeliverAsync(rig.Receiver, input);
        retry.TrySetResult();
        Assert.Equal(DeliveryStatus.Duplicate, (await duplicate).Status);
        await Fixture.WaitForEffectCountAsync(rig.Receiver, 1);
        AssertSuccess(rig, input, outputCount: 0);
        Assert.Same(rig.Context, Fixture.GetGrainContext(rig.Receiver));
    }

    [Fact]
    public async Task CanceledJobCallback_LeavesActiveHandlerAndCommittedInboxUndisturbed()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var input = await DeliverAsync(rig);
        var snapshot = rig.Grain.GetSnapshotForTest();
        var writes = Writes(rig);
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, rig.Receiver.GetGrainId()));
        var feature = (IDurableJobFeatureHandler)rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
        var canceled = new CancellationToken(canceled: true);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OnTurnAsync(rig.Context, async () =>
            await feature.ExecuteJobAsync(new JobContext(job), canceled)));
        Assert.Equal(canceled, error.CancellationToken);
        Assert.Equal(writes, Writes(rig));
        Assert.Single(rig.Inbox);
        Assert.Empty(rig.Effects);
        Assert.Empty(rig.Processed);
        Assert.Equal(snapshot.InboxJobId, rig.Grain.GetSnapshotForTest().InboxJobId);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        AssertSuccess(rig, input, outputCount: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInFinalBlock_CompleteAndOwnedWritePreserveOutcome(bool failWrite)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        IGrainTimer timer = null!;
        handler.Body = (self, token) =>
        {
            self.Mutate();
            timer.Dispose();
            rig.Outbox.Send(self.Output);
            self.Context.Complete();
            return ValueTask.CompletedTask;
        };
        var input = await DeliverAsync(rig);
        timer = GetTimer(events, rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        AssertCompletedState(rig, input);
        Assert.False(finished.IsCompleted);
        var failure = new OperationCanceledException("Actual storage canceled.", new CancellationToken(canceled: true));
        if (failWrite) storage.Fail(failure);
        else storage.Release();
        await WaitAsync(finished);
        if (failWrite)
        {
            Assert.Same(failure, await WaitAsync(rig.Grain.DeactivationFailure.Task));
            await AssertFailureReplayAsync(rig, input);
        }
        else
        {
            AssertSuccess(rig, input, outputCount: 1);
            await AssertHealthyAsync(rig);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWrite_HookOutcomePreservesActualCommit(bool postCommit)
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var input = await DeliverAsync(rig);
        var error = new IOException("Journal hook failed.");
        await OnTurnAsync(rig.Context, () => rig.Manager.Hooks.Add(new TestJournaledStateHook
        {
            BeforeOperation = postCommit ? null : (_, _) => throw error,
            AfterOperation = postCommit ? (_, _) => throw error : null
        }));
        var timer = GetTimer(events, rig);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await WaitAsync(finished);
        if (postCommit)
        {
            Assert.IsType<JournaledStatePostCommitException>(
                ((GrainTimerEvents.TickStop)(await TimerStoppedAsync(events, timer)).Payload!).Exception);
            AssertSuccess(rig, input, outputCount: 0);
            await OnTurnAsync(rig.Context, rig.Manager.Hooks.Clear);
            await AssertHealthyAsync(rig);
        }
        else
        {
            var failure = Assert.IsType<JournaledStatePreCommitException>(await WaitAsync(rig.Grain.DeactivationFailure.Task));
            Assert.Same(error, failure.InnerException);
            await AssertFailureReplayAsync(rig, input);
        }
    }

    [Fact]
    public async Task PostCompleteHandlerFailure_WithStorageFailureKeepsStorageCauseAuthoritative()
    {
        var rig = await CreateAsync();
        using var handler = rig.Handler;
        var handlerError = new InvalidOperationException("After Complete.");
        handler.Body = (self, _) => { self.Mutate(); self.Context.Complete(); throw handlerError; };
        var input = await DeliverAsync(rig);
        using var storage = Fixture.Storage.BlockWrite(rig.Journal);
        var finished = await FinishedAsync(rig);
        handler.Release.TrySetResult();
        await storage.WaitUntilEnteredAsync();
        var storageError = new IOException("Storage failed.");
        storage.Fail(storageError);
        await WaitAsync(finished);
        Assert.Same(storageError, await WaitAsync(rig.Grain.DeactivationFailure.Task));
        await AssertFailureReplayAsync(rig, input);
    }

    private async Task<Rig> CreateAsync()
    {
        var receiver = NewGrain();
        await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var services = context.ActivationServices;
        var effects = services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEffect>>("test-effects");
        var handler = new TestHandler(effects, context);
        var rig = new Rig(receiver, context, Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance),
            services.GetRequiredService<IJournaledStateManager>(), (JournaledTestOutbox)services.GetRequiredService<IDurableOutbox>(),
            effects, handler,
            services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DurableEnvelope>>("__orleans.durable-messaging.inbox"),
            services.GetRequiredKeyedService<IDurableDictionary<HierarchicalKey, DateTimeOffset>>("__orleans.durable-messaging.inbox-processed"));
        await OnTurnAsync(context, () => rig.Grain.HandlerOverride = handler);
        return rig;
    }

    private async Task<DurableEnvelope> DeliverAsync(Rig rig)
    {
        var input = CreateEnvelope(rig.Receiver, NewMessage(501, "async-handler"), "async/handler");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(rig.Receiver, input)).Status);
        await WaitAsync(rig.Handler.Entered.Task);
        return input;
    }

    private static void InvokeContextOperation(TestHandler handler, string operation)
    {
        switch (operation)
        {
            case "complete": handler.Context.Complete(); break;
            case "dead-letter": handler.Context.Fail("unusable input"); break;
            default: throw new ArgumentException(nameof(operation));
        }
    }

    private static void AssertCompletedState(Rig rig, DurableEnvelope input)
    {
        Assert.Empty(rig.Inbox);
        Assert.Equal(input.MessageId, Assert.Single(rig.Processed).Key);
    }

    private static void AssertSuccess(Rig rig, DurableEnvelope input, int outputCount)
    {
        AssertCompletedState(rig, input);
        Assert.Equal(new DurableEffect(input.MessageId, 1, 501, "async-handler"), Assert.Single(rig.Effects).Value);
        Assert.Equal(outputCount, rig.Outbox.Count);
        Assert.Empty(rig.Grain.GetSnapshotForTest().InboxDeadLetters);
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
    }

    private int Writes(Rig rig) => Fixture.Storage.GetSuccessfulWriteCount(rig.Journal);
    private static async Task AssertHealthyAsync(Rig rig)
    {
        await OnTurnAsync(rig.Context, async () =>
        {
            rig.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "healthy";
            await rig.Manager.WriteStateAsync(CancellationToken.None);
        });
        Assert.False(rig.Grain.DeactivationFailure.Task.IsCompleted);
    }

    private static async Task DeactivateAsync(Rig rig)
    {
        await rig.Receiver.RequestDeactivationAsync();
        await WaitAsync(rig.Context.Deactivated);
    }

    private async Task AssertFailureReplayAsync(Rig rig, DurableEnvelope input)
    {
        await WaitAsync(rig.Context.Deactivated);
        await rig.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForDeadLetterCountAsync(rig.Receiver, 1);
        Assert.Empty(recovered.Effects);
        Assert.Equal(0, recovered.OutboxCount);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(input.MessageId, Assert.Single(recovered.InboxDeadLetters).MessageId);
    }

    private static async Task<Task> FinishedAsync(Rig rig)
    {
        Task result = null!;
        await OnTurnAsync(rig.Context, () =>
        {
            var extension = rig.Context.ActivationServices.GetRequiredService(CancellationCleanupProbe.ExtensionType);
            var pending = CancellationCleanupProbe.Field<System.Collections.IList>(extension, "_pendingWrites");
            var operation = Assert.Single(pending.Cast<object>(), item => item.GetType().Name == "HandlerWrite");
            result = ((TaskCompletionSource)operation.GetType().GetProperty("Finished")!.GetValue(operation)!).Task;
        });
        return result;
    }

    private static IGrainTimer GetTimer(DiagnosticEventCollector events, Rig rig) =>
        Assert.Single(events.Events.Select(item => item.Payload).OfType<GrainTimerEvents.Created>(),
            item => ReferenceEquals(item.GrainContext, rig.Context) && IsLocalDrainTimer(item.Timer)).Timer;
    private static IGrainTimer GetPumpTimer(DiagnosticEventCollector events, Rig rig) =>
        Assert.Single(events.Events.Select(item => item.Payload).OfType<GrainTimerEvents.Created>(),
            item => ReferenceEquals(item.GrainContext, rig.Context) && IsInboxTimer(item.Timer)
                && !IsLocalDrainTimer(item.Timer)).Timer;
    private static bool IsLocalDrainTimer(IGrainTimer timer) => IsInboxTimer(timer)
        && timer.GetType().GenericTypeArguments[0].Name == "LocalDrainTimerState";
    private static bool IsInboxTimer(IGrainTimer timer) => timer.GetType().GenericTypeArguments is [var type]
        && type.DeclaringType == ReceiverTestServices.GetImplementationType("DurableInboxExtension");
    private static Task<DiagnosticEvent> TimerStoppedAsync(DiagnosticEventCollector events, IGrainTimer timer) =>
        events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && ReferenceEquals(stop.Timer, timer),
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static Task OnTurnAsync(IGrainContext context, Action action) =>
        OnTurnAsync(context, () => { action(); return Task.CompletedTask; });
    private static Task OnTurnAsync(IGrainContext context, Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() => { _ = CompleteAsync(); });
        return done.Task;
        async Task CompleteAsync()
        {
            try
            {
                Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
                await action();
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }
    }

    private static Task WaitAsync(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private static Task<T> WaitAsync<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    private sealed record Rig(IDurableMessagingTestGrain Receiver, IGrainContext Context, DurableMessagingTestGrain Grain,
        IJournaledStateManager Manager, JournaledTestOutbox Outbox, IDurableDictionary<HierarchicalKey, DurableEffect> Effects,
        TestHandler Handler, IDurableDictionary<HierarchicalKey, DurableEnvelope> Inbox,
        IDurableDictionary<HierarchicalKey, DateTimeOffset> Processed)
    {
        public JournalId Journal => JournalId.FromGrainId(Receiver.GetGrainId());
    }

    private sealed class TestHandler(IDurableDictionary<HierarchicalKey, DurableEffect> effects, IGrainContext grainContext) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IInboxHandlerContext Context { get; private set; } = null!;
        public DurableEnvelope Output { get; private set; }
        public Func<TestHandler, CancellationToken, ValueTask> Body { get; set; } =
            static (self, _) => { self.Mutate(); self.Context.Complete(); return default; };
        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            Context = context;

            Output = TestApplicationProtocol.Create(grainContext.ActivationServices.GetRequiredService<SerializerSessionPool>(), grainContext.GrainId, grainContext.GrainId, "output", 41);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await Body(this, cancellationToken);
        }
        public void Mutate()
        {
            effects.TryGetValue(Context.Envelope.MessageId, out var previous);
            effects[Context.Envelope.MessageId] =
                new DurableEffect(Context.Envelope.MessageId, (previous?.Count ?? 0) + 1, 501, "async-handler");
        }
        public void Dispose()
        {
            Release.TrySetResult();

        }
    }
    private sealed class JobContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }

    private sealed class StartAtTimerStop(Rig rig, IGrainTimer preceding, IDurableJobFeatureHandler feature,
        JobContext run, CancellationToken cancellationToken, DiagnosticEventCollector events)
        : IObserver<GrainTimerEvents.TimerEvent>
    {
        private bool _started;
        public TaskCompletionSource<IGrainTimer> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnNext(GrainTimerEvents.TimerEvent value)
        {
            if (_started || value is not GrainTimerEvents.TickStop stopped
                || !ReferenceEquals(stopped.Timer, preceding))
            {
                return;
            }
            _started = true;
            try
            {
                Assert.Same(rig.Context, ReceiverTestServices.CurrentGrainContext);
                var result = feature.ExecuteJobAsync(run, cancellationToken);
                Assert.True(result.IsCompletedSuccessfully);
                Assert.True(result.GetAwaiter().GetResult().IsInProgress);
                Started.SetResult(GetPumpTimer(events, rig));
            }
            catch (Exception error)
            {
                Started.SetException(error);
            }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) => Started.TrySetException(error);
    }
}
