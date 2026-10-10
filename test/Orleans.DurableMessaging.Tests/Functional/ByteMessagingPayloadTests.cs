using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class ByteMessagingPayloadTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task DirectAdmission_CallerCancellationLeavesActualAcceptanceAndHandlerPayloadAvailable()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(
            ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var envelope = CreateEnvelope(receiver, NewMessage(300, "gc-admission"));
        var expected = envelope.Payload.ToArray();
        using var handler = new PayloadProbe(expected);
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        using var scheduling = Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
        using var cancellation = new CancellationTokenSource();
        Task<DeliveryResult> waiting = null!;
        await OnTurnAsync(context, () => waiting = extension.DeliverAsync(envelope, cancellation.Token).AsTask());
        await scheduling.WaitUntilEnteredAsync();
        var actual = (Task)extension.GetType().GetField("_activeDelivery",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
        envelope = default;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(actual.IsCompleted);
        scheduling.Continue();
        await actual.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(expected, handler.Payload);
        handler.Release.TrySetResult();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.InboxCount == 0 && snapshot.ProcessedMessageCount == 1);
        Assert.Empty(completed.InboxDeadLetters);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(expected, handler.Payload);
    }

    [Fact]
    public async Task RpcAcceptanceAndDuplicate_CopyPayloadAndPreserveCallerBytesAcrossReplay()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var envelope = CreateEnvelope(receiver, NewMessage(302, "rpc-bytes"));
        var expected = envelope.Payload.ToArray();
        using var handler = new PayloadProbe(expected);
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotSame(envelope.Payload, handler.Payload);
        envelope.Payload[0] ^= 0xff;
        Assert.Equal(expected, handler.Payload);
        envelope.Payload[0] ^= 0xff;
        Assert.Equal(expected, envelope.Payload);
        handler.Release.TrySetResult();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.InboxCount == 0 && snapshot.ProcessedMessageCount == 1);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(expected, envelope.Payload);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    [Fact]
    public async Task RpcRejection_PreservesCallerBytesWithoutStagingState()
    {
        var receiver = NewGrain();
        await receiver.ConfigureHandlerAsync(false);
        var original = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await original.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(301, "rejected-bytes"));
        var expected = envelope.Payload.ToArray();
        Assert.Equal(DeliveryStatus.HandlerNotFound, (await DeliverAsync(receiver, envelope)).Status);
        var rejected = await receiver.GetSnapshotAsync();
        Assert.Equal(0, rejected.InboxCount);
        Assert.Equal(0, rejected.ProcessedMessageCount);
        Assert.Empty(rejected.Effects);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(expected, envelope.Payload);
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception exception) { completed.SetException(exception); }
        });
        return completed.Task;
    }

    private sealed class PayloadProbe(byte[] expected) : IInboxHandler, IDisposable
    {
        public byte[] Payload { get; private set; } = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                Payload = context.Envelope.Payload;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                Assert.Equal(expected, Payload);
                context.Complete();
                Assert.Same(Payload, context.Envelope.Payload);
                Assert.Equal(expected, context.Envelope.Payload);
                Completed.TrySetResult();
            }
            catch (Exception exception)
            {
                Completed.TrySetException(exception);
                throw;
            }
        }

        public void Dispose() => Release.TrySetResult();
    }
}
