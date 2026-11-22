using System.Reflection;
using System.Reflection.Emit;

namespace Orleans.Testing.Simulation.Shims.System.Net.Http;

/// <summary>
/// Builds a delegate which calls a virtual instance method without virtual dispatch, as an override's base call does. A
/// shim which replaces a base call must run the base implementation, and C# can call a virtual method non-virtually only
/// through <c>base.</c> inside a derived type, so the delegate is a dynamic method whose body is the <c>call</c>.
/// </summary>
internal static class NonVirtualCall
{
    /// <summary>Creates the delegate, whose first parameter is the receiver.</summary>
    /// <typeparam name="TDelegate">The delegate type: the receiver, then the method's parameters.</typeparam>
    /// <param name="type">The type which declares or overrides the method.</param>
    /// <param name="name">The method's name.</param>
    /// <param name="parameters">The method's parameter types.</param>
    /// <returns>The delegate.</returns>
    public static TDelegate Create<TDelegate>(Type type, string name, params Type[] parameters)
        where TDelegate : Delegate
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, parameters)
            ?? throw new MissingMethodException(type.FullName, name);
        var call = new DynamicMethod(
            "Base" + name,
            method.ReturnType,
            [type, .. parameters],
            typeof(NonVirtualCall).Module,
            skipVisibility: true);
        ILGenerator il = call.GetILGenerator();
        for (int index = 0; index <= parameters.Length; index++)
        {
            il.Emit(OpCodes.Ldarg, index);
        }

        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        return call.CreateDelegate<TDelegate>();
    }
}
