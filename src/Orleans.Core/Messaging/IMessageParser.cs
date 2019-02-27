using System.Buffers;

namespace Orleans.Runtime
{
    internal interface IMessageParser
    {
        bool TryParseMessage(ref ReadOnlySequence<byte> buffer, out Message message);
        void FormatMessage<TWriter>(ref TWriter writer, Message message) where TWriter : IBufferWriter<byte>;
    }
}
