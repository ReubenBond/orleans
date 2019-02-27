using Orleans;
using Orleans.Serialization;
using Xunit;

namespace Tester.SerializationTests
{
    public class SerializationTestsUtils
    {
        public static void VerifyUsingFallbackSerializer(SerializationManager serializationManager, object ob)
        {
            using (var buffer = new ArrayBufferWriter())
            {
                var writer = new BinaryTokenStreamWriter2<ArrayBufferWriter>(buffer);
                var context = new SerializationContext(serializationManager)
                {
                    StreamWriter = writer
                };
                serializationManager.FallbackSerializer(ob, context, ob.GetType());
                writer.Commit();

                var reader = new BinaryTokenStreamReader(buffer.Formatted);
                var serToken = reader.ReadToken();
                Assert.Equal(SerializationTokenType.Fallback, serToken);
            }
        }
    }
}
