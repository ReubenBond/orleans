using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The Orleans buffer ownership OR-I18 rests on, checked against Orleans' own generated proxy, copier, invokable, and
/// response at the pinned build (orleans.md §3, §5.12), so a pin move which changes it fails here: the write request is
/// not <see cref="ImmutableAttribute"/>, so Orleans' copier pins its payload and keeps the pin; the grain method owns the
/// argument Orleans decoded and the caller the result, Orleans releasing neither; Orleans keeps a reply handed to it; and on
/// a call within one silo the callee releases the proxy's pin and the caller the callee's reply, each the one releaser.
/// </summary>
[Collection(TestingHostCollection.Name)]
[NoPageLeakCheck("The checks count Orleans' pins, which Clog's page accounting does not see; the cluster's calls are leak-checked in OrleansReplicaTransportTests.")]
public sealed class OrleansBufferOwnershipContractTests : IAsyncLifetime
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(ILedgerStoreGrain).Assembly))
        .BuildServiceProvider();

    private ReplicaCluster? _cluster;

    private ReplicaCluster Cluster => _cluster!;

    public async ValueTask InitializeAsync() => _cluster = await ReplicaCluster.DeployAsync(2);

    public async ValueTask DisposeAsync()
    {
        if (_cluster is not null)
        {
            _cluster.CalleeScript = null;
            await _cluster.DisposeAsync();
        }

        await _services.DisposeAsync();
    }

    /// <summary>
    /// The write request is not <see cref="ImmutableAttribute"/>: Orleans' generated copier, which its proxy runs as a call
    /// is made, pins the payload, so the copy outlives its source's release, and its own release returns the pages.
    /// </summary>
    [Fact]
    public void TheCopierPinsAWritesPayload()
    {
        Assert.Null(typeof(LedgerReplicaWriteRequest).GetCustomAttribute<ImmutableAttribute>());
        var source = Request(WireBytes.Of(1, 2, 3));

        var copy = _services.GetRequiredService<DeepCopier<LedgerReplicaWriteRequest>>().Copy(source);
        source.Payload.Dispose();

        Assert.Equal([1, 2, 3], copy.Payload.ToArray());
        var pin = copy.Payload;
        pin.Dispose();
        Assert.True(WireBytes.IsReleased(copy.Payload));
    }

    /// <summary>
    /// The grain method owns the payload Orleans decoded into a write's invokable: disposing the invokable releases no pin,
    /// so the decoded payload's release is its only one.
    /// </summary>
    [Fact]
    public void TheCalleeAloneOwnsADecodedPayload()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var invokable = (IInvokable)Activator.CreateInstance(InvokableOf(nameof(ILedgerStoreGrain.WriteAsync)))!;
        var sent = Request(WireBytes.Of(1, 2, 3));
        invokable.SetArgument(0, sent);
        invokable.SetArgument(1, CancellationToken.None);
        var input = WireBytes.Of(serializer.SerializeToArray<object>(invokable));
        sent.Payload.Dispose();

        var received = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize<object>(input));
        var payload = ((LedgerReplicaWriteRequest)received.GetArgument(0)!).Payload;
        AssertOneOwner(payload, received.Dispose, input);
    }

    /// <summary>
    /// The caller owns the DATA Orleans decoded into a read's response: disposing the response releases no pin, so the
    /// caller's release is its only one.
    /// </summary>
    [Fact]
    public void TheCallerAloneOwnsADecodedReply()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var data = WireBytes.Data(4, 5, 6);
        var input = WireBytes.Of(serializer.SerializeToArray<object>(Response.FromResult(new RemoteReplicaRead(data))));
        data.Bytes.Dispose();

        var response = Assert.IsAssignableFrom<Response>(serializer.Deserialize<object>(input));
        var bytes = Assert.IsType<RemoteReplicaRead.Data>(((RemoteReplicaRead)response.Result!).Value).Bytes;
        AssertOneOwner(bytes, response.Dispose, input);
    }

    /// <summary>
    /// Across silos, Orleans' proxy pins a write's payload as the call is made and never releases the pin, so the sender
    /// releases its own copy as soon as its call ends; Orleans keeps a DATA reply its callee hands it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OrleansKeepsThePinsItTakesAndTheRepliesItIsHanded()
    {
        var token = TestContext.Current.CancellationToken;
        var callee = Cluster.SiloNames[1];
        var grain = Cluster.TestCluster.Client.GetGrain<ILedgerStoreGrain>(Cluster.Node(callee).Node.Value);
        var payload = WireBytes.Of(new byte[64 << 10]);
        var held = payload;

        _ = await grain.WriteAsync(Request(payload) with { Version = LedgerReplicaProtocol.Version + 1 }, token);
        payload.Dispose();
        await AssertKeptAsync(held);

        var data = WireBytes.Data(new byte[64 << 10]);
        Cluster.CalleeScript = _ => data;
        await using (var replica = Cluster.Transport(Cluster.SiloNames[0]).Open(Cluster.Node(callee).Node, new LedgerId(31), new LedgerReplicaOptions { WindowSize = 4 }))
        {
            Assert.True((await replica.ReadAsync(new Slot(0), Slot.Zero, token)).TryGetValue(out ArcBuffer batch));
            batch.Dispose();
        }

        await AssertKeptAsync(data.Bytes);
    }

    /// <summary>
    /// A call to a node's grain from the node's own silo is not serialized: the callee receives the proxy's pin on the
    /// sender's copy and releases it, and the caller receives the callee's reply and releases it, each the one releaser.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OnACallWithinOneSiloEachBufferHasOneReleaser()
    {
        var token = TestContext.Current.CancellationToken;
        var silo = Cluster.SiloNames[0];
        var node = Cluster.Node(silo).Node;
        var grain = Cluster.Services(silo).GetRequiredService<IGrainFactory>().GetGrain<ILedgerStoreGrain>(node.Value);
        var payload = WireBytes.Of(7, 8, 9);
        var held = payload;

        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await grain.WriteAsync(Request(payload), token)).Value);
        payload.Dispose();
        Assert.True(WireBytes.IsReleased(held));

        var answer = await grain.ReadAsync(
            new LedgerReplicaReadRequest { Version = LedgerReplicaProtocol.Version, Ledger = 32, WindowSize = 4, MaxReplyBytes = 1024 },
            token);
        var reply = Assert.IsType<RemoteReplicaRead.Data>(answer.Value).Bytes;
        Assert.True(LedgerReplicaCallResults.Read(node, answer, 1024, ranged: false).TryGetValue(out ArcBuffer batch));
        using (batch)
        {
            Assert.Equal([7, 8, 9], batch.ToArray());
        }

        Assert.True(WireBytes.IsReleased(reply));
        await Cluster.AssertQuiescentAsync();
    }

    private static LedgerReplicaWriteRequest Request(OrleansBuffer payload) =>
        new() { Version = LedgerReplicaProtocol.Version, Ledger = 32, WindowSize = 4, Payload = payload };

    /// <summary>
    /// Asserts that <paramref name="decoded"/> has one owner Orleans keeps no hold on: with a test's pin beside it,
    /// releasing it and then disposing its carrier, as the receiver and Orleans do, leaves the test's pin readable, and
    /// releasing the test's pin and the input it was decoded from returns its pages.
    /// </summary>
    private static void AssertOneOwner(OrleansBuffer decoded, Action disposeCarrier, OrleansBuffer input)
    {
        var held = decoded;
        var pin = decoded.Slice(0);
        decoded.Dispose();
        disposeCarrier();

        Assert.False(WireBytes.IsReleased(pin));
        input.Dispose();
        pin.Dispose();
        Assert.True(WireBytes.IsReleased(held));
    }

    /// <summary>Asserts that <paramref name="buffer"/>, released by its sender, stays readable: Orleans holds a pin on it.</summary>
    private static async Task AssertKeptAsync(OrleansBuffer buffer)
    {
        var until = TimeProvider.System.GetTimestamp() + (long)(Settle.TotalSeconds * TimeProvider.System.TimestampFrequency);
        do
        {
            Assert.False(WireBytes.IsReleased(buffer), "Orleans released a pin it took or a reply it was handed; OR-I18's one-releaser argument needs revisiting.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        while (TimeProvider.System.GetTimestamp() < until);
    }

    /// <summary>Returns the invokable type Orleans generated for the <see cref="ILedgerStoreGrain"/> method <paramref name="method"/>.</summary>
    private static Type InvokableOf(string method) => typeof(ILedgerStoreGrain).Assembly.GetTypes().Single(type =>
        typeof(IInvokable).IsAssignableFrom(type)
        && !type.IsAbstract
        && type.GetConstructor(Type.EmptyTypes) is not null
        && Activator.CreateInstance(type) is IInvokable invokable
        && invokable.GetInterfaceType() == typeof(ILedgerStoreGrain)
        && invokable.GetMethodName() == method);
}
