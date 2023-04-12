using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;

namespace Orleans.Serialization
{
    /// <summary>
    /// Creates delegates for calling ISerializable-conformant constructors.
    /// </summary>
    internal sealed class SerializationConstructorFactory
    {
        private static readonly Type[] SerializationConstructorParameterTypes = { typeof(SerializationInfo), typeof(StreamingContext) };
        private static readonly Func<Type, object> CreateConstructorDelegate = static t => GetSerializationConstructorInvoker(t, typeof(object), typeof(Action<object, SerializationInfo, StreamingContext>));
        private readonly ConcurrentDictionary<Type, object> _constructors = new();

        public delegate void ValueSerializationConstructor<T>(ref T value, SerializationInfo info, StreamingContext context);

        /// <summary>
        /// Determines whether the provided type has a serialization constructor.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true" /> if the provided type has a serialization constructor; otherwise, <see langword="false" />.</returns>
        public static bool HasSerializationConstructor(
            [DynamicallyAccessedMembers(PublicConstructors | NonPublicConstructors)]
            Type type) => GetSerializationConstructor(type) != null;

        public Action<object, SerializationInfo, StreamingContext> GetSerializationConstructorDelegate(Type type)
            => (Action<object, SerializationInfo, StreamingContext>)_constructors.GetOrAdd(type, CreateConstructorDelegate);

        public ValueSerializationConstructor<T> GetValueTypeSerializationConstructorDelegate<[DynamicallyAccessedMembers(PublicConstructors | NonPublicConstructors)] T>()
        {
            if (_constructors.TryGetValue(typeof(T), out var existing))
            {
                return (ValueSerializationConstructor<T>)existing;
            }

            var constructor = GetSerializationConstructor(typeof(T));
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                void CallConstructor(ref T instance, SerializationInfo info, StreamingContext context)
                {
                    var boxed = (object)instance;
                    constructor.Invoke(boxed, new object[] { info, context });
                    instance = (T)boxed;
                }

                var callConstructor = CallConstructor;
                return (ValueSerializationConstructor<T>)_constructors.GetOrAdd(typeof(T), callConstructor);
            }
            else
            {

                Type[] parameterTypes = new[] { typeof(object), typeof(T).MakeByRefType(), typeof(SerializationInfo), typeof(StreamingContext) };

                var method = new DynamicMethod($"{typeof(T)}_serialization_ctor", null, parameterTypes, typeof(T), skipVisibility: true);
                var il = method.GetILGenerator();

                // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
                il.Emit(OpCodes.Ldarg_1);

                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, constructor);
                il.Emit(OpCodes.Ret);

                var callConstructor = method.CreateDelegate(typeof(ValueSerializationConstructor<T>));
                return (ValueSerializationConstructor<T>)_constructors.GetOrAdd(typeof(T), callConstructor);
            }
        }

        private object GetSerializationConstructorDelegate(
            [DynamicallyAccessedMembers(PublicConstructors | NonPublicConstructors)]
            Type owner, Type delegateType)
#pragma warning disable IL2067 // Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The parameter of method does not have matching annotations.
            => _constructors.GetOrAdd(owner, static (t, d) => GetSerializationConstructorInvoker(t, t, d), delegateType);
#pragma warning restore IL2067 // Target parameter argument does not satisfy 'DynamicallyAccessedMembersAttribute' in call to target method. The parameter of method does not have matching annotations.

        private static ConstructorInfo GetSerializationConstructor(
            [DynamicallyAccessedMembers(PublicConstructors | NonPublicConstructors)]
            Type type) => type.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                SerializationConstructorParameterTypes,
                null);

        private static Delegate GetSerializationConstructorInvoker([DynamicallyAccessedMembers(PublicConstructors | NonPublicConstructors)] Type type, Type owner, Type delegateType)
        {
            var constructor = GetSerializationConstructor(type) ?? (typeof(Exception).IsAssignableFrom(type) ? GetSerializationConstructor(typeof(Exception)) : null);
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                void CallConstructor(object instance, SerializationInfo info, StreamingContext context) => constructor.Invoke(instance, new object[] { info, context });
                return CallConstructor;
            }

            if (constructor is null)
            {
                throw new SerializationException($"{nameof(ISerializable)} constructor not found on type {type}.");
            }

            Type[] parameterTypes;
            if (owner.IsValueType)
            {
                parameterTypes = new[] { typeof(object), owner.MakeByRefType(), typeof(SerializationInfo), typeof(StreamingContext) };
            }
            else
            {
                parameterTypes = new[] { typeof(object), typeof(object), typeof(SerializationInfo), typeof(StreamingContext) };
            }

            var method = new DynamicMethod($"{type}_serialization_ctor", null, parameterTypes, type, skipVisibility: true);
            var il = method.GetILGenerator();

            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            il.Emit(OpCodes.Ldarg_1);
            if (type != owner)
            {
                il.Emit(OpCodes.Castclass, type);
            }

            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, constructor);
            il.Emit(OpCodes.Ret);

            return method.CreateDelegate(delegateType);
        }
    }
}