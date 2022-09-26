#nullable enable
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Buffers;
using static Orleans.Runtime.Message;
using static Orleans.Serialization.Buffers.PooledBuffer;
using System.Diagnostics;
using Orleans.Serialization.Utilities;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageSerializer
    {
        private const int MessageSizeHint = 4096;
        private readonly Serializer<object> _bodySerializer;
        private readonly Serializer<GrainAddress> _activationAddressCodec;
        private readonly CachingSiloAddressCodec _readerSiloAddressCachingCodec;
        private readonly CachingSiloAddressCodec _writerSiloAddressCachingCodec;
        private readonly CachingIdSpanCodec _readerIdSpanCachingCodec;
        private readonly CachingIdSpanCodec _writerIdSpanCachingCodec;
        private readonly Serializer _serializer;
        private readonly SerializerSession _serializationSession;
        private readonly SerializerSession _deserializationSession;
        private readonly int _maxMessageLength;
        private readonly SerializerSessionPool _sessionPool;
        private readonly DictionaryCodec<string, object> _requestContextCodec;

        public MessageSerializer(
            Serializer<object> bodySerializer,
            Serializer serializer,
            SerializerSessionPool sessionPool,
            Serializer<GrainAddress> activationAddressSerializer,
            ICodecProvider codecProvider,
            int maxHeaderSize)
        {
            _readerSiloAddressCachingCodec = new CachingSiloAddressCodec();
            _writerSiloAddressCachingCodec = new CachingSiloAddressCodec();
            _readerIdSpanCachingCodec = new CachingIdSpanCodec();
            _writerIdSpanCachingCodec = new CachingIdSpanCodec();
            _serializer = serializer;
            _activationAddressCodec = activationAddressSerializer;
            _serializationSession = sessionPool.GetSession();
            _deserializationSession = sessionPool.GetSession();
            _bodySerializer = bodySerializer;
            _maxMessageLength = maxHeaderSize;
            _sessionPool = sessionPool;
            _requestContextCodec = OrleansGeneratedCodeHelper.GetService<DictionaryCodec<string, object>>(this, codecProvider);
        }

        public void Read(in BufferSlice buffer, out Message message)
        {
            try
            {
                var reader = Reader.Create(buffer, _deserializationSession);

                // Decode header
                message = new();
                ReadMessageHeader(ref reader, message);

                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // TODO: DEFER BODY DECODING UNTIL LATER
                // Decode body
                _deserializationSession.Reset();
                var bodySlice = buffer.Slice((int)reader.Position);

                var formatted = BitStreamFormatter.Format(bodySlice, _deserializationSession);
                _deserializationSession.Reset();
                Debug.WriteLine($"== START {message.Id} ==\n{formatted}\n== END {message.Id} ==");

                reader = Reader.Create(bodySlice, _deserializationSession);
                var fieldHeader = reader.ReadFieldHeader();
                message.BodyObject = ObjectCodec.ReadValue(ref reader, fieldHeader);
            }
            finally
            {
                _deserializationSession.Reset();
            }
        }

        public void ReadHeader(in BufferSlice buffer, out Message message)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(buffer, session);

            // Decode header
            message = new();
            ReadMessageHeader(ref reader, message);
        }

        public void ReadBody(in BufferSlice buffer, Message message)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(buffer, session);
            var fieldHeader = reader.ReadFieldHeader();
            message.BodyObject = ObjectCodec.ReadValue(ref reader, fieldHeader);
        }

        public void Write(ref PooledBuffer buffer, Message message)
        {
            try
            {
                // Write the header and the payload
                var start = buffer.Length;
                var writer = Writer.Create(buffer, _serializationSession);
                WriteMessageHeader(ref writer, message);
                writer.Commit();
                var length = writer.Position;
                _serializationSession.Reset();

                var headerSlice = writer.Output.Slice(start, length);
                ReadHeader(in headerSlice, out var readMsg);

                // Reset the writer, since the header and body are deserialized separately.
                var bodyBuffer = writer.Output;
                writer = Writer.Create(bodyBuffer, _serializationSession);
                ObjectCodec.WriteField(ref writer, 0, typeof(object), message.BodyObject);
                writer.Commit();

                ReadBody(writer.Output.Slice(start + length), readMsg);

                // Copy the modified writer output struct back (we may be able to avoid this once ref structs can hold ref fields)
                length += writer.Position;
                buffer = writer.Output;

                // Before completing, check lengths
                ThrowIfLengthInvalid(buffer.Length);
            }
            finally
            {
                _serializationSession.Reset();
            }
        }

        private void ThrowIfLengthInvalid(int length)
        {
            if (length <= 0 || length > _maxMessageLength)
            {
                throw new OrleansException($"Invalid message size: {length} (max configured value is {_maxMessageLength}, see {nameof(MessagingOptions.MaxMessageSize)})");
            }
        }

        private Message WriteMessageHeader<TBufferWriter>(ref Writer<TBufferWriter> writer, Message value) where TBufferWriter : IBufferWriter<byte>
        {
            var headers = value.Headers;
            writer.WriteUInt32((uint)headers);

            writer.WriteInt64(value.Id.ToInt64());
            WriteGrainId(ref writer, value.SendingGrain);
            WriteGrainId(ref writer, value.TargetGrain);
            _writerSiloAddressCachingCodec.WriteRaw(ref writer, value.SendingSilo);
            _writerSiloAddressCachingCodec.WriteRaw(ref writer, value.TargetSilo);

            if (headers.HasFlag(MessageFlags.HasTimeToLive))
            {
                writer.WriteInt32((int)value.GetTimeToLiveMilliseconds());
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceType))
            {
                _writerIdSpanCachingCodec.WriteRaw(ref writer, value.InterfaceType.Value);
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceVersion))
            {
                writer.WriteVarUInt32(value.InterfaceVersion);
            }

            if (headers.HasFlag(MessageFlags.HasCallChainId))
            {
                GuidCodec.WriteRaw(ref writer, value.CallChainId);
            }

            if (headers.HasFlag(MessageFlags.HasCacheInvalidationHeader))
            {
                WriteCacheInvalidationHeaders(ref writer, value.CacheInvalidationHeader);
            }

            // Always write RequestContext last
            if (headers.HasFlag(MessageFlags.HasRequestContextData))
            {
                WriteRequestContext(ref writer, value.RequestContextData);
            }

            return value;
        }

        private void ReadMessageHeader<TInput>(ref Reader<TInput> reader, Message result)
        {
            var headers = (PackedHeaders)reader.ReadUInt32();

            result.Headers = headers;
            result.Id = new CorrelationId(reader.ReadInt64());
            result.SendingGrain = ReadGrainId(ref reader);
            result.TargetGrain = ReadGrainId(ref reader);
            result.SendingSilo = _readerSiloAddressCachingCodec.ReadRaw(ref reader);
            result.TargetSilo = _readerSiloAddressCachingCodec.ReadRaw(ref reader);

            if (headers.HasFlag(MessageFlags.HasTimeToLive))
            {
                result.SetTimeToLiveMilliseconds(reader.ReadInt32());
            }
            else
            {
                result.SetInfiniteTimeToLive();
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceType))
            {
                var interfaceTypeSpan = _readerIdSpanCachingCodec.ReadRaw(ref reader);
                result.InterfaceType = new GrainInterfaceType(interfaceTypeSpan);
            }

            if (headers.HasFlag(MessageFlags.HasInterfaceVersion))
            {
                result.InterfaceVersion = (ushort)reader.ReadVarUInt32();
            }

            if (headers.HasFlag(MessageFlags.HasCallChainId))
            {
                result.CallChainId = GuidCodec.ReadRaw(ref reader);
            }

            if (headers.HasFlag(MessageFlags.HasCacheInvalidationHeader))
            {
                result.CacheInvalidationHeader = ReadCacheInvalidationHeaders(ref reader);
            }

            if (headers.HasFlag(MessageFlags.HasRequestContextData))
            {
                result.RequestContextData = ReadRequestContext(ref reader);
            }
        }

        private List<GrainAddress> ReadCacheInvalidationHeaders<TInput>(ref Reader<TInput> reader)
        {
            var n = reader.ReadVarUInt32();
            if (n > 0)
            {
                var list = new List<GrainAddress>((int)n);
                for (int i = 0; i < n; i++)
                {
                    list.Add(_activationAddressCodec.Deserialize(ref reader));
                }

                return list;
            }

            return new List<GrainAddress>();
        }

        private void WriteCacheInvalidationHeaders<TBufferWriter>(ref Writer<TBufferWriter> writer, List<GrainAddress> value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt32((uint)value.Count);
            foreach (var entry in value)
            {
                _activationAddressCodec.Serialize(entry, ref writer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string? ReadString<TInput>(ref Reader<TInput> reader)
        {
            var length = reader.ReadVarInt32();
            if (length <= 0)
            {
                if (length < 0)
                {
                    return null;
                }

                return string.Empty;
            }

            string result;
            if (reader.TryReadBytes(length, out var span))
            {
                result = Encoding.UTF8.GetString(span);
            }
            else
            {
                var bytes = reader.ReadBytes((uint)length);
                result = Encoding.UTF8.GetString(bytes);
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteString<TBufferWriter>(ref Writer<TBufferWriter> writer, string value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                writer.WriteVarInt32(-1);
                return;
            }

            var numBytes = Encoding.UTF8.GetByteCount(value);
            writer.WriteVarInt32(numBytes);
            if (numBytes < 512)
            {
                writer.EnsureContiguous(numBytes);
            }

            var currentSpan = writer.WritableSpan;

            // If there is enough room in the current span for the encoded data,
            // then encode directly into the output buffer.
            if (numBytes <= currentSpan.Length)
            {
                Encoding.UTF8.GetBytes(value, currentSpan);
                writer.AdvanceSpan(numBytes);
            }
            else
            {
                // Note: there is room for optimization here.
                Span<byte> bytes = Encoding.UTF8.GetBytes(value);
                writer.Write(bytes);
            }
        }

        public void WriteRequestContext<TBufferWriter>(ref Writer<TBufferWriter> writer, Dictionary<string, object> value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt32((uint)value.Count);
            foreach (var entry in value)
            {
                WriteString(ref writer, entry.Key);
                _serializer.Serialize(entry.Value, ref writer);
            }
        }

        public Dictionary<string, object> ReadRequestContext<TInput>(ref Reader<TInput> reader)
        {
            var size = (int)reader.ReadVarUInt32();
            var result = new Dictionary<string, object>(size);
            for (var i = 0; i < size; i++)
            {
                var key = ReadString(ref reader);
                var value = _serializer.Deserialize<object, TInput>(ref reader);

                Debug.Assert(key is not null);
                result.Add(key, value);
            }

            return result;
        }

        private GrainId ReadGrainId<TInput>(ref Reader<TInput> reader)
        {
            var grainType = _readerIdSpanCachingCodec.ReadRaw(ref reader);
            var grainKey = IdSpanCodec.ReadRaw(ref reader);
            return new GrainId(new GrainType(grainType), grainKey);
        }

        private void WriteGrainId<TBufferWriter>(ref Writer<TBufferWriter> writer, GrainId value) where TBufferWriter : IBufferWriter<byte>
        {
            _writerIdSpanCachingCodec.WriteRaw(ref writer, value.Type.Value);
            IdSpanCodec.WriteRaw(ref writer, value.Key);
        }

        private static ActivationId ReadActivationId<TInput>(ref Reader<TInput> reader)
        {
            if (reader.ReadByte() == 0)
            {
                return default;
            }

            if (reader.TryReadBytes(16, out var readOnly))
            {
                return new(new Guid(readOnly));
            }

            Span<byte> bytes = stackalloc byte[16];
            for (var i = 0; i < 16; i++)
            {
                bytes[i] = reader.ReadByte();
            }

            return new(new Guid(bytes));
        }

        private static void WriteActivationId<TBufferWriter>(ref Writer<TBufferWriter> writer, ActivationId value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value.IsDefault)
            {
                writer.WriteByte(0);
                return;
            }

            writer.WriteByte(1);
            writer.EnsureContiguous(16);
            if (value.Key.TryWriteBytes(writer.WritableSpan))
            {
                writer.AdvanceSpan(16);
                return;
            }

            writer.Write(value.Key.ToByteArray());
        }
    }
}
