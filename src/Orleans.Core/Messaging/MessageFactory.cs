
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class MessageFactory
    {
        private static readonly int ProcessorCount = CeilingPowerOfTwo(Environment.ProcessorCount);
        private static readonly int ProcessorMask = ProcessorCount - 1;
        private static readonly PaddedULong[] _nextIds = new PaddedULong[ProcessorCount];

        // The nonce reduces the chance of an id collision for a given grain to effectively zero. Id collisions are only relevant in scenarios
        // where where the infinitesimally small chance of a collision is acceptable, such as call cancellation.
        public readonly ulong _seed = unchecked((ulong)Random.Shared.NextInt64());

        public CorrelationId GetNextCorrelationId()
        {
            var coreId = Thread.GetCurrentProcessorId() & ProcessorMask;
            var tid = BinaryPrimitives.ReverseEndianness((ulong)coreId);
            var id = _seed ^ tid ^ _nextIds[coreId].Increment();
            return new(unchecked((long)id));
        }

        public static int CeilingPowerOfTwo(int x) => (int)(1u << -BitOperations.LeadingZeroCount((uint)x - 1));

        private readonly DeepCopier _deepCopier;
        private readonly ILogger _logger;
        private readonly MessagingTrace _messagingTrace;

        public MessageFactory(DeepCopier deepCopier, ILogger<MessageFactory> logger, MessagingTrace messagingTrace)
        {
            _deepCopier = deepCopier;
            _logger = logger;
            _messagingTrace = messagingTrace;

            _seed = unchecked((ulong)Random.Shared.NextInt64());
        }

        public Message CreateMessage(object body, InvokeMethodOptions options)
        {
            var message = new Message
            {
                Direction = (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request,
                Id = GetNextCorrelationId(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                IsAlwaysInterleave = (options & InvokeMethodOptions.AlwaysInterleave) != 0,
                BodyObject = body,
                RequestContextData = RequestContextExtensions.Export(_deepCopier),
            };

            _messagingTrace.OnCreateMessage(message);
            return message;
        }

        /*
        private CorrelationId GetNextCorrelationId()
        {
            var id = _seed ^ Interlocked.Increment(ref _nextId);
            return new CorrelationId(unchecked((long)id));
        }
        */

        public Message CreateResponseMessage(Message request)
        {
            var response = new Message
            {
                IsSystemMessage = request.IsSystemMessage,
                Direction = Message.Directions.Response,
                Id = request.Id,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                TargetSilo = request.SendingSilo,
                TargetGrain = request.SendingGrain,
                SendingSilo = request.TargetSilo,
                SendingGrain = request.TargetGrain,
                CacheInvalidationHeader = request.CacheInvalidationHeader,
                TimeToLive = request.TimeToLive,
                RequestContextData = RequestContextExtensions.Export(_deepCopier),
            };

            _messagingTrace.OnCreateMessage(response);
            return response;
        }

        public Message CreateRejectionResponse(Message request, Message.RejectionTypes type, string info, Exception ex = null)
        {
            var response = CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Rejection;
            response.BodyObject = new RejectionResponse
            {
                RejectionType = type,
                RejectionInfo = info,
                Exception = ex,
            };
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(
                    ex,
                    "Creating {RejectionType} rejection with info '{Info}' at:" + Environment.NewLine + "{StackTrace}",
                    type,
                    info,
                    Utils.GetStackTrace());
            return response;
        }

        internal Message CreateDiagnosticResponseMessage(Message request, bool isExecuting, bool isWaiting, List<string> diagnostics)
        {
            var response = CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Status;
            response.BodyObject = new StatusResponse(isExecuting, isWaiting, diagnostics);

            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Creating {RequestMessage} status update with diagnostics {Diagnostics}", request, diagnostics);

            return response;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 2 * Padding.CACHE_LINE_SIZE)] // padding before/between/after fields
    public struct PaddedULong
    {
        [FieldOffset(Padding.CACHE_LINE_SIZE)] public ulong value;
        public ulong Increment() => Interlocked.Increment(ref value);
    }

    internal static class Padding
    {
#if TARGET_ARM64 || TARGET_LOONGARCH64
        internal const int CACHE_LINE_SIZE = 128;
#else
        internal const int CACHE_LINE_SIZE = 64;
#endif
    }
}