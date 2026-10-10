using Documentation.Grains.DurableMessaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Documentation;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class DurableMessagingSnippetTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private static readonly GrainId Sender = GrainId.Create("sender", "snippet");
    private static readonly GrainId Receiver = GrainId.Create("notification", "snippet");
    private static readonly HierarchicalKey Command = HierarchicalKey.Create("notifications", "42");
    private Serializer Serializer => _services.GetRequiredService<Serializer>();
    private readonly List<DurableEnvelope> _owned = [];

    private DurableMessageType<T> Type<T>() => new(typeof(T).Name, _services.GetRequiredService<Serializer<T>>());

    private DurableEnvelope Own(DurableEnvelope envelope)
    {
        _owned.Add(envelope);
        return envelope;
    }

    [Fact]
    public async Task NotificationReply_DerivesResultIdentityFromApplicationCommand()
    {
        var attempt = await CreateAsync(new Notify("received message", Sender));
        var handling = attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "send", "count", "complete" }, attempt.Events);
        await handling;
        var reply = Assert.Single(attempt.Output);
        Assert.Equal(Sender, reply.ReceiverId);
        Assert.Equal(Receiver, reply.SenderId);
        Assert.Equal(Command.CreateChildKey("result"), reply.MessageId);
        Assert.Equal(Type<NotificationReceived>().Subject, reply.Subject);
        Assert.Equal(new NotificationReceived("received message", Command), Type<NotificationReceived>().Decode(reply));
        Assert.Equal(8, attempt.Count.Value);
        attempt.Inbox.Received(1).RegisterHandler(attempt.Handler);
        attempt.Outbox.Received(1).Send(reply);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationHandling_PreMutationCancellationPreservesBusinessState()
    {
        var attempt = await CreateAsync(new Notify("received message", Sender));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, cancellation.Token));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_CancellationDuringSynchronousPreparationPreservesCompletedOutcome()
    {
        var attempt = await CreateAsync(new Notify("prepared message", Sender));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnRead = cancellation.Cancel;

        await attempt.Handler.HandleAsync(attempt.Context, cancellation.Token);

        attempt.Count.OnRead = null;
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(8, attempt.Count.Value);
        Assert.Single(attempt.Output);
        Assert.Equal(new[] { "send", "count", "complete" }, attempt.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotificationHandling_CancellationAfterFirstMutationCompletesBeforeMethodReturns(bool replyRequested)
    {
        var attempt = await CreateAsync(new Notify("prepared message", replyRequested ? Sender : null));
        using var cancellation = new CancellationTokenSource();
        attempt.Count.OnWrite = () =>
        {
            Assert.False(cancellation.IsCancellationRequested);
            cancellation.Cancel();
        };

        var handling = attempt.Handler.HandleAsync(attempt.Context, cancellation.Token);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(replyRequested ? new[] { "send", "count", "complete" } : new[] { "count", "complete" }, attempt.Events);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        attempt.Context.Received(1).Complete();
    }

    [Fact]
    public async Task NotificationWithoutReply_StagesCountAndCompletes()
    {
        var attempt = await CreateAsync(new Notify("received message"));
        var handling = attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken);

        Assert.True(handling.IsCompletedSuccessfully);
        Assert.Equal(new[] { "count", "complete" }, attempt.Events);
        await handling;
        Assert.Equal(8, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        attempt.Context.Received(1).Complete();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task NotificationHandling_InvalidBodyLeavesBusinessAndCompletionUnchanged(string? body)
    {
        var attempt = await CreateAsync(new Notify(body));
        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.IsType(body is null ? typeof(ArgumentNullException) : typeof(ArgumentException), exception);
        Assert.Equal("message.Text", exception.ParamName);
        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Output);
        Assert.Empty(attempt.Events);
    }

    [Fact]
    public async Task NotificationHandling_UnexpectedSubjectPreservesBusinessState()
    {
        var attempt = await CreateAsync(new Notify("prepared"));
        var unexpected = Own(Type<NotificationReceived>().Create(
            Command, Sender, Receiver, new NotificationReceived("receipt", Command)));
        attempt.Context.Envelope.Returns(unexpected);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await attempt.Handler.HandleAsync(attempt.Context, TestContext.Current.CancellationToken));

        Assert.Equal(7, attempt.Count.Value);
        Assert.Empty(attempt.Events);
        Assert.Empty(attempt.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shipment_OrdinarySerializationRoundTripPreservesReservationAndManifest(bool emptyManifest)
    {
        var request = new ReserveStock(3, Sender);
        byte[] manifest = [1, 2, 3];
        if (emptyManifest) manifest = [];
        var shipment = new Shipment(request, manifest);
        var serializer = _services.GetRequiredService<Serializer<Shipment>>();

        var bytes = serializer.SerializeToArray(shipment);
        if (!emptyManifest) manifest[0] = 99;
        var decoded = Assert.IsType<Shipment>(serializer.Deserialize(bytes));

        Assert.Equal(request, decoded.Reservation);
        Assert.NotSame(shipment, decoded);
        if (emptyManifest)
        {
            Assert.Empty(decoded.Manifest);
        }
        else
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Manifest);
            Assert.NotSame(manifest, decoded.Manifest);
        }
    }

    [Fact]
    public async Task ShipmentSender_StagesOrdinaryRecordBeforeAwaitingJournalAcknowledgement()
    {
        var outbox = Substitute.For<IDurableOutbox>();
        outbox.SenderId.Returns(Sender);
        var state = Substitute.For<IDurableStateManager>();
        var shipments = new DurableMessageType<Shipment>(MessagingSubjects.Shipment,
            _services.GetRequiredService<Serializer<Shipment>>());
        var grain = new ShipmentSenderGrain(outbox, state, shipments);
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new List<DurableEnvelope>();
        var events = new List<string>();
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            output.Add(Own(call.Arg<DurableEnvelope>().Retain()));
            events.Add("send");
        });
        state.WriteStateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            events.Add("write");
            return new ValueTask(acknowledgement.Task);
        });
        var shipment = new Shipment(new ReserveStock(3, Sender), [1, 2, 3]);

        var sending = grain.SendAsync(Command, Receiver, shipment);

        Assert.False(sending.IsCompleted);
        Assert.Equal(new[] { "send", "write" }, events);
        shipment.Manifest[0] = 99;
        var envelope = Assert.Single(output);
        Assert.Equal(Command, envelope.MessageId);
        Assert.Equal(Sender, envelope.SenderId);
        Assert.Equal(Receiver, envelope.ReceiverId);
        Assert.Equal(MessagingSubjects.Shipment, envelope.Subject);
        var received = shipments.Decode(envelope);
        Assert.Equal(shipment.Reservation, received.Reservation);
        Assert.Equal(new byte[] { 1, 2, 3 }, received.Manifest);
        acknowledgement.SetResult();
        await sending;
        await state.Received(1).WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShipmentSender_InvalidBodyLeavesIntentAndJournalWriteUnstaged()
    {
        var outbox = Substitute.For<IDurableOutbox>();
        outbox.SenderId.Returns(Sender);
        var state = Substitute.For<IDurableStateManager>();
        var grain = new ShipmentSenderGrain(outbox, state, Type<Shipment>());

        await Assert.ThrowsAsync<ArgumentNullException>(() => grain.SendAsync(Command, Receiver, null!));

        outbox.DidNotReceive().Send(Arg.Any<DurableEnvelope>());
        await state.DidNotReceive().WriteStateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Envelope_RetainOwnsIndependentPinAndSerializationDoesNotConsumePayload()
    {
        DurableEnvelope retained;
        using (var envelope = Type<Notify>().Create(Command, Sender, Receiver, new Notify("owned")))
        {
            retained = envelope.Retain();
        }

        using (retained)
        {
            using var encoder = new ArcBufferWriter();
            Serializer.Serialize(retained, encoder);
            using var first = encoder.ConsumeSlice(encoder.Length);
            Serializer.Serialize(retained, encoder);
            using var second = encoder.ConsumeSlice(encoder.Length);
            Assert.Equal(first.ToArray(), second.ToArray());
            using var decoded = Serializer.Deserialize<DurableEnvelope>(first);
            Assert.Equal(retained.MessageId, decoded.MessageId);
            Assert.Equal(retained.Subject, decoded.Subject);
            Assert.Equal(new Notify("owned"), Type<Notify>().Decode(decoded));
            Assert.Equal(new Notify("owned"), Type<Notify>().Decode(retained));
        }
    }

    [Fact]
    public async Task NotificationHandling_StagingFailureLeavesBusinessAndCompletionUnstaged()
    {
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        outbox.SenderId.Returns(Receiver);
        var context = Substitute.For<IInboxHandlerContext>();
        var input = Own(Type<Notify>().Create(Command, Sender, Receiver, new Notify("prepared", Sender)));
        context.Envelope.Returns(input);
        ArcBuffer borrowedReply = default;
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            borrowedReply = call.Arg<DurableEnvelope>().Payload;
            throw new IOException("staging failed");
        });
        IInboxHandler handler = null!;
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(call => handler = call.Arg<IInboxHandler>());
        var count = new TestCount([]);
        var grain = new NotificationGrain(inbox, outbox, Type<Notify>(), Type<NotificationReceived>(), count);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(async () =>
            await handler.HandleAsync(context, TestContext.Current.CancellationToken));
        context.DidNotReceive().Complete();
        Assert.NotEqual(0, borrowedReply.Length);
        Assert.Equal(7, count.Value);
    }

    private async Task<Attempt> CreateAsync(Notify message)
    {
        var inbox = Substitute.For<IDurableInbox>();
        var outbox = Substitute.For<IDurableOutbox>();
        outbox.SenderId.Returns(Receiver);
        var context = Substitute.For<IInboxHandlerContext>();
        var input = Own(Type<Notify>().Create(Command, Sender, Receiver, message));
        context.Envelope.Returns(input);
        var events = new List<string>();
        var output = new List<DurableEnvelope>();
        var count = new TestCount(events);
        outbox.When(value => value.Send(Arg.Any<DurableEnvelope>())).Do(call =>
        {
            output.Add(Own(call.Arg<DurableEnvelope>().Retain()));
            events.Add("send");
        });
        context.When(value => value.Complete()).Do(_ => events.Add("complete"));
        IInboxHandler handler = null!;
        inbox.When(value => value.RegisterHandler(Arg.Any<IInboxHandler>())).Do(call => handler = call.Arg<IInboxHandler>());
        var grain = new NotificationGrain(inbox, outbox, Type<Notify>(), Type<NotificationReceived>(), count);
        await grain.OnActivateAsync(TestContext.Current.CancellationToken);
        return new(grain, handler, inbox, outbox, context, count, output, events);
    }

    public void Dispose()
    {
        foreach (var envelope in _owned) envelope.Dispose();
        _services.Dispose();
    }

    private sealed record Attempt(NotificationGrain Grain, IInboxHandler Handler, IDurableInbox Inbox, IDurableOutbox Outbox,
        IInboxHandlerContext Context, TestCount Count, List<DurableEnvelope> Output, List<string> Events);

    private sealed class TestCount(List<string> events) : IDurableValue<int>
    {
        private int _value = 7;
        public Action? OnRead { get; set; }
        public Action? OnWrite { get; set; }
        public int Value
        {
            get { OnRead?.Invoke(); return _value; }
            set { _value = value; events.Add("count"); OnWrite?.Invoke(); }
        }
    }
}
