using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Activators
{
    internal sealed class DefaultActivator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T> : IActivator<T> where T : class
    {
        private static readonly Func<T> DefaultConstructorFunction = Init();
        private readonly Func<T> _constructor = DefaultConstructorFunction;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        private readonly Type _type = typeof(T);

        private static Func<T> Init()
        {
            var ctor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (ctor is null)
                return null;

            var method = new DynamicMethod(nameof(DefaultActivator<T>), typeof(T), new[] { typeof(object) });
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);
            return (Func<T>)method.CreateDelegate(typeof(Func<T>));
        }

        public T Create() => _constructor is { } ctor ? ctor() : Unsafe.As<T>(RuntimeHelpers.GetUninitializedObject(_type));
    }
}