using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The zero-copy consume-on-serialize buffer ownership OR-I18 rests on, checked against Orleans' own generated proxy,
/// copier, invokable, and response at the pinned build (orleans.md §3, §5.12): the write request is not
/// <see cref="ImmutableAttribute"/>, so Orleans' copier retains an independent zero-copy <see cref="WireArcBuffer"/>
/// lease as each call is made; <see cref="WireArcBufferCodec"/> consumes and releases the lease immediately during
/// remote serialization; the grain method alone owns a decoded payload and the caller alone owns a decoded reply; and on
/// a call within one silo the callee consumes the proxy's retained lease and the caller consumes the callee's reply
/// directly with zero copy.
/// </summary>
[Collection(TestingHostCollection.Name)]
public sealed class OrleansBufferOwnershipContractTests : IAsyncLifetime
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddSerializer(static serializer => serializer.AddAssembly(typeof(ILedgerStoreGrain).Assembly))
        .BuildServiceProvider();

    private ReplicaCluster? _cluster;

    private ReplicaCluster Cluster => _cluster!;

    public async ValueTask InitializeAsync() => _cluster = await ReplicaCluster.DeployAsync(2);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cluster is not null)
            {
                _cluster.CalleeScript = null;
                await _cluster.DisposeAsync();
            }

            await _services.DisposeAsync();
        }
        finally
        {
            _leaks.Dispose();
        }
    }

    /// <summary>
    /// The write request is not <see cref="ImmutableAttribute"/>: Orleans' generated copier, which its proxy runs as a call
    /// is made, retains an independent zero-copy lease on the payload, so the copy outlives its source's release, and its
    /// own release returns the pooled pages.
    /// </summary>
    [Fact]
    public void TheCopierPinsAWritesPayload()
    {
        Assert.Null(typeof(LedgerReplicaWriteRequest).GetCustomAttribute<ImmutableAttribute>());
        var source = Request(WireBytes.Arc(1, 2, 3));

        var copy = _services.GetRequiredService<DeepCopier<LedgerReplicaWriteRequest>>().Copy(source);
        source.Payload.Dispose();

        Assert.Equal([1, 2, 3], copy.Payload.ToArray());
        Assert.True(WireBytes.IsReleased(source.Payload));
        copy.Payload.Dispose();
        Assert.True(WireBytes.IsReleased(copy.Payload));
    }

    /// <summary>
    /// OR-I18: <see cref="WireBuffers.CopyToOrleans(ArcBuffer)"/> (used by forwarded journal reads) wraps an exact-sized
    /// unpinned array rather than renting a pooled page, including at <see cref="OrleansBufferWriter.MinimumPageSize"/>,
    /// so retained outbound copies do not leak 32 KiB <see cref="System.Buffers.ArrayPool{T}"/> arrays or pinned pages.
    /// </summary>
    [Theory]
    [InlineData(619)]
    [InlineData(OrleansBufferWriter.MinimumPageSize)]
    [InlineData(OrleansBufferWriter.MinimumPageSize + 17)]
    public void CopyToOrleansWrapsExactSizedNonPooledArray(int length)
    {
        var expected = new byte[length];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = unchecked((byte)(i * 31 + 7));
        }

        using var source = ArcBuffer.CopyFrom(expected);
        var copied = WireBuffers.CopyToOrleans(source);
        Assert.NotNull(copied.First);
        var page = copied.First;

        Assert.Equal(length, copied.Length);
        Assert.Equal(length, page.Array.Length);
        Assert.False(page.IsMinimumSize);
        Assert.Equal(expected, copied.ToArray());

        var deepCopy = _services.GetRequiredService<DeepCopier<OrleansBuffer>>().Copy(copied);
        copied.Dispose();
        Assert.Equal(expected, deepCopy.ToArray());
        var pin = deepCopy;
        pin.Dispose();
        Assert.True(WireBytes.IsReleased(deepCopy));
        Assert.Empty(page.Array);
    }

    /// <summary>
    /// The grain method owns the payload Orleans decoded into a write's invokable: serializing consumes the sender's lease
    /// immediately, and disposing the invokable releases no lease on the decoded payload, so the callee's consumption is
    /// its only one.
    /// </summary>
    [Fact]
    public void TheCalleeAloneOwnsADecodedPayload()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var invokable = (IInvokable)Activator.CreateInstance(InvokableOf(nameof(ILedgerStoreGrain.WriteAsync)))!;
        var sent = Request(WireBytes.Arc(1, 2, 3));
        invokable.SetArgument(0, sent);
        invokable.SetArgument(1, CancellationToken.None);
        var input = WireBytes.Of(serializer.SerializeToArray<object>(invokable));
        Assert.True(WireBytes.IsReleased(sent.Payload));

        var received = Assert.IsAssignableFrom<IInvokable>(serializer.Deserialize<object>(input));
        var payload = ((LedgerReplicaWriteRequest)received.GetArgument(0)!).Payload;
        AssertOneOwner(payload, received.Dispose, input);
    }

    /// <summary>
    /// The caller owns the DATA Orleans decoded into a read's response: serializing consumes the callee's lease
    /// immediately, and disposing the response releases no lease on the decoded DATA, so the caller's consumption is its
    /// only one.
    /// </summary>
    [Fact]
    public void TheCallerAloneOwnsADecodedReply()
    {
        var serializer = _services.GetRequiredService<Serializer>();
        var data = WireBytes.Data(4, 5, 6);
        var input = WireBytes.Of(serializer.SerializeToArray<object>(Response.FromResult(new RemoteReplicaRead(data))));
        Assert.True(WireBytes.IsReleased(data.Bytes));

        var response = Assert.IsAssignableFrom<Response>(serializer.Deserialize<object>(input));
        var bytes = Assert.IsType<RemoteReplicaRead.Data>(((RemoteReplicaRead)response.Result!).Value).Bytes;
        AssertOneOwner(bytes, response.Dispose, input);
    }

    /// <summary>
    /// Across silos, <see cref="WireArcBufferCodec"/> consumes both the proxy's retained write payload lease and the
    /// callee's DATA reply lease immediately during serialization, returning all Durable Storage pages with zero leaks.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OrleansConsumesWritePayloadAndDataReplyOnRemoteSerialization()
    {
        var token = TestContext.Current.CancellationToken;
        var callee = Cluster.SiloNames[1];
        var grain = Cluster.TestCluster.Client.GetGrain<ILedgerStoreGrain>(Cluster.Node(callee).Node.Value);
        using var payload = WireBytes.Arc(new byte[64 << 10]);

        _ = await grain.WriteAsync(Request(payload) with { Version = LedgerReplicaProtocol.Version + 1 }, token);
        var proxyCopy = payload.DetachLastCopy();
        Assert.NotNull(proxyCopy);
        Assert.True(WireBytes.IsReleased(proxyCopy));

        var data = WireBytes.Data(new byte[64 << 10]);
        Cluster.CalleeScript = _ => data;
        await using (var replica = Cluster.Transport(Cluster.SiloNames[0]).Open(Cluster.Node(callee).Node, new LedgerId(31), new LedgerReplicaOptions { WindowSize = 4 }))
        {
            Assert.True((await replica.ReadAsync(new Slot(0), Slot.Zero, token)).TryGetValue(out ArcBuffer batch));
            batch.Dispose();
        }

        Assert.True(WireBytes.IsReleased(data.Bytes));
    }

    /// <summary>
    /// A call to a node's grain from the node's own silo is not serialized: the callee consumes the proxy's retained lease
    /// on the sender's payload and releases it, and the caller consumes the callee's reply directly with zero copy and
    /// releases it, each the one releaser.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OnACallWithinOneSiloEachBufferHasOneReleaser()
    {
        var token = TestContext.Current.CancellationToken;
        var silo = Cluster.SiloNames[0];
        var node = Cluster.Node(silo).Node;
        var grain = Cluster.Services(silo).GetRequiredService<IGrainFactory>().GetGrain<ILedgerStoreGrain>(node.Value);
        using var payload = WireBytes.Arc(7, 8, 9);

        _ = Assert.IsType<ReplicaWriteOutcome.Written>((await grain.WriteAsync(Request(payload), token)).Value);
        var proxyCopy = payload.DetachLastCopy();
        Assert.NotNull(proxyCopy);
        Assert.True(WireBytes.IsReleased(proxyCopy));

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

    private static LedgerReplicaWriteRequest Request(WireArcBuffer payload) =>
        new() { Version = LedgerReplicaProtocol.Version, Ledger = 32, WindowSize = 4, Payload = payload };

    /// <summary>
    /// Asserts that <paramref name="decoded"/> has one owner Orleans keeps no hold on: taking its buffer and then
    /// disposing its carrier, as the receiver and Orleans do, leaves the taken buffer readable, and releasing the taken
    /// buffer and the input it was decoded from returns all pages.
    /// </summary>
    private static void AssertOneOwner(WireArcBuffer decoded, Action disposeCarrier, OrleansBuffer input)
    {
        using var taken = decoded.TakeBuffer();
        Assert.True(WireBytes.IsReleased(decoded));
        disposeCarrier();

        Assert.True(taken.Length > 0);
        input.Dispose();
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
