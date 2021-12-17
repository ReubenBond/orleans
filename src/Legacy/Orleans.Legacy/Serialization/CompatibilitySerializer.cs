using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace Orleans.Serialization
{
    public class CompatibilitySerializer : IExternalSerializer
    {
        private static readonly RuntimeTypeHandle IntPtrTypeHandle = typeof(IntPtr).TypeHandle;
        private static readonly RuntimeTypeHandle UIntPtrTypeHandle = typeof(UIntPtr).TypeHandle;
        private static readonly Type DelegateType = typeof(Delegate);
        private static readonly ConcurrentDictionary<Type, TypeSerializationMetadata> TypeMetadata = new();
        private readonly SerializationManager _serializationManager;

        public CompatibilitySerializer(SerializationManager serializationManager)
        {
            _serializationManager = serializationManager;
        }
        
        public bool IsSupportedType(Type type) => !type.IsAbstract && !type.IsInterface && !type.IsArray && !type.IsEnum && IsSupportedFieldType(type);

        public object DeepCopy(object source, ICopyContext context)
        {
            if (source is null) return null;

            var resultType = source.GetType();
            var typeInfo = GetSerializationMetadata(resultType);
            var copy = FormatterServices.GetUninitializedObject(resultType);
            context.RecordCopy(source, copy);

            foreach (var field in typeInfo.Fields)
            {
                var fieldValue = field.GetValue(source);
                if (!field.FieldType.IsOrleansShallowCopyable())
                {
                    fieldValue = SerializationManager.DeepCopyInner(fieldValue, context);
                }

                field.SetValue(copy, fieldValue);
            }

            return copy;
        }

        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            var itemType = item.GetType();
            var typeInfo = GetSerializationMetadata(itemType);
            var callbackArgs = new object[] { new StreamingContext(StreamingContextStates.All, context) };
            typeInfo.OnSerializing?.Invoke(item, callbackArgs);
            foreach (var field in typeInfo.Fields)
            {
                SerializationManager.SerializeInner(field.GetValue(item), context, field.FieldType);
            }

            typeInfo.OnSerialized?.Invoke(item, callbackArgs);
        }

        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            var typeInfo = GetSerializationMetadata(expectedType);
            var callbackArgs = new object[] { new StreamingContext(StreamingContextStates.All, context) };

            var result = FormatterServices.GetUninitializedObject(expectedType);
            context.RecordObject(result);
            typeInfo.OnDeserializing?.Invoke(result, callbackArgs);

            foreach (var field in typeInfo.Fields)
            {
                field.SetValue(result, SerializationManager.DeserializeInner(field.FieldType, context));
            }

            typeInfo.OnDeserialized?.Invoke(result, callbackArgs);
            return result;
        }

        /// <summary>
        /// Returns a value indicating whether the provided type is supported as a field by this class.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A value indicating whether the provided type is supported as a field by this class.</returns>
        private static bool IsSupportedFieldType(Type type)
        {
            if (type.IsPointer || type.IsByRef) return false;

            var handle = type.TypeHandle;
            if (handle.Equals(IntPtrTypeHandle)) return false;
            if (handle.Equals(UIntPtrTypeHandle)) return false;
            if (DelegateType.IsAssignableFrom(type)) return false;

            return true;
        }

        private static TypeSerializationMetadata GetSerializationMetadata(Type type)
        {
            return TypeMetadata.GetOrAdd(type, t => CreateSerializationMetadata(t));

            static TypeSerializationMetadata CreateSerializationMetadata(Type type)
            {
                MethodInfo onDeserializing = null;
                MethodInfo onDeserialized = null;
                MethodInfo onSerializing = null;
                MethodInfo onSerialized = null;

                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length != 1) continue;
                    if (parameters[0].ParameterType != typeof(StreamingContext)) continue;

                    if (method.GetCustomAttribute<OnDeserializingAttribute>() != null)
                    {
                        onDeserializing = method;
                    }

                    if (method.GetCustomAttribute<OnDeserializedAttribute>() != null)
                    {
                        onDeserialized = method;
                    }

                    if (method.GetCustomAttribute<OnSerializingAttribute>() != null)
                    {
                        onSerializing = method;
                    }

                    if (method.GetCustomAttribute<OnSerializedAttribute>() != null)
                    {
                        onSerialized = method;
                    }
                }

                var fields =
                    type.GetAllFields()
                        .Where(field => !field.IsStatic && IsFieldSerialized(field) && IsSupportedFieldType(field.FieldType))
                        .ToList();
                fields.Sort(FieldInfoComparer.Instance);

                var result = new TypeSerializationMetadata
                {
                    Fields = fields,
                    OnDeserializing = onDeserializing,
                    OnDeserialized = onDeserialized,
                    OnSerializing = onSerializing,
                    OnSerialized = onSerialized,
                };

                return result;

                static bool IsFieldSerialized(FieldInfo field) => (field.Attributes & FieldAttributes.NotSerialized) != FieldAttributes.NotSerialized;
            }
        }

        private class TypeSerializationMetadata
        {
            public IReadOnlyList<FieldInfo> Fields { get; init; }
            public MethodInfo OnDeserializing { get; init; }
            public MethodInfo OnDeserialized { get; init; }
            public MethodInfo OnSerializing { get; init; }
            public MethodInfo OnSerialized { get; init; }
        }

        /// <summary>
        /// A comparer for <see cref="FieldInfo"/> which compares by name.
        /// </summary>
        private class FieldInfoComparer : IComparer<FieldInfo>
        {
            /// <summary>
            /// Gets the singleton instance of this class.
            /// </summary>
            public static FieldInfoComparer Instance { get; } = new FieldInfoComparer();

            public int Compare(FieldInfo x, FieldInfo y)
            {
                return string.Compare(x.Name, y.Name, StringComparison.Ordinal);
            }
        }
    }
}
