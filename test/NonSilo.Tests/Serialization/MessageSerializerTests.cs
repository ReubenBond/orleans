using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Serialization.Buffers.Adaptors;
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

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
            this.messageSerializer = this.fixture.Services.GetRequiredService<MessageSerializer>();
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
        public void Message_SerializeHeaderTooBig()
        {
            var buffer = new PooledBuffer();
            try
            {
                // Create a ridiculously big RequestContext
                var maxHeaderSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageSize;
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
                var maxBodySize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageSize;

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
            var maxSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageSize;

            DeserializeFakeMessage(maxSize + 1, 0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Message_DeserializeBodyTooBig()
        {
            var maxSize = this.fixture.Services.GetService<IOptions<SiloMessagingOptions>>().Value.MaxMessageSize;

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
                Assert.Throws<OrleansException>(() => this.messageSerializer.Read(in reader, out var message));
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
                this.messageSerializer.Write(ref buffer, message);

                var reader = buffer.Slice(0);
                this.messageSerializer.Read(in reader, out var deserializedMessage);
                return deserializedMessage;
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }
}
