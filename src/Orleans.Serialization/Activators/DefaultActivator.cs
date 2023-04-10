using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Activators
{
    internal sealed class DefaultActivator<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
        T> : IActivator<T> where T : class
    {
        private static readonly Func<T> DefaultConstructorFunction = Init();
        private readonly Func<T> _constructor = DefaultConstructorFunction;

#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
#endif
        private static readonly Type Type = typeof(T);

        private static Func<T> Init()
        {
            var ctor = Type.GetConstructor(Type.EmptyTypes);
            if (ctor is null)
                return null;

            var method = new DynamicMethod(nameof(DefaultActivator<T>), typeof(T), new[] { typeof(object) });
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);
            return (Func<T>)method.CreateDelegate(typeof(Func<T>));
        }

        public T Create() => _constructor is { } ctor ? ctor() : Unsafe.As<T>(RuntimeHelpers.GetUninitializedObject(Type));
    }
}