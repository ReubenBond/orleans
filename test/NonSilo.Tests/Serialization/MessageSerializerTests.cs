using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
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
        private readonly ISerializer<Message.HeadersContainer> messageHeadersSerializer;
        private readonly ISerializer<object> objectSerializer;

        public MessageSerializerTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.messageFactory = this.fixture.Services.GetRequiredService<MessageFactory>();
            this.messageHeadersSerializer = this.fixture.Services.GetRequiredService<ISerializer<Message.HeadersContainer>>();
            this.objectSerializer = this.fixture.Services.GetRequiredService<ISerializer<object>>();
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void MessageTest_BinaryRoundTrip()
        {
            RunTest(1000);
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnAccess()
        {
            var request = new InvokeMethodRequest(0, 0, 0, null);
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.InRange(message.TimeToLive.Value, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        }

        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public async Task MessageTest_TtlUpdatedOnSerialization()
        {
            var request = new InvokeMethodRequest(0, 0, 0, null);
            var message = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);

            message.TimeToLive = TimeSpan.FromSeconds(1);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var deserializedMessage = RoundTripMessage(message);

            Assert.NotNull(deserializedMessage.TimeToLive);
            Assert.InRange(deserializedMessage.TimeToLive.Value, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(500));
        }

        private Message RoundTripMessage(Message message)
        {
            Message deserializedMessage;
            var segments = new List<ArraySegment<byte>>(4);
            var lengthFields = new byte[2 * sizeof(int)];
            segments.Add(new ArraySegment<byte>(lengthFields, 0, lengthFields.Length));
            using (var buffer = new MultiSegmentBufferWriter(maxAllocationSize: 9192, bufferList: segments))
            {
                this.messageHeadersSerializer.Serialize(buffer, message.Headers);
                var headerLength = buffer.CommitedByteCount;
                this.objectSerializer.Serialize(buffer, message.BodyObject);
                var bodyLength = buffer.CommitedByteCount - headerLength;

                void WriteLengthPrefixes()
                {
                    var lengthPrefixes = MemoryMarshal.Cast<byte, int>(lengthFields);
                    lengthPrefixes[0] = headerLength;
                    lengthPrefixes[1] = bodyLength;
                }

                WriteLengthPrefixes();

                byte[] data = new byte[buffer.CommitedByteCount];
                int n = 0;
                foreach (var b in buffer.Committed)
                {
                    Array.Copy(b.Array, b.Offset, data, n, b.Count);
                    n += b.Count;
                }

                deserializedMessage = DeserializeMessage(buffer.CommitedByteCount, data);
            }

            return deserializedMessage;
        }

        private void RunTest(int numItems)
        {
            InvokeMethodRequest request = new InvokeMethodRequest(0, 2, 0, null);
            Message resp = this.messageFactory.CreateMessage(request, InvokeMethodOptions.None);
            resp.Id = new CorrelationId();
            resp.SendingSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 200), 0);
            resp.TargetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 300), 0);
            resp.SendingGrain = GrainId.NewId();
            resp.TargetGrain = GrainId.NewId();
            resp.IsAlwaysInterleave = true;
            Assert.True(resp.IsUsingInterfaceVersions);

            List<object> requestBody = new List<object>();
            for (int k = 0; k < numItems; k++)
            {
                requestBody.Add(k + ": test line");
            }

            resp.BodyObject = requestBody;

            string s = resp.ToString();
            output.WriteLine(s);
            
            var resp1 = RoundTripMessage(resp);

            //byte[] serialized = resp.FormatForSending();
            //Message resp1 = new Message(serialized, serialized.Length);
            Assert.Equal(resp.Category, resp1.Category); //Category is incorrect"
            Assert.Equal(resp.Direction, resp1.Direction); //Direction is incorrect
            Assert.Equal(resp.Id, resp1.Id); //Correlation ID is incorrect
            Assert.Equal(resp.IsAlwaysInterleave, resp1.IsAlwaysInterleave); //Foo Boolean is incorrect
            Assert.Equal(resp.CacheInvalidationHeader, resp1.CacheInvalidationHeader); //Bar string is incorrect
            Assert.True(resp.TargetSilo.Equals(resp1.TargetSilo));
            Assert.True(resp.TargetGrain.Equals(resp1.TargetGrain));
            Assert.True(resp.SendingGrain.Equals(resp1.SendingGrain));
            Assert.True(resp.SendingSilo.Equals(resp1.SendingSilo)); //SendingSilo is incorrect
            Assert.True(resp1.IsUsingInterfaceVersions);
            List<object> responseList = Assert.IsAssignableFrom<List<object>>(resp1.GetDeserializedBody(this.fixture.SerializationManager));
            Assert.Equal<int>(numItems, responseList.Count); //Body list has wrong number of entries
            for (int k = 0; k < numItems; k++)
            {
                Assert.IsAssignableFrom<string>(responseList[k]); //Body list item " + k + " has wrong type
                Assert.Equal((string)(requestBody[k]), (string)(responseList[k])); //Body list item " + k + " is incorrect
            }
        }

        private Message DeserializeMessage(int length, byte[] data)
        {
            int headerLength = BitConverter.ToInt32(data, 0);
            int bodyLength = BitConverter.ToInt32(data, 4);
            Assert.Equal<int>(length, headerLength + bodyLength + 8); //Serialized lengths are incorrect
            byte[] header = new byte[headerLength];
            Array.Copy(data, 8, header, 0, headerLength);
            byte[] body = new byte[bodyLength];
            Array.Copy(data, 8 + headerLength, body, 0, bodyLength);

            this.messageHeadersSerializer.Deserialize(new ReadOnlySequence<byte>(header), out var headersContainer);
            this.objectSerializer.Deserialize(new ReadOnlySequence<byte>(body), out var bodyObject);
            
            return new Message
            {
                Headers = headersContainer,
                BodyObject = bodyObject
            };
        }
    }
}
