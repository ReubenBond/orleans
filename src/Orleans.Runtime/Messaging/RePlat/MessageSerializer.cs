using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Orleans.Serialization;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageSerializer : IMessageSerializer
    {
        private readonly OrleansSerializer<Message.HeadersContainer> messageHeadersSerializer;
        private readonly OrleansSerializer<object> objectSerializer;

        public MessageSerializer(OrleansSerializer<Message.HeadersContainer> headersSerializer, OrleansSerializer<object> objectSerializer)
        {
            this.messageHeadersSerializer = headersSerializer;
            this.objectSerializer = objectSerializer;
        }

        public int TryRead(ref ReadOnlySequence<byte> input, out Message message)
        {
            if (input.Length < 8)
            {
                message = default;
                return 8;
            }

            (int, int) ReadLengths(ReadOnlySequence<byte> b)
            {
                Span<byte> lengthBytes = stackalloc byte[8];
                b.Slice(0, 8).CopyTo(lengthBytes);
                return (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes), BinaryPrimitives.ReadInt32LittleEndian(lengthBytes.Slice(4)));
            }

            var (headerLength, bodyLength) = ReadLengths(input);

            var requiredBytes = headerLength + bodyLength;
            if (input.Length < requiredBytes)
            {
                message = default;
                return requiredBytes;
            }

            // decode header
            var header = input.Slice(Message.LENGTH_HEADER_SIZE, headerLength);

            // decode body
            int bodyOffset = Message.LENGTH_HEADER_SIZE + headerLength;
            var body = input.Slice(bodyOffset, bodyLength);

            // build message
            this.messageHeadersSerializer.Deserialize(header, out var headersContainer);
            message = new Message
            {
                Headers = headersContainer
            };
            this.objectSerializer.Deserialize(body, out var bodyObject);
            message.BodyObject = bodyObject;

            input = input.Slice(Message.LENGTH_HEADER_SIZE + requiredBytes);
            return 0;
        }

        public void Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>
        {
            var data = new List<ArraySegment<byte>>();
            var lengthFields = new byte[2 * sizeof(int)];
            data.Add(new ArraySegment<byte>(lengthFields, 0, 2 * sizeof(int)));
            using (var buffer = new ArrayBufferWriter())
            {
                this.messageHeadersSerializer.Serialize(buffer, message.Headers);
                var headerLength = buffer.CommitedByteCount;

                this.objectSerializer.Serialize(buffer, message.BodyObject);
                var bodyLength = buffer.CommitedByteCount - headerLength;

                data.Add(new ArraySegment<byte>(buffer.ToArray()));

                // Write length prefixes, first header length then body length.
                var lengthPrefixes = MemoryMarshal.Cast<byte, int>(lengthFields);
                lengthPrefixes[0] = headerLength;
                lengthPrefixes[1] = bodyLength;

                foreach (var segment in data)
                {
                    writer.Write(segment);
                }
            }
        }
    }
}
