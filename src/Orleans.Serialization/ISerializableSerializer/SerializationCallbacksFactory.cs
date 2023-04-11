using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    /// <summary>
    /// Creates delegates for calling methods marked with serialization attributes.
    /// </summary>
    internal sealed class SerializationCallbacksFactory
    {
        private static readonly Func<Type, object> CreateReferenceTypeCallbacksDelegate = CreateReferenceTypeCallbacks;
        private readonly ConcurrentDictionary<Type, object> _cache = new();

        public delegate void ValueTypeSerializationCallback<T>(ref T value, StreamingContext context);

        /// <summary>
        /// Gets serialization callbacks for reference types.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>Serialization callbacks.</returns>
        public SerializationCallbacks<Action<object, StreamingContext>> GetReferenceTypeCallbacks(Type type) => (
            SerializationCallbacks<Action<object, StreamingContext>>)_cache.GetOrAdd(type, CreateReferenceTypeCallbacksDelegate);

        /// <summary>
        /// Gets serialization callbacks for value types.
        /// </summary>
        /// <typeparam name="T">The declaring type.</typeparam>
        /// <returns>Serialization callbacks.</returns>
        public SerializationCallbacks<ValueTypeSerializationCallback<T>> GetValueTypeCallbacks<T>() where T : struct 
            => (SerializationCallbacks<ValueTypeSerializationCallback<T>>)_cache.GetOrAdd(typeof(T), static (_) => CreateValueTypeCallbacks<T>());

        private static SerializationCallbacks<ValueTypeSerializationCallback<T>> CreateValueTypeCallbacks<[DynamicallyAccessedMembers(PublicMethods | NonPublicMethods)] T>()
        {
            var methods = GetCallbackMethods(typeof(T));
            ValueTypeSerializationCallback<T> onDeserializing;
            ValueTypeSerializationCallback<T> onDeserialized;
            ValueTypeSerializationCallback<T> onSerializing;
            ValueTypeSerializationCallback<T> onSerialized;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                onDeserializing = (ValueTypeSerializationCallback<T>)GetSerializationMethod(typeof(T), methods.OnDeserializing, typeof(T)).CreateDelegate(typeof(ValueTypeSerializationCallback<T>));
                onDeserialized = (ValueTypeSerializationCallback<T>)GetSerializationMethod(typeof(T), methods.OnDeserialized, typeof(T)).CreateDelegate(typeof(ValueTypeSerializationCallback<T>));
                onSerializing = (ValueTypeSerializationCallback<T>)GetSerializationMethod(typeof(T), methods.OnSerializing, typeof(T)).CreateDelegate(typeof(ValueTypeSerializationCallback<T>));
                onSerialized = (ValueTypeSerializationCallback<T>)GetSerializationMethod(typeof(T), methods.OnSerialized, typeof(T)).CreateDelegate(typeof(ValueTypeSerializationCallback<T>));
            }
            else
            {
                void OnDeserializing(ref T self, StreamingContext context)
                {
                    var boxed = (object)self;
                    methods.OnDeserializing.Invoke(boxed, new object[] { context });
                    self = (T)boxed;
                }

                onDeserializing = OnDeserializing;
                void OnDeserialized(ref T self, StreamingContext context)
                {
                    var boxed = (object)self;
                    methods.OnDeserialized.Invoke(boxed, new object[] { context });
                    self = (T)boxed;
                }

                onDeserialized = OnDeserialized;
                void OnSerializing(ref T self, StreamingContext context)
                {
                    var boxed = (object)self;
                    methods.OnSerializing.Invoke(boxed, new object[] { context });
                    self = (T)boxed;
                }

                onSerializing = OnSerializing;
                void OnSerialized(ref T self, StreamingContext context)
                {
                    var boxed = (object)self;
                    methods.OnSerialized.Invoke(boxed, new object[] { context });
                    self = (T)boxed;
                }

                onSerialized = OnSerialized;
            }

            return new SerializationCallbacks<ValueTypeSerializationCallback<T>>(onDeserializing, onDeserialized, onSerializing, onSerialized);
        }

        private static SerializationCallbacks<Action<object, StreamingContext>> CreateReferenceTypeCallbacks(Type type)
        {
            var methods = GetCallbackMethods(type);
            Action<object, StreamingContext> onDeserializing;
            Action<object, StreamingContext> onDeserialized;
            Action<object, StreamingContext> onSerializing;
            Action<object, StreamingContext> onSerialized;
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                onDeserializing = (Action<object, StreamingContext>)GetSerializationMethod(type, methods.OnDeserializing, typeof(object)).CreateDelegate(typeof(Action<object, StreamingContext>));
                onDeserialized = (Action<object, StreamingContext>)GetSerializationMethod(type, methods.OnDeserialized, typeof(object)).CreateDelegate(typeof(Action<object, StreamingContext>));
                onSerializing = (Action<object, StreamingContext>)GetSerializationMethod(type, methods.OnSerializing, typeof(object)).CreateDelegate(typeof(Action<object, StreamingContext>));
                onSerialized = (Action<object, StreamingContext>)GetSerializationMethod(type, methods.OnSerialized, typeof(object)).CreateDelegate(typeof(Action<object, StreamingContext>));
            }
            else
            {
                void OnDeserializing(object self, StreamingContext context) => methods.OnDeserializing.Invoke(self, new object[] { context });
                void OnDeserialized(object self, StreamingContext context) => methods.OnDeserialized.Invoke(self, new object[] { context });
                void OnSerializing(object self, StreamingContext context) => methods.OnSerializing.Invoke(self, new object[] { context });
                void OnSerialized(object self, StreamingContext context) => methods.OnSerialized.Invoke(self, new object[] { context });

                onDeserializing = OnDeserializing;
                onDeserialized = OnDeserialized;
                onSerializing = OnSerializing;
                onSerialized = OnSerialized;
            }

            return new SerializationCallbacks<Action<object, StreamingContext>>(onDeserializing, onDeserialized, onSerializing, onSerialized);
        }

        private static (MethodInfo OnDeserializing, MethodInfo OnDeserialized, MethodInfo OnSerializing, MethodInfo OnSerialized) GetCallbackMethods([DynamicallyAccessedMembers(PublicMethods | NonPublicMethods)] Type type)
        {
            var onDeserializing = default(MethodInfo);
            var onDeserialized = default(MethodInfo);
            var onSerializing = default(MethodInfo);
            var onSerialized = default(MethodInfo);
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }

                if (parameters[0].ParameterType != typeof(StreamingContext))
                {
                    continue;
                }

                if (method.IsDefined(typeof(OnDeserializingAttribute), false))
                {
                    onDeserializing = method;
                }

                if (method.IsDefined(typeof(OnDeserializedAttribute), false))
                {
                    onDeserialized = method;
                }

                if (method.IsDefined(typeof(OnSerializingAttribute), false))
                {
                    onSerializing = method;
                }

                if (method.IsDefined(typeof(OnSerializedAttribute), false))
                {
                    onSerialized = method;
                }
            }

            return (onDeserializing, onDeserialized, onSerializing, onSerialized);
        }

        private static DynamicMethod GetSerializationMethod(Type type, MethodInfo callbackMethod, Type owner)
        {
            Type[] callbackParameterTypes;
            if (owner.IsValueType)
            {
                callbackParameterTypes = new[] { typeof(object), owner.MakeByRefType(), typeof(StreamingContext) };
            }
            else
            {
                callbackParameterTypes = new[] { typeof(object), typeof(object), typeof(StreamingContext) };
            }

            var method = new DynamicMethod($"{callbackMethod.Name}_Trampoline", null, callbackParameterTypes, type, skipVisibility: true);
            var il = method.GetILGenerator();

            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            il.Emit(OpCodes.Ldarg_1);
            if (type != owner)
            {
                il.Emit(OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, callbackMethod);
            il.Emit(OpCodes.Ret);

            return method;
        }

        /// <summary>
        /// Serialization callbacks.
        /// </summary>
        /// <typeparam name="TDelegate">The delegate type for each callback.</typeparam>
        public sealed class SerializationCallbacks<TDelegate>
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SerializationCallbacks{TDelegate}"/> class.
            /// </summary>
            /// <param name="onDeserializing">The callback invoked during deserialization.</param>
            /// <param name="onDeserialized">The callback invoked once a value is deserialized.</param>
            /// <param name="onSerializing">The callback invoked during serialization.</param>
            /// <param name="onSerialized">The callback invoked once a value is serialized.</param>
            public SerializationCallbacks(
                TDelegate onDeserializing,
                TDelegate onDeserialized,
                TDelegate onSerializing,
                TDelegate onSerialized)
            {
                OnDeserializing = onDeserializing;
                OnDeserialized = onDeserialized;
                OnSerializing = onSerializing;
                OnSerialized = onSerialized;
            }

            /// <summary>
            /// Gets the callback invoked while deserializing.
            /// </summary>
            public readonly TDelegate OnDeserializing;

            /// <summary>
            /// Gets the callback invoked once a value has been deserialized.
            /// </summary>
            public readonly TDelegate OnDeserialized;

            /// <summary>
            /// Gets the callback invoked during serialization.
            /// </summary>
            /// <value>The on serializing.</value>
            public readonly TDelegate OnSerializing;

            /// <summary>
            /// Gets the callback invoked once a value has been serialized.
            /// </summary>
            public readonly TDelegate OnSerialized;
        }
    }
}