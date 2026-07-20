using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

#nullable enable

namespace UnitTests.Runtime;

[TestCategory("BVT")]
public class MessageDestinationCacheTests
{
    [Fact]
    public void GrainReferenceCache_DoesNotRetainReceiver()
    {
        var grainId = GrainId.Create("test", "grain");
        var shared = new GrainReferenceShared(
            grainId.Type,
            GrainInterfaceType.Create("test.interface"),
            interfaceVersion: 0,
            runtime: null!,
            InvokeMethodOptions.None,
            codecProvider: null!,
            copyContextPool: null!,
            serviceProvider: null!);
        var grainReference = GrainReference.FromGrainId(shared, grainId);
        var receiver = CacheReceiver(grainReference);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(receiver.TryGetTarget(out _));
        Assert.Null(((IMessageReceiverCache)grainReference).MessageReceiver);
        GC.KeepAlive(grainReference);
    }

    [Fact]
    public void SuccessfulResponse_CachesRespondingSilo()
    {
        var receiver = new object();
        var cache = new TestMessageDestinationCache(receiver, targetSilo: null);
        var request = CreateRequest(targetSilo: null);
        var respondingSilo = CreateSilo(1);
        request.TargetSilo = respondingSilo;
        var response = CreateResponse(request, respondingSilo, Message.ResponseTypes.None);

        MessageDestinationCache.Update(cache, expectedSilo: null, request, response);

        Assert.Same(respondingSilo, cache.TargetSilo);
        Assert.Same(receiver, cache.MessageReceiver);
    }

    [Fact]
    public void CacheInvalidation_ReplacesTargetAndClearsReceiver()
    {
        var oldSilo = CreateSilo(1);
        var newSilo = CreateSilo(2);
        var receiver = new object();
        var cache = new TestMessageDestinationCache(receiver, oldSilo);
        var request = CreateRequest(oldSilo);
        var response = CreateResponse(request, oldSilo, Message.ResponseTypes.Rejection);
        response.AddToCacheInvalidationHeader(
            CreateAddress(request.TargetGrain, oldSilo),
            CreateAddress(request.TargetGrain, newSilo));

        MessageDestinationCache.Update(cache, oldSilo, request, response);

        Assert.Same(newSilo, cache.TargetSilo);
        Assert.Null(cache.MessageReceiver);
    }

    [Fact]
    public void Rejection_ClearsTargetAndReceiver()
    {
        var oldSilo = CreateSilo(1);
        var cache = new TestMessageDestinationCache(new object(), oldSilo);
        var request = CreateRequest(oldSilo);
        var response = CreateResponse(request, oldSilo, Message.ResponseTypes.Rejection);

        MessageDestinationCache.Update(cache, oldSilo, request, response);

        Assert.Null(cache.TargetSilo);
        Assert.Null(cache.MessageReceiver);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleResponse_DoesNotOverwriteNewerDestination(bool isRejection)
    {
        var oldSilo = CreateSilo(1);
        var newSilo = CreateSilo(2);
        var newReceiver = new object();
        var cache = new TestMessageDestinationCache(newReceiver, newSilo);
        var request = CreateRequest(oldSilo);
        var response = CreateResponse(
            request,
            oldSilo,
            isRejection ? Message.ResponseTypes.Rejection : Message.ResponseTypes.Success);

        MessageDestinationCache.Update(cache, oldSilo, request, response);

        Assert.Same(newSilo, cache.TargetSilo);
        Assert.Same(newReceiver, cache.MessageReceiver);
    }

    private static Message CreateRequest(SiloAddress? targetSilo) => new()
    {
        TargetGrain = GrainId.Create("test", "grain"),
        TargetSilo = targetSilo,
    };

    private static Message CreateResponse(Message request, SiloAddress sendingSilo, Message.ResponseTypes responseType) => new()
    {
        Direction = Message.Directions.Response,
        Result = responseType,
        SendingGrain = request.TargetGrain,
        SendingSilo = sendingSilo,
    };

    private static GrainAddress CreateAddress(GrainId grainId, SiloAddress siloAddress) => new()
    {
        GrainId = grainId,
        ActivationId = ActivationId.NewId(),
        SiloAddress = siloAddress,
    };

    private static SiloAddress CreateSilo(int generation) => SiloAddress.New(IPAddress.Loopback, 10_000 + generation, generation);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CacheReceiver(IMessageReceiverCache cache)
    {
        var receiver = new object();
        Assert.True(cache.CompareExchangeMessageReceiver(receiver, comparand: null));
        return new(receiver);
    }

    private sealed class TestMessageDestinationCache : IMessageDestinationCache
    {
        private object? _messageReceiver;
        private SiloAddress? _targetSilo;

        public TestMessageDestinationCache(object? messageReceiver, SiloAddress? targetSilo)
        {
            _messageReceiver = messageReceiver;
            _targetSilo = targetSilo;
        }

        public object? MessageReceiver => Volatile.Read(ref _messageReceiver);

        public SiloAddress? TargetSilo => Volatile.Read(ref _targetSilo);

        public bool CompareExchangeMessageReceiver(object? value, object? comparand)
            => ReferenceEquals(Interlocked.CompareExchange(ref _messageReceiver, value, comparand), comparand);

        public bool CompareExchangeTargetSilo(SiloAddress? value, SiloAddress? comparand)
            => ReferenceEquals(Interlocked.CompareExchange(ref _targetSilo, value, comparand), comparand);
    }
}
