using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Utilities
{
    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    public delegate TField ValueTypeGetter<[DynamicallyAccessedMembers(NonPublicFields | PublicFields)] TDeclaring, out TField>(ref TDeclaring instance) where TDeclaring : struct;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ValueTypeSetter<[DynamicallyAccessedMembers(NonPublicFields | PublicFields)] TDeclaring, in TField>(ref TDeclaring instance, TField value) where TDeclaring : struct;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    public delegate TField ReferenceTypeGetter<[DynamicallyAccessedMembers(NonPublicFields | PublicFields)] TDeclaring, out TField>(TDeclaring instance) where TDeclaring : class;

    /// <summary>
    /// The delegate used to set fields in value types.
    /// </summary>
    /// <typeparam name="TDeclaring">The declaring type of the field.</typeparam>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="instance">The instance having its field set.</param>
    /// <param name="value">The value being set.</param>
    public delegate void ReferenceTypeSetter<[DynamicallyAccessedMembers(NonPublicFields | PublicFields)] TDeclaring, in TField>(TDeclaring instance, TField value) where TDeclaring : class;

    /// <summary>
    /// Functionality for accessing fields.
    /// </summary>
    public static class FieldAccessor
    {
        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static ReferenceTypeGetter<TDeclaring, TField> GetReferenceGetter<TDeclaring, TField>(string fieldName) where TDeclaring : class
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                TField GetValue(TDeclaring instance) => (TField)field.GetValue(instance);
                return GetValue;
            }
            else
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var parameterTypes = new[] { typeof(object), typeof(TDeclaring) };

                var method = new DynamicMethod(fieldName + "Get", field.FieldType, parameterTypes, typeof(FieldAccessor).Module, true);

                var emitter = method.GetILGenerator();
                // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
                emitter.Emit(OpCodes.Ldarg_1);
                emitter.Emit(OpCodes.Ldfld, field);
                emitter.Emit(OpCodes.Ret);

                return (ReferenceTypeGetter<TDeclaring, TField>)method.CreateDelegate(typeof(ReferenceTypeGetter<TDeclaring, TField>));
            }
        }

        /// <summary>
        /// Returns a delegate to get the value of a specified field.
        /// </summary>
        /// <returns>A delegate to get the value of a specified field.</returns>
        public static ValueTypeGetter<TDeclaring, TField> GetValueGetter<TDeclaring, TField>(string fieldName) where TDeclaring : struct
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                TField GetValue(ref TDeclaring instance) => (TField)field.GetValue(instance);
                return GetValue;
            }
            else
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var parameterTypes = new[] { typeof(object), typeof(TDeclaring).MakeByRefType() };

                var method = new DynamicMethod(fieldName + "Get", field.FieldType, parameterTypes, typeof(FieldAccessor).Module, true);

                var emitter = method.GetILGenerator();
                // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
                emitter.Emit(OpCodes.Ldarg_1);
                emitter.Emit(OpCodes.Ldfld, field);
                emitter.Emit(OpCodes.Ret);

                return (ValueTypeGetter<TDeclaring, TField>)method.CreateDelegate(typeof(ValueTypeGetter<TDeclaring, TField>));
            }
        }

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static ReferenceTypeSetter<TDeclaring, TField> GetReferenceSetter<TDeclaring, TField>(string fieldName) where TDeclaring : class
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                void SetValue(TDeclaring instance, TField value) => field.SetValue(instance, value); 
                return SetValue;
            }
            else
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var parameterTypes = new[] { typeof(object), typeof(TDeclaring), field.FieldType };

                var method = new DynamicMethod(fieldName + "Set", null, parameterTypes, typeof(FieldAccessor).Module, true);

                var emitter = method.GetILGenerator();
                // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
                emitter.Emit(OpCodes.Ldarg_1);
                emitter.Emit(OpCodes.Ldarg_2);
                emitter.Emit(OpCodes.Stfld, field);
                emitter.Emit(OpCodes.Ret);

                return (ReferenceTypeSetter<TDeclaring, TField>)method.CreateDelegate(typeof(ReferenceTypeSetter<TDeclaring, TField>));
            }
        }

        /// <summary>
        /// Returns a delegate to set the value of this field for an instance.
        /// </summary>
        /// <returns>A delegate to set the value of this field for an instance.</returns>
        public static ValueTypeSetter<TDeclaring, TField> GetValueSetter<TDeclaring, TField>(string fieldName) where TDeclaring : struct
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                void SetValue(ref TDeclaring instance, TField value) => field.SetValueDirect(__makeref(instance), value);
                return SetValue;
            }
            else
            {
                var field = typeof(TDeclaring).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var parameterTypes = new[] { typeof(object), typeof(TDeclaring).MakeByRefType(), field.FieldType };

                var method = new DynamicMethod(fieldName + "Set", null, parameterTypes, typeof(FieldAccessor).Module, true);

                var emitter = method.GetILGenerator();
                // arg0 is unused for better delegate performance (avoids argument shuffling thunk)
                emitter.Emit(OpCodes.Ldarg_1);
                emitter.Emit(OpCodes.Ldarg_2);
                emitter.Emit(OpCodes.Stfld, field);
                emitter.Emit(OpCodes.Ret);

                return (ValueTypeSetter<TDeclaring, TField>)method.CreateDelegate(typeof(ValueTypeSetter<TDeclaring, TField>));
            }
        }
    }
}