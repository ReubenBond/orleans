using System;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Activators
{
    internal sealed class DefaultActivator<[DynamicallyAccessedMembers(PublicConstructors | PublicParameterlessConstructor | NonPublicConstructors)] T> : IActivator<T> where T : class
    {
        private static readonly Func<T> DefaultConstructorFunction = Init();
        private readonly Func<T> _constructor = DefaultConstructorFunction;

        [DynamicallyAccessedMembers(PublicConstructors | PublicParameterlessConstructor | NonPublicConstructors)]
        private static readonly Type Type = typeof(T);

        private static Func<T> Init()
        {
            if (RuntimeFeature.IsDynamicCodeSupported)
            {
                var ctor = Type.GetConstructor(Type.EmptyTypes);
                if (ctor is not null)
                {
                    var method = new DynamicMethod(nameof(DefaultActivator<T>), typeof(T), new[] { typeof(object) });
                    var il = method.GetILGenerator();
                    il.Emit(OpCodes.Newobj, ctor);
                    il.Emit(OpCodes.Ret);
                    return (Func<T>)method.CreateDelegate(typeof(Func<T>));
                }
            }

            return () => Unsafe.As<T>(RuntimeHelpers.GetUninitializedObject(Type));
        }

        public T Create() => _constructor();
    }
}