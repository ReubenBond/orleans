using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Orleans.Networking.Transport;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;

namespace Orleans.Runtime.Messaging
{
    [GenerateSerializer, Immutable]
    internal sealed class ConnectionPreamble
    {
        [Id(0)]
        public NetworkProtocolVersion NetworkProtocolVersion { get; init; }

        [Id(1)]
        public GrainId NodeIdentity { get; init; }

        [Id(2)]
        public SiloAddress SiloAddress { get; init; }

        [Id(3)]
        public string ClusterId { get; init; }
    }

    internal sealed class ConnectionPreambleHelper
    {
        private const int MaxPreambleLength = 1024;
        private readonly Serializer<ConnectionPreamble> _preambleSerializer;
        private readonly SerializerSessionPool _serializerSessionPool;

        public ConnectionPreambleHelper(Serializer<ConnectionPreamble> preambleSerializer, SerializerSessionPool serializerSessionPool)
        {
            _preambleSerializer = preambleSerializer;
            _serializerSessionPool = serializerSessionPool;
        }

        internal async ValueTask Write(MessageTransport transport, ConnectionPreamble preamble)
        {
            using var writeRequest = PreambleWriteRequest.Create(preamble, _preambleSerializer, _serializerSessionPool);
            if (!transport.WriteAsync(writeRequest))
            {
                throw new ConnectionAbortedException();
            }

            await writeRequest.Completion;

            return;
        }

        internal async ValueTask<ConnectionPreamble> Read(MessageTransport transport)
        {
            using var readRequest = PreambleReadRequest.Create(_preambleSerializer);
            if (!transport.ReadAsync(readRequest))
            {
                throw new ConnectionAbortedException();
            }

            var result = await readRequest.Completion;
            return result;
        }

        private sealed class PreambleWriteRequest : WriteRequest, IDisposable
        {
            private readonly TaskCompletionSource _completion = new();
            private PooledBuffer _buffer;

            private PreambleWriteRequest(PooledBuffer buffer)
            {
                IsSingleBuffer = false;
                _buffer = buffer;
            }

            public static PreambleWriteRequest Create(ConnectionPreamble preamble, Serializer<ConnectionPreamble> preambleSerializer, SerializerSessionPool serializerSessionPool)
            {
                // Reserve space for framing
                var buffer = new PooledBuffer();
                var framingBytes = buffer.GetSpan(sizeof(int));
                buffer.Advance(sizeof(int));

                // Serialize the preamble.
                using var session = serializerSessionPool.GetSession();
                var writer = Writer.Create(buffer, session);
                preambleSerializer.Serialize(preamble, ref writer);

                // Write framing
                var length = writer.Position;
                BinaryPrimitives.WriteInt32LittleEndian(framingBytes, length);

                if (length > MaxPreambleLength)
                {
                    throw new InvalidOperationException($"Created preamble of length {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
                }

                return new(writer.Output);
            }

            public void SetPreamble(in PooledBuffer buffer) => _buffer = buffer;

            public override ReadOnlyMemory<byte> Buffer => throw new NotImplementedException();
            public override ReadOnlySequence<byte> Buffers => _buffer.AsReadOnlySequence();

            public override void OnCompleted() => _completion.SetResult();
            public override void OnError(Exception error) => _completion.SetException(error);

            public void Dispose() => _buffer.Reset();

            public Task Completion => _completion.Task;
        }

        private sealed class PreambleReadRequest : ReadRequest, IDisposable
        {
            private readonly Serializer<ConnectionPreamble> _preambleSerializer;
            private readonly TaskCompletionSource<ConnectionPreamble> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private byte[] _preambleBuffer = new byte[MaxPreambleLength + sizeof(int)];
            private int _totalBytesRead;
            private int _preambleLength = -1;
            private Memory<byte> _buffer;

            private PreambleReadRequest(Serializer<ConnectionPreamble> preambleSerializer)
            {
                _preambleSerializer = preambleSerializer;

                // The initial read must be precisely the framing size to prevent over-reading.
                _buffer = _preambleBuffer.AsMemory(0, sizeof(int));
            }

            public override Memory<byte> Buffer => _buffer;
            public Task<ConnectionPreamble> Completion => _completion.Task;

            public static PreambleReadRequest Create(Serializer<ConnectionPreamble> preambleSerializer) => new (preambleSerializer);

            public void Dispose() { }
            public override void OnError(Exception error) => _completion.SetException(error);
            public override bool OnProgress(int bytesRead)
            {
                _totalBytesRead += bytesRead;

                if (_totalBytesRead < sizeof(int))
                {
                    _buffer = _buffer[bytesRead..];
                    return false;
                }

                if (_preambleLength < 0)
                {
                    _preambleLength = BinaryPrimitives.ReadInt32LittleEndian(_preambleBuffer.AsSpan(0, sizeof(int)));

                    if (_preambleLength > MaxPreambleLength)
                    {
                        throw new InvalidOperationException($"Read preamble length of {_preambleLength}, which is greater than maximum allowed size of {MaxPreambleLength}.");
                    }

                    if (_preambleLength <= 0)
                    {
                        throw new InvalidOperationException($"Read preamble length of {_preambleLength}, which is less than or equal to zero.");
                    }

                    // Limit the maximum amount of data which can be read to the specified preamble length.
                    _buffer = _preambleBuffer.AsMemory(sizeof(int), _preambleLength);
                }

                if (_totalBytesRead == _preambleLength + sizeof(int))
                {
                    var payload = _preambleBuffer.AsMemory(sizeof(int), _preambleLength);
                    var preamble = _preambleSerializer.Deserialize(payload);
                    _completion.SetResult(preamble);

                    return true;
                }

                _buffer = _buffer[bytesRead..];
                return false;
            }
        }
    }
}
