using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    public static class DeserializationContextExtensions
    {
        /// <summary>
        /// Returns a new nested context which begins at the specified position.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="position"></param>
        /// <param name="reader"></param>
        /// <returns></returns>
        public static IDeserializationContext CreateNestedContext(
            this IDeserializationContext context,
            int position,
            BinaryTokenStreamReader reader)
        {
            return new DeserializationContext.NestedDeserializationContext(context, position, reader);
        }
    }

    public sealed class DeserializationContext : SerializationContextBase, IDeserializationContext
    {
        private readonly Dictionary<int, object> taggedObjects;

        public DeserializationContext(SerializationManager serializationManager)
            : base(serializationManager)
        {
            this.taggedObjects = new Dictionary<int, object>();
        }

        /// <inheritdoc />
        /// <inheritdoc />
        public IBinaryTokenStreamReader StreamReader { get; set; }

        /// <inheritdoc />
        public int CurrentObjectOffset { get; set; }

        public int CurrentPosition => this.StreamReader.CurrentPosition;

        /// <inheritdoc />
        public void RecordObject(object obj)
        {
            this.RecordObject(obj, this.CurrentObjectOffset);
        }

        /// <inheritdoc />
        public void RecordObject(object obj, int offset)
        {
            taggedObjects[offset] = obj;
        }

        /// <inheritdoc />
        public object FetchReferencedObject(int offset)
        {
            object result;
            if (!taggedObjects.TryGetValue(offset, out result))
            {
                throw new SerializationException("Reference with no referred object");
            }
            return result;
        }

        internal void Reset()
        {
            this.taggedObjects.Clear();
            this.CurrentObjectOffset = 0;
        }

        public override object AdditionalContext => this.SerializationManager.RuntimeClient;

        public object DeserializeInner(Type expected)
        {
            return SerializationManager.DeserializeInner(expected, this);
        }

        internal class NestedDeserializationContext : IDeserializationContext
        {
            private readonly IDeserializationContext parent;
            private readonly int position;

            /// <summary>
            /// Initializes a new <see cref="NestedDeserializationContext"/> instance.
            /// </summary>
            /// <param name="parent"></param>
            /// <param name="position">The position, relative to the outer-most context, at which this context begins.</param>
            /// <param name="reader"></param>
            public NestedDeserializationContext(IDeserializationContext parent, int position, BinaryTokenStreamReader reader)
            {
                this.position = position;
                this.parent = parent;
                this.StreamReader = reader;
                this.CurrentObjectOffset = this.parent.CurrentObjectOffset;
            }
            
            public IServiceProvider ServiceProvider => this.parent.ServiceProvider;
            public object AdditionalContext => this.parent.AdditionalContext;
            public IBinaryTokenStreamReader StreamReader { get; }
            public int CurrentObjectOffset { get; set; }
            public int CurrentPosition => this.position + this.StreamReader.CurrentPosition;
            public void RecordObject(object obj, int offset) => this.parent.RecordObject(obj, offset);
            public void RecordObject(object obj) => this.RecordObject(obj, this.CurrentObjectOffset);
            public object FetchReferencedObject(int offset) => this.parent.FetchReferencedObject(offset);
            public object DeserializeInner(Type expected) => SerializationManager.DeserializeInner(expected, this);
        }
    }

    internal sealed class ReferencedTypeCollection
    {
        private readonly struct ReferencePair
        {
            public ReferencePair(uint id, Type type)
            {
                this.Id = id;
                this.Type = type;
            }

            public uint Id { get; }

            public Type Type { get; }
        }

        private int referenceToTypeCount;
        private ReferencePair[] referenceToType = new ReferencePair[64];

        private int typeToReferenceCount;
        private ReferencePair[] typeToReference = new ReferencePair[64];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetReferencedType(uint reference, out Type value)
        {
            // Reference 0 is always null.
            if (reference == 0)
            {
                value = null;
                return true;
            }

            for (int i = 0; i < this.referenceToTypeCount; ++i)
            {
                if (this.referenceToType[i].Id == reference)
                {
                    value = this.referenceToType[i].Type;
                    return true;
                }
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkValueField() => ++this.CurrentReferenceId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetReference(Type value, out uint reference)
        {
            // TODO: Binary search
            for (int i = 0; i < this.typeToReferenceCount; ++i)
            {
                if (value.Equals(this.typeToReference[i].Type))
                {
                    reference = this.typeToReference[i].Id;
                    return true;
                }
            }

            reference = 0;
            return false;
        }

        private void AddToReferenceToIdMap(Type value, uint reference)
        {
            if (this.typeToReferenceCount >= this.typeToReference.Length)
            {
                var old = this.typeToReference;
                this.typeToReference = new ReferencePair[this.typeToReference.Length * 2];
                Array.Copy(old, this.typeToReference, this.typeToReferenceCount);
            }

            this.typeToReference[this.typeToReferenceCount++] = new ReferencePair(reference, value);
        }

        private void AddToReferences(Type value, uint reference)
        {
            if (this.referenceToTypeCount >= this.referenceToType.Length)
            {
                var old = this.referenceToType;
                this.referenceToType = new ReferencePair[this.referenceToType.Length * 2];
                Array.Copy(old, this.referenceToType, this.referenceToTypeCount);
            }

            this.referenceToType[this.referenceToTypeCount++] = new ReferencePair(reference, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordTypeWhileSerializing(Type value) => this.AddToReferenceToIdMap(value, ++this.CurrentReferenceId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RecordTypeWhileDeserializing(Type value) => this.AddToReferences(value, ++this.CurrentReferenceId);

        public Dictionary<uint, Type> CopyReferenceTable() => this.referenceToType.Take(this.referenceToTypeCount).ToDictionary(r => r.Id, r => r.Type);
        public Dictionary<Type, uint> CopyIdTable() => this.typeToReference.Take(this.typeToReferenceCount).ToDictionary(r => r.Type, r => r.Id);

        public uint CurrentReferenceId { get; set; }

        public void Reset()
        {
            for (var i = 0; i < this.referenceToTypeCount; i++)
            {
                this.referenceToType[i] = default;
            }
            for (var i = 0; i < this.typeToReferenceCount; i++)
            {
                this.typeToReference[i] = default;
            }
            this.referenceToTypeCount = 0;
            this.typeToReferenceCount = 0;
            this.CurrentReferenceId = 0;
        }
    }
}
