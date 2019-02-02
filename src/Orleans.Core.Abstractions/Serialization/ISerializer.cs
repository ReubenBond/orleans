using System.Buffers;

namespace Orleans.Serialization
{
    /// <summary>
    /// Functionality for serializing objects of type <typeparamref name="T"/> and (if applicable) descendant types.
    /// </summary>
    /// <typeparam name="T">The base type which this serializer is able to serialize.</typeparam>
    public interface ISerializer<T>
    {
        /// <summary>
        /// Serializes the provided value into the output buffer writer.
        /// </summary>
        /// <typeparam name="TBufferWriter"></typeparam>
        /// <param name="output">The output buffer writer.</param>
        /// <param name="value">The value being serialized.</param>
        void Serialize<TBufferWriter>(TBufferWriter output, T value) where TBufferWriter : IBufferWriter<byte>;
        
        /// <summary>
        /// Deserializes a value from the provided input buffer.
        /// </summary>
        /// <param name="input">The input buffer.</param>
        /// <param name="value">The deserialized value.</param>
        void Deserialize(ReadOnlySequence<byte> input, out T value);
    }
}
