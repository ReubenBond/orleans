using Orleans.Serialization.Buffers;
using System.Buffers;

namespace Orleans.Serialization.Serializers
{
    public delegate void ValueReader<TValue, TInput>(ref Reader<TInput> reader, scoped ref TValue value);
    public delegate void ValueWriter<TValue, TOutput>(ref Writer<TOutput> reader, scoped ref TValue value) where TOutput : IBufferWriter<byte>;

    /// <summary>
    /// Functionality for serializing a value type.
    /// </summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <typeparam name="TOutput">The buffer writer type.</typeparam>
    public interface IValueEncoder<TValue, TOutput> where TValue : struct where TOutput : IBufferWriter<byte>
    {
        /// <summary>
        /// Serializes the provided value.
        /// </summary>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        void Serialize(ref Writer<TOutput> writer, scoped ref TValue value);
    }

    /// <summary>
    /// Functionality for serializing a value type.
    /// </summary>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <typeparam name="TInput">The buffer type.</typeparam>
    public interface IValueDecoder<TValue, TInput> where TValue : struct
    {
        /// <summary>
        /// Deserializes the specified type.
        /// </summary>
        /// <param name="reader">The reader.</param>
        /// <param name="value">The value.</param>
        void Deserialize(ref Reader<TInput> reader, scoped ref TValue value);
    }

    /// <summary>
    /// Functionality for serializing a value type.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    public interface IValueSerializer<T> : IValueSerializer where T : struct
    {
        /// <summary>
        /// Serializes the provided value.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        void Serialize<TBufferWriter>(ref Writer<TBufferWriter> writer, scoped ref T value) where TBufferWriter : IBufferWriter<byte>;

        /// <summary>
        /// Deserializes the specified type.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="value">The value.</param>
        void Deserialize<TInput>(ref Reader<TInput> reader, scoped ref T value);
    }

    /// <summary>
    /// Marker interface for value type serializers.
    /// </summary>
    public interface IValueSerializer
    {
    }
}