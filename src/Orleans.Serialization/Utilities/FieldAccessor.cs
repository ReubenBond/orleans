using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;

namespace Orleans.Serialization.Utilities
{
    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    public delegate TField ValueTypeGetter<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
        TDeclaring, out TField>(ref TDeclaring instance) where TDeclaring : struct;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ValueTypeSetter<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
        TDeclaring, in TField>(ref TDeclaring instance, TField value) where TDeclaring : struct;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    public delegate TField ReferenceTypeGetter<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
        TDeclaring, out TField>(TDeclaring instance) where TDeclaring : class;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ReferenceTypeSetter<
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
        TDeclaring, in TField>(TDeclaring instance, TField value) where TDeclaring : class;

    /// <summary>
    /// Functionality for accessing fields.
    /// </summary>
    public static class FieldAccessor
    {
        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static TDelegate GetGetter<TDelegate>(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
            Type declaringType,
            string fieldName) where TDelegate : Delegate => GetGetter<TDelegate>(declaringType, fieldName, false);

        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static TDelegate GetValueGetter<TDelegate>(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
            Type declaringType, string fieldName) where TDelegate : Delegate => GetGetter<TDelegate>(declaringType, fieldName, true);

        private static TDelegate GetGetter<TDelegate>(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
           Type declaringType, string fieldName, bool byRef) where TDelegate : Delegate
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var parameterTypes = new[] { typeof(object), byRef ? declaringType.MakeByRefType() : declaringType };

            var method = new DynamicMethod(fieldName + "Get", field.FieldType, parameterTypes, typeof(FieldAccessor).Module, true);

            var emitter = method.GetILGenerator();
            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldfld, field);
            emitter.Emit(OpCodes.Ret);

            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static TDelegate GetReferenceSetter<TDelegate>(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
            Type declaringType,
            string fieldName) where TDelegate : Delegate => GetSetter<TDelegate>(declaringType, fieldName, false);

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static TDelegate GetValueSetter<TDelegate>(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
            Type declaringType,
        string fieldName) where TDelegate : Delegate => GetSetter<TDelegate>(declaringType, fieldName, true);

        private static TDelegate GetSetter<TDelegate>(
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicFields)]
#endif
            Type declaringType,
            string fieldName,
            bool byRef) where TDelegate : Delegate
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var parameterTypes = new[] { typeof(object), byRef ? declaringType.MakeByRefType() : declaringType, field.FieldType };

            var method = new DynamicMethod(fieldName + "Set", null, parameterTypes, typeof(FieldAccessor).Module, true);

            var emitter = method.GetILGenerator();
            // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
            emitter.Emit(OpCodes.Ldarg_1);
            emitter.Emit(OpCodes.Ldarg_2);
            emitter.Emit(OpCodes.Stfld, field);
            emitter.Emit(OpCodes.Ret);

            return (TDelegate)method.CreateDelegate(typeof(TDelegate));
        }
    }
}