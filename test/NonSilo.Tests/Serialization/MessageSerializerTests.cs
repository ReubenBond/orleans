using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Buffers;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Serialization
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class MessageSerializerTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;
        private readonly MessageFactory messageFactory;
        private readonly MessageSerializer messageSerializer;
        private readonly MessageHandlerShared messageHandlerShared;

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
            this.messageSerializer = this.fixture.Services.GetRequiredService<MessageSerializer>();
            this.messageHandlerShared = this.fixture.Services.GetRequiredService<MessageHandlerShared>();
        }

        [Fact, TestCategory("Functional")]
        public async Task MessageTest_TtlUpdatedOnAccess()
        {
            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(-1000), TimeSpan.FromMilliseconds(900));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnSerialization()
        {
            var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var deserializedMessage = RoundTripMessage(message);

            Assert.NotNull(deserializedMessage.TimeToLive);
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(-1000), TimeSpan.FromMilliseconds(900));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_Serialize_RoundTrip_Buffer()
        {
            for (var i = 0; i < 10; i++)
            {
                var writeBuffer = new PooledBuffer();
                var readBuffer = new PooledBuffer();
                try
                {
                    var message = CreateTestMessage();
                    var (headerLength, bodyLength) = this.messageSerializer.Write(ref writeBuffer, message);

                    writeBuffer.CopyTo(ref readBuffer);
                    var bufferSlice = readBuffer.Slice();
                    this.messageSerializer.Read(in bufferSlice, headerLength, bodyLength, out var deserializedMessage);

                    CheckMessage(message, deserializedMessage);
                }
                finally
                {
                    writeBuffer.Dispose();
                    readBuffer.Dispose();
                    RequestContext.Clear();
                }
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_Serialize_RoundTrip_Request()
        {
            for (var i = 0; i < 10; i++)
            {
                var writeRequest = this.messageHandlerShared.GetSendMessageHandler();
                var message = CreateTestMessage();
                writeRequest.Initialize(message);

                var readRequest = messageHandlerShared.GetReceiveMessageHandler();

                var writeBuffers = writeRequest.Buffers;
                int writeLength;
                do
                {
                    writeLength = (int)Math.Min(writeBuffers.Length, readRequest.Buffer.Length);
                    writeBuffers.Slice(0, writeLength).CopyTo(readRequest.Buffer.Span);
                    writeBuffers = writeBuffers.Slice(writeLength);
                } while (!readRequest.OnProgress(writeLength));

                var deserializedMessage = readRequest.TestReadMessage();
                CheckMessage(message, deserializedMessage);
            }
        }

        private static void CheckMessage(Message message, Message deserializedMessage)
        {
            Assert.Equal(message.Id, deserializedMessage.Id);
            Assert.Equal(message.BodyObject, deserializedMessage.BodyObject);
            Assert.Equal(message.SendingGrain, deserializedMessage.SendingGrain);
            Assert.Equal(message.SendingSilo, deserializedMessage.SendingSilo);
            Assert.Equal(message.TargetGrain, deserializedMessage.TargetGrain);
            Assert.Equal(message.TargetSilo, deserializedMessage.TargetSilo);
            Assert.Equal(message.CacheInvalidationHeader.Count, deserializedMessage.CacheInvalidationHeader.Count);
            Assert.Equal(message.ForwardCount, deserializedMessage.ForwardCount);
            Assert.Equal(message.Direction, deserializedMessage.Direction);
            Assert.Equal(message.InterfaceType, deserializedMessage.InterfaceType);
            Assert.Equal(message.InterfaceVersion, deserializedMessage.InterfaceVersion);
            foreach (var header in message.CacheInvalidationHeader)
            {
                Assert.Contains(header, deserializedMessage.CacheInvalidationHeader);
            }

            Assert.Equal(message.BodyObject, deserializedMessage.BodyObject);
        }

        private Message CreateTestMessage()
        {
            try
            {
                RequestContext.Set("fancy_feet", "yes");
                var message = this.messageFactory.CreateMessage("ladida", InvokeMethodOptions.None);
                message.SendingGrain = GrainId.Create("test", "foo");
                message.TargetGrain = GrainId.Create("test2", "foo2");
                message.SendingSilo = SiloAddress.New(IPAddress.Loopback, 12345, 543212345);
                message.TargetSilo = SiloAddress.New(IPAddress.Parse("100.200.1.2"), 12345, 543212345);
                message.CacheInvalidationHeader = new()
                {
                    new GrainAddress
                    {
                        GrainId = GrainId.Create("test", "foo"),
                        ActivationId = ActivationId.NewId(),
                        SiloAddress = SiloAddress.New(IPAddress.Parse("1.2.3.4"), 8285, 11)
                    },

                    new GrainAddress
                    {
                        GrainId = GrainId.Create("cow", "gertrude"),
                        ActivationId = ActivationId.NewId(),
                        SiloAddress = SiloAddress.New(IPAddress.Parse("2.2.2.22"), 1, 123456)
                    }
                };
                return message;
            }
            finally
            {
                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_SerializeHeaderTooBig()
        {
            var buffer = new PooledBuffer();
            try
            {
                // Create a ridiculously big RequestContext
                var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;
                RequestContext.Set("big_object", new byte[maxHeaderSize + 1]);

                var message = this.messageFactory.CreateMessage(null, InvokeMethodOptions.None);

                Assert.Throws<OrleansException>(() => this.messageSerializer.Write(ref buffer, message));
            }
            finally
            {
                buffer.Dispose();
                RequestContext.Clear();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_SerializeBodyTooBig()
        {
            var buffer = new PooledBuffer();
            try
            {
                var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;

                // Create a request with a ridiculously big argument
                var arg = new byte[maxBodySize + 1];
                var request = new[] { arg };
                var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

                Assert.Throws<OrleansException>(() => this.messageSerializer.Write(ref buffer, message));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeHeaderTooBig()
        {
            var maxSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;

            DeserializeFakeMessage(maxSize + 1, 0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeBodyTooBig()
        {
            var maxSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageHeaderSize;

            DeserializeFakeMessage(0, maxSize + 1);
        }

        private void DeserializeFakeMessage(int headerSize, int bodySize)
        {
            var buffer = new PooledBuffer();

            try
            {
                Span<byte> lengthFields = stackalloc byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(lengthFields, headerSize);
                BinaryPrimitives.WriteInt32LittleEndian(lengthFields.Slice(4), bodySize);
                buffer.Write(lengthFields);

                var reader = buffer.Slice(0);
                Assert.Throws<OrleansException>(() => this.messageSerializer.Read(in reader, headerSize, bodySize, out var message));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        private Message RoundTripMessage(Message message)
        {
            var buffer = new PooledBuffer();

            try
            {
                var (headerSize, bodySize) = this.messageSerializer.Write(ref buffer, message);

                var reader = buffer.Slice(0);
                this.messageSerializer.Read(in reader, headerSize, bodySize, out var deserializedMessage);
                return deserializedMessage;
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }
}
