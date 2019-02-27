using System;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Utilities;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization
{
    internal class ArrayBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private ResizableArray<byte> buffer;

        public ArrayBufferWriter(int capacity = 1024)
        {
            this.buffer = new ResizableArray<byte>(ArrayPool<byte>.Shared.Rent(capacity));
        }

        public int CommitedByteCount => this.buffer.Count;

        public void Clear()
        {
            this.buffer.Count = 0;
        }

        public ArraySegment<byte> Free => this.buffer.Free;

        public ArraySegment<byte> Formatted => this.buffer.Full;

        public byte[] ToArray() => this.Formatted.AsMemory().ToArray();

        public Memory<byte> GetMemory(int minimumLength = 0)
        {
            if (minimumLength < 1)
            {
                minimumLength = 1;
            }

            if (minimumLength > this.buffer.FreeCount)
            {
                int doubleCount = this.buffer.FreeCount * 2;
                int newSize = minimumLength > doubleCount ? minimumLength : doubleCount;
                byte[] newArray = ArrayPool<byte>.Shared.Rent(newSize + this.buffer.Count);
                byte[] oldArray = this.buffer.Resize(newArray);
                ArrayPool<byte>.Shared.Return(oldArray);
            }

            return this.buffer.FreeMemory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<byte> GetSpan(int minimumLength = 0)
        {
            if (minimumLength < 1)
            {
                minimumLength = 1;
            }

            if (minimumLength > this.buffer.FreeCount)
            {
                int doubleCount = this.buffer.FreeCount * 2;
                int newSize = minimumLength > doubleCount ? minimumLength : doubleCount;
                byte[] newArray = ArrayPool<byte>.Shared.Rent(newSize + this.buffer.Count);
                byte[] oldArray = this.buffer.Resize(newArray);
                ArrayPool<byte>.Shared.Return(oldArray);
            }

            return this.buffer.FreeSpan;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int bytes)
        {
            this.buffer.Count += bytes;
            if (this.buffer.Count > this.buffer.Capacity)
            {
                throw new InvalidOperationException("More bytes commited than returned from FreeBuffer");
            }
        }

        public void Dispose()
        {
            byte[] array = this.buffer.Array;
            this.buffer.Array = null;
            ArrayPool<byte>.Shared.Return(array);
        }

        private struct ResizableArray<T>
        {
            public ResizableArray(T[] array, int count = 0)
            {
                this.Array = array;
                this.Count = count;
            }

            public T[] Array { get; set; }

            public int Count { get; set; }

            public int Capacity => this.Array.Length;

            public T[] Resize(T[] newArray)
            {
                T[] oldArray = this.Array;
                this.Array.AsSpan(0, this.Count).CopyTo(newArray);  // CopyTo will throw if newArray.Length < _count
                this.Array = newArray;
                return oldArray;
            }

            public ArraySegment<T> Full => new ArraySegment<T>(this.Array, 0, this.Count);

            public ArraySegment<T> Free => new ArraySegment<T>(this.Array, this.Count, this.Array.Length - this.Count);

            public Span<T> FreeSpan => new Span<T>(this.Array, this.Count, this.Array.Length - this.Count);

            public Memory<T> FreeMemory => new Memory<T>(this.Array, this.Count, this.Array.Length - this.Count);

            public int FreeCount => this.Array.Length - this.Count;
        }
    }

    [TestCategory("BVT"), TestCategory("Serialization")]
    public class ILBasedExceptionSerializerTests
    {
        private readonly ILSerializerGenerator serializerGenerator = new ILSerializerGenerator();
        private readonly SerializationTestEnvironment environment;

        public ILBasedExceptionSerializerTests()
        {
            this.environment = SerializationTestEnvironment.Initialize(null, typeof(ILBasedSerializer));
        }

        /// <summary>
        /// Tests that <see cref="ILBasedExceptionSerializer"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact]
        public void ExceptionSerializer_SimpleException()
        {
            // Throw an exception so that is has a stack trace.
            var expected = GetNewException();
            this.TestExceptionSerialization(expected);
        }

        private ILExceptionSerializerTestException TestExceptionSerialization(ILExceptionSerializerTestException expected)
        {
            var buffer = new ArrayBufferWriter();
            var writer = new BinaryTokenStreamWriter2<ArrayBufferWriter>(buffer);
            var context = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = writer
            };

            // Deep copies should be reference-equal.
            Assert.Equal(
                expected,
                SerializationManager.DeepCopyInner(expected, new SerializationContext(this.environment.SerializationManager)),
                ReferenceEqualsComparer.Instance);

            this.environment.SerializationManager.Serialize(expected, writer);
            writer.Commit();
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(buffer.ToArray())
            };

            var actual = (ILExceptionSerializerTestException) this.environment.SerializationManager.Deserialize(null, reader.StreamReader);
            Assert.Equal(expected.BaseField.Value, actual.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.SubClassField, actual.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.OtherField.Value, actual.OtherField.Value, StringComparer.Ordinal);

            // Check for referential equality in the two fields which happened to be reference-equals.
            Assert.Equal(actual.BaseField, actual.OtherField, ReferenceEqualsComparer.Instance);

            return actual;
        }

        /// <summary>
        /// Tests that <see cref="ILBasedExceptionSerializer"/> supports reference cycles.
        /// </summary>
        [Fact]
        public void ExceptionSerializer_ReferenceCycle()
        {
            // Throw an exception so that is has a stack trace.
            var expected = GetNewException();

            // Create a reference cycle at the top level.
            expected.SomeObject = expected;

            var actual = this.TestExceptionSerialization(expected);
            Assert.Equal(actual, actual.SomeObject);
        }

        /// <summary>
        /// Tests that <see cref="ILBasedExceptionSerializer"/> supports reference cycles.
        /// </summary>
        [Fact]
        public void ExceptionSerializer_NestedReferenceCycle()
        {
            // Throw an exception so that is has a stack trace.
            var exception = GetNewException();
            var expected = new Outer
            {
                SomeFunObject = exception.OtherField,
                Object = exception,
            };

            // Create a reference cycle.
            exception.SomeObject = expected;

            var buffer = new ArrayBufferWriter();
            var writer = new BinaryTokenStreamWriter2<ArrayBufferWriter>(buffer);
            var context = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = writer
            };
            
            this.environment.SerializationManager.Serialize(expected, context.StreamWriter);
            writer.Commit();
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(buffer.ToArray())
            };

            var actual = (Outer)this.environment.SerializationManager.Deserialize(null, reader.StreamReader);
            Assert.Equal(expected.Object.BaseField.Value, actual.Object.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.Object.SubClassField, actual.Object.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.Object.OtherField.Value, actual.Object.OtherField.Value, StringComparer.Ordinal);

            // Check for referential equality in the fields which happened to be reference-equals.
            Assert.Equal(actual.Object.BaseField, actual.Object.OtherField, ReferenceEqualsComparer.Instance);
            Assert.Equal(actual, actual.Object.SomeObject, ReferenceEqualsComparer.Instance);
            Assert.Equal(actual.SomeFunObject, actual.Object.OtherField, ReferenceEqualsComparer.Instance);
        }

        private static ILExceptionSerializerTestException GetNewException()
        {
            ILExceptionSerializerTestException expected;
            try
            {
                var baseField = new SomeFunObject
                {
                    Value = Guid.NewGuid().ToString()
                };
                var res = new ILExceptionSerializerTestException
                {
                    BaseField = baseField,
                    SubClassField = Guid.NewGuid().ToString(),
                    OtherField = baseField,
                };
                throw res;
            }
            catch (ILExceptionSerializerTestException exception)
            {
                expected = exception;
            }
            return expected;
        }

        /// <summary>
        /// Tests that <see cref="ILBasedExceptionSerializer"/> supports distinct field selection for serialization
        /// versus copy operations.
        /// </summary>
        [Fact]
        public void ExceptionSerializer_UnknownException()
        {
            var expected = GetNewException();

            var knowsException = new ILBasedExceptionSerializer(this.serializerGenerator, new TypeSerializer(new CachedTypeResolver()));

            var buffer = new ArrayBufferWriter();
            var writer = new BinaryTokenStreamWriter2<ArrayBufferWriter>(buffer);
            var context = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = writer
            };
            knowsException.Serialize(expected, context, null);
            writer.Commit();

            // Deep copies should be reference-equal.
            var copyContext = new SerializationContext(this.environment.SerializationManager);
            Assert.Equal(expected, knowsException.DeepCopy(expected, copyContext), ReferenceEqualsComparer.Instance);

            // Create a deserializer which doesn't know about the expected exception type.
            var reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(buffer.ToArray())
            };

            // Ensure that the deserialized object has the fallback type.
            var doesNotKnowException = new ILBasedExceptionSerializer(this.serializerGenerator, new TestTypeSerializer(new CachedTypeResolver()));
            var untypedActual = doesNotKnowException.Deserialize(null, reader);
            Assert.IsType<RemoteNonDeserializableException>(untypedActual);

            // Ensure that the original type name is preserved correctly.
            var actualDeserialized = (RemoteNonDeserializableException) untypedActual;
            Assert.Equal(RuntimeTypeNameFormatter.Format(typeof(ILExceptionSerializerTestException)), actualDeserialized.OriginalTypeName);

            // Re-serialize the deserialized object using the serializer which does not have access to the original type.
            var buffer2 = new ArrayBufferWriter();
            var writer2 = new BinaryTokenStreamWriter2<ArrayBufferWriter>(buffer2);
            context = new SerializationContext(this.environment.SerializationManager)
            {
                StreamWriter = writer2
            };
            doesNotKnowException.Serialize(untypedActual, context, null);
            writer2.Commit();

            reader = new DeserializationContext(this.environment.SerializationManager)
            {
                StreamReader = new BinaryTokenStreamReader(buffer2.ToArray())
            };

            // Deserialize the round-tripped object and verify that it has the original type and all properties are
            // correctly.
            untypedActual = knowsException.Deserialize(null, reader);
            Assert.IsType<ILExceptionSerializerTestException>(untypedActual);

            var actual = (ILExceptionSerializerTestException) untypedActual;
            Assert.Equal(expected.BaseField.Value, actual.BaseField.Value, StringComparer.Ordinal);
            Assert.Equal(expected.SubClassField, actual.SubClassField, StringComparer.Ordinal);
            Assert.Equal(expected.OtherField.Value, actual.OtherField.Value, StringComparer.Ordinal);

            // Check for referential equality in the two fields which happened to be reference-equals.
            Assert.Equal(actual.BaseField, actual.OtherField, ReferenceEqualsComparer.Instance);
        }

        private class Outer
        {
            public SomeFunObject SomeFunObject { get; set; }
            public ILExceptionSerializerTestException Object { get; set; }
        }

        private class SomeFunObject
        {
            public string Value { get; set; }
        }

        private class BaseException : Exception
        {
            public SomeFunObject BaseField { get; set; }
        }

        [Serializable]
        private class ILExceptionSerializerTestException : BaseException
        {
            public string SubClassField { get; set; }
            public SomeFunObject OtherField { get; set; }
            public object SomeObject { get; set; }
        }

        private class TestTypeSerializer : TypeSerializer
        {
            internal override Type GetTypeFromName(string assemblyQualifiedTypeName, bool throwOnError)
            {
                if (throwOnError) throw new TypeLoadException($"Type {assemblyQualifiedTypeName} could not be loaded");
                return null;
            }

            public TestTypeSerializer(ITypeResolver typeResolver) : base(typeResolver)
            {
            }
        }
    }
}